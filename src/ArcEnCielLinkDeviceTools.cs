using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;

namespace ArcEnCiel.Link.Swarm;

internal sealed class ArcEnCielLinkDeviceTools
{
    public const string Capability = "device_tools_v1";
    private readonly object _gate = new();
    private CancellationTokenSource? _stop;
    private JObject? _state;
    private DateTimeOffset _lastAck;
    private readonly SemaphoreSlim _reportGate = new(1, 1);
    private readonly ConcurrentDictionary<string, (long Mtime, long Size, string Hash)> _cache = new();
    public List<(string Path, string Hash, long Mtime, long Size)> CachedFiles() => _cache.Select(p => (p.Key, p.Value.Hash, p.Value.Mtime, p.Value.Size)).ToList();
    public JObject? Status { get { lock (_gate) return _state is null ? null : (JObject)_state.DeepClone(); } }
    private bool _running;
    private bool _finishing;

    public bool Cancel(JObject msg, string runtimeId)
    {
        lock (_gate) {
            if (!_running || msg.Value<string>("runtimeId") != runtimeId || msg.Value<string>("taskId") != _state?.Value<string>("taskId")) return false;
            _stop?.Cancel(); return true;
        }
    }
    private void Set(string key, JToken? value) { lock (_gate) _state![key] = value; }
    private void Increment(string key) { lock (_gate) _state![key] = (_state.Value<int?>(key) ?? 0) + 1; }

    public bool Start(JObject message, string runtimeId, Func<IEnumerable<string>> roots,
        Func<string, JObject, CancellationToken, Task<JObject>> post,
        Func<JObject, string, string, CancellationToken, Task<(bool Changed, bool Warning)>> repair,
        Action<List<string>> syncLocal, bool busy)
    {
        string? action = message.Value<string>("action"), id = message.Value<string>("taskId");
        if (!Guid.TryParse(id, out _) || message.Value<string>("runtimeId") != runtimeId || action is not ("rescan" or "repair_sidecars")) return false;
        lock (_gate) {
            if (_running) return false;
            _running = true; _finishing = false; _stop = new(); _lastAck = DateTimeOffset.UtcNow;
            _state = new JObject { ["taskId"] = id, ["runtimeId"] = runtimeId, ["action"] = action, ["state"] = "RUNNING", ["phase"] = "starting", ["processed"] = 0, ["total"] = 0, ["repaired"] = 0, ["skipped"] = 0, ["warnings"] = 0 };
        }
        _ = Task.Run(async () => {
            using CancellationTokenSource heartbeatStop = new();
            CancellationToken token = _stop.Token;
            async Task<bool> Report(bool heartbeat = false) {
                await _reportGate.WaitAsync(CancellationToken.None);
                try {
                    if (heartbeat && _finishing) return true;
                    if (DateTimeOffset.UtcNow - _lastAck > TimeSpan.FromSeconds(45)) { _stop.Cancel(); token.ThrowIfCancellationRequested(); }
                    await post("/tools/progress", Status!, token); _lastAck = DateTimeOffset.UtcNow; return true;
                } catch (OperationCanceledException) { _stop.Cancel(); throw; }
                catch { return false; }
                finally { _reportGate.Release(); }
            }
            Task heartbeat = Task.Run(async () => {
                try { while (true) { await Task.Delay(10000, heartbeatStop.Token); await Report(true); } }
                catch (OperationCanceledException) { }
            });
            try {
                if (busy) throw new InvalidOperationException("BUSY");
                if (!await Report()) throw new InvalidOperationException("TOOL_FAILED");
                Set("phase", "scanning");
                List<(string Hash, string Path)> files;
                try { files = await Scan(roots(), token); }
                catch (OperationCanceledException) { throw; }
                catch { throw new InvalidOperationException("SCAN_FAILED"); }
                List<string> hashes = files.Select(f => f.Hash).Distinct().ToList();
                Set("phase", "syncing");
                try { await post("/inventory", new JObject { ["hashes"] = new JArray(hashes), ["runtimeId"] = runtimeId }, token); syncLocal(hashes); }
                catch (OperationCanceledException) { throw; }
                catch { throw new InvalidOperationException("INVENTORY_FAILED"); }
                if (action == "repair_sidecars") {
                    Set("phase", "repairing"); Set("total", files.Count * 2);
                    foreach (var batch in files.Chunk(100)) {
                        JObject metas;
                        try { metas = await post("/sidecars/meta", new JObject { ["hashes"] = new JArray(batch.Select(f => f.Hash).Distinct()) }, token); }
                        catch (OperationCanceledException) { throw; }
                        catch { throw new InvalidOperationException("METADATA_FAILED"); }
                        foreach (var file in batch) {
                            token.ThrowIfCancellationRequested();
                            if (metas[file.Hash] is not JObject meta) Increment("skipped");
                            else {
                                try { var result = await repair(meta, file.Hash, file.Path, token); Increment(result.Changed ? "repaired" : "skipped"); if (result.Warning) Increment("warnings"); }
                                catch (OperationCanceledException) { throw; }
                                catch { Increment("warnings"); }
                            }
                            Increment("processed");
                        }
                    }
                }
                _finishing = true; Set("state", "DONE"); Set("phase", "complete"); if (!await Report()) throw new InvalidOperationException("TOOL_FAILED");
            } catch (OperationCanceledException) { Set("state", "CANCELLED"); }
            catch (Exception ex) {
                _finishing = true; Set("state", "ERROR");
                Set("code", new[] { "BUSY", "SCAN_FAILED", "INVENTORY_FAILED", "METADATA_FAILED" }.Contains(ex.Message) ? ex.Message : "TOOL_FAILED");
                try { await Report(); } catch (OperationCanceledException) { }
            } finally {
                heartbeatStop.Cancel(); await heartbeat;
                lock (_gate) { _running = false; _stop.Dispose(); _stop = null; }
            }
        });
        return true;
    }

    private async Task<List<(string Hash, string Path)>> Scan(IEnumerable<string> roots, CancellationToken token)
    {
        HashSet<string> files = new(StringComparer.Ordinal);
        string[] extensions = [".safetensors", ".ckpt", ".pt", ".sft", ".gguf"];
        foreach (string root in roots.Distinct()) {
            if (!Directory.Exists(root)) { if (File.Exists(root)) throw new IOException(); continue; }
            Stack<string> pending = new(); pending.Push(root);
            while (pending.Count > 0) {
                token.ThrowIfCancellationRequested();
                string dir = pending.Pop();
                foreach (string child in Directory.EnumerateDirectories(dir)) if (!Path.GetFileName(child).StartsWith('.') && (File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push(child);
                foreach (string file in Directory.EnumerateFiles(dir)) if (extensions.Contains(Path.GetExtension(file).ToLowerInvariant()) && (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0) files.Add(Path.GetFullPath(file));
                if (files.Count > 10000) throw new IOException();
            }
        }
        Set("total", files.Count); Set("processed", 0);
        List<(string Hash, string Path)> result = [];
        foreach (string path in files.OrderBy(p => p, StringComparer.Ordinal)) {
            token.ThrowIfCancellationRequested();
            FileInfo before = new(path); long stamp = before.LastWriteTimeUtc.Ticks, size = before.Length;
            string digest;
            if (_cache.TryGetValue(path, out var cached) && cached.Mtime == stamp && cached.Size == size) digest = cached.Hash;
            else {
                await using FileStream stream = File.OpenRead(path);
                using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[1024 * 1024]; int n;
                while ((n = await stream.ReadAsync(buffer, token)) > 0) { token.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, n); }
                FileInfo after = new(path); if (after.LastWriteTimeUtc.Ticks != stamp || after.Length != size) throw new IOException();
                digest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(); _cache[path] = (stamp, size, digest);
            }
            result.Add((digest, path)); Increment("processed");
        }
        foreach (string old in _cache.Keys.Where(k => !files.Contains(k)).ToArray()) _cache.TryRemove(old, out _);
        return result;
    }
}
