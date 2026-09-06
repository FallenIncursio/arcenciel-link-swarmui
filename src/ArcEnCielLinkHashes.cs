using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using SwarmUI.Core;
using SwarmUI.Utils;

namespace ArcEnCiel.Link.Swarm;

internal sealed class ArcEnCielLinkHashes
{
    private readonly object _lock = new();
    private Dictionary<string, HashEntry> _cache = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private bool _loaded;
    public bool InventoryComplete { get; private set; }
    public List<(string Path, string Hash, long Mtime, long Size)> CachedFiles()
    {
        lock (_lock) { EnsureLoaded(); return _cache.Select(p => (p.Key, p.Value.Hash, p.Value.MTime, p.Value.Size)).ToList(); }
    }

    private static string CachePath => Path.Combine(Program.DataDir, "Extensions", "ArcEnCielLink", "hashes.json");

    public IReadOnlyDictionary<string, string> GetModelFilesByHash()
    {
        ListModelHashes();
        Dictionary<string, string> map = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        lock (_lock)
        {
            foreach (KeyValuePair<string, HashEntry> entry in _cache)
            {
                if (!File.Exists(entry.Key))
                {
                    continue;
                }
                map[entry.Value.Hash] = entry.Key;
            }
        }
        return map;
    }

    public List<string> ListModelHashes()
    {
        lock (_lock)
        {
            EnsureLoaded();
            bool updated = false;
            InventoryComplete = true;
            List<string> hashes = [];

            HashSet<string> seenPaths = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (string root in ArcEnCielLinkPaths.GetModelRoots())
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }
                try
                {
                    foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    {
                        if (!ArcEnCielLinkPaths.IsModelFile(file))
                        {
                            continue;
                        }

                        seenPaths.Add(file);
                        FileInfo before = new(file);
                        long mtime = before.LastWriteTimeUtc.Ticks, size = before.Length;
                        if (_cache.TryGetValue(file, out HashEntry? entry) && entry.MTime == mtime && entry.Size == size && !string.IsNullOrWhiteSpace(entry.Hash))
                        {
                            hashes.Add(entry.Hash);
                            continue;
                        }

                        string hash = ComputeSha256(file);
                        FileInfo after = new(file);
                        if (after.LastWriteTimeUtc.Ticks != mtime || after.Length != size) throw new IOException("Model changed during inventory scan");
                        _cache[file] = new HashEntry { MTime = mtime, Size = size, Hash = hash };
                        hashes.Add(hash);
                        updated = true;
                    }
                }
                catch (Exception ex)
                {
                    InventoryComplete = false;
                    Logs.Error($"[AEC-LINK] Failed to scan '{root}': {ex.Message}");
                }
            }

            string[] knownPaths = _cache.Keys.ToArray();
            foreach (string path in knownPaths)
            {
                if (!seenPaths.Contains(path) && !File.Exists(path))
                {
                    _cache.Remove(path);
                    updated = true;
                }
            }

            if (updated)
            {
                Save();
            }

            return hashes;
        }
    }

    public List<string> UpdateCachedHash(string path, string hash)
    {
        lock (_lock)
        {
            EnsureLoaded();
            long mtime = File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0;
            _cache[path] = new HashEntry { MTime = mtime, Size = File.Exists(path) ? new FileInfo(path).Length : -1, Hash = hash };
            Save();
            return _cache.Values.Select(v => v.Hash).Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        if (File.Exists(CachePath))
        {
            try
            {
                string json = File.ReadAllText(CachePath);
                Dictionary<string, HashEntry>? data = JsonSerializer.Deserialize<Dictionary<string, HashEntry>>(json);
                if (data is not null)
                {
                    _cache = new Dictionary<string, HashEntry>(data, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                }
            }
            catch (Exception ex)
            {
                Logs.Error($"[AEC-LINK] Failed to load hash cache: {ex.Message}");
            }
        }

        _loaded = true;
    }

    private void Save()
    {
        try
        {
            string? dir = Path.GetDirectoryName(CachePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }
            string json = JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(CachePath, json);
        }
        catch (Exception ex)
        {
            Logs.Error($"[AEC-LINK] Failed to save hash cache: {ex.Message}");
        }
    }

    private static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using SHA256 sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed class HashEntry
    {
        public long MTime { get; set; }
        public long Size { get; set; } = -1;
        public string Hash { get; set; } = "";
    }
}
