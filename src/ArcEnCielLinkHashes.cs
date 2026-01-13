using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using SwarmUI.Core;
using SwarmUI.Utils;

namespace ArcEnCiel.Link.Swarm;

internal sealed class ArcEnCielLinkHashes
{
    private readonly object _lock = new();
    private Dictionary<string, HashEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    private static string CachePath => Path.Combine(Program.DataDir, "Extensions", "ArcEnCielLink", "hashes.json");

    public IReadOnlyDictionary<string, string> GetModelFilesByHash()
    {
        ListModelHashes();
        Dictionary<string, string> map = new(StringComparer.OrdinalIgnoreCase);
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
            List<string> hashes = [];

            HashSet<string> seenPaths = new(StringComparer.OrdinalIgnoreCase);
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
                        long mtime = File.GetLastWriteTimeUtc(file).Ticks;
                        if (_cache.TryGetValue(file, out HashEntry? entry) && entry.MTime == mtime && !string.IsNullOrWhiteSpace(entry.Hash))
                        {
                            hashes.Add(entry.Hash);
                            continue;
                        }

                        string hash = ComputeSha256(file);
                        _cache[file] = new HashEntry { MTime = mtime, Hash = hash };
                        hashes.Add(hash);
                        updated = true;
                    }
                }
                catch (Exception ex)
                {
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
            _cache[path] = new HashEntry { MTime = mtime, Hash = hash };
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
                    _cache = new Dictionary<string, HashEntry>(data, StringComparer.OrdinalIgnoreCase);
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
        public string Hash { get; set; } = "";
    }
}
