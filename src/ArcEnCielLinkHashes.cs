using System.IO;
using SwarmUI.Utils;

namespace ArcEnCiel.Link.Swarm;

internal sealed class ArcEnCielLinkHashes
{
    public bool InventoryComplete { get; private set; }
    private readonly ArcEnCielLinkHashCache cache = ArcEnCielLinkHashCache.Shared;
    public List<(string Path, string Hash, long Mtime, long Size)> CachedFiles() => cache.Snapshot();
    public IReadOnlyDictionary<string, string> GetModelFilesByHash()
    {
        ListModelHashes();
        return CachedFiles().Where(p => File.Exists(p.Path)).GroupBy(p => p.Hash).ToDictionary(p => p.Key, p => p.First().Path);
    }
    public List<string> ListModelHashes()
    {
        InventoryComplete = true;
        HashSet<string> hashes = new();
        HashSet<string> paths = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        try
        {
            foreach (string root in ArcEnCielLinkPaths.GetModelRoots())
            {
                if (!Directory.Exists(root)) continue;
                try
                {
                    foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                        if (ArcEnCielLinkPaths.IsModelFile(file) && paths.Add(Path.GetFullPath(file))) hashes.Add(cache.Get(file));
                }
                catch (Exception ex) { InventoryComplete = false; Logs.Error($"[AEC-LINK] Inventory scan failed: {ex.GetType().Name}"); }
            }
        }
        finally { cache.Flush(); }
        return hashes.ToList();
    }
    public List<string> UpdateCachedHash(string path, string hash) => cache.RecordDownload(path, hash);
}
