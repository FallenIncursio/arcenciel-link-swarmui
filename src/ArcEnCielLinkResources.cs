using System.IO;
using Newtonsoft.Json.Linq;
using SwarmUI.Core;

namespace ArcEnCiel.Link.Swarm;

internal static class ArcEnCielLinkResources
{
    public const string Capability = "resource_inventory_v1";
    private static int _refresh = 1;
    public static void RequestRefresh() => Interlocked.Exchange(ref _refresh, 1);
    public record Entry(string kind, string sha256, string selectionName, long sizeBytes, bool selectable);
    public record Inventory(int schemaVersion, bool complete, List<Entry> entries);
    private static readonly Dictionary<string, string> Kinds = new() { ["Stable-Diffusion"] = "checkpoint", ["LoRA"] = "lora", ["VAE"] = "vae", ["Embedding"] = "embedding" };

    public static bool HasVerifiedFile(string? digest, ArcEnCielLinkHashes hashes, ArcEnCielLinkDeviceTools tools)
    {
        if (string.IsNullOrEmpty(digest) || digest.Length != 64 || digest.Any(c => !char.IsAsciiHexDigit(c))) return false;
        foreach (var cached in hashes.CachedFiles())
        {
            if (!string.Equals(digest, cached.Hash, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                FileInfo file = new(cached.Path);
                if (file.Exists && file.LastWriteTimeUtc.Ticks == cached.Mtime && file.Length == cached.Size) return true;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return false;
    }

    public static Inventory Collect(ArcEnCielLinkHashes hashes, ArcEnCielLinkDeviceTools tools)
    {
        bool cacheChanged = ArcEnCielLinkHashCache.Shared.TakeCatalogChange();
        if (Interlocked.Exchange(ref _refresh, 0) == 1 || cacheChanged)
        {
            foreach (var pair in Kinds) if (Program.T2IModelSets.TryGetValue(pair.Key, out var handler)) handler.Refresh();
        }
        var cached = hashes.CachedFiles().ToList();
        var cachedByPath = cached.ToLookup(c => Path.GetFullPath(c.Path), OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        HashSet<string> used = new();
        bool complete = hashes.InventoryComplete || tools.Status?.Value<string>("state") == "DONE";
        List<Entry> entries = [];
        foreach (var pair in Kinds)
        {
            if (!Program.T2IModelSets.TryGetValue(pair.Key, out var handler)) { complete = false; continue; }
            Dictionary<string, (string Name, bool Selectable)> names = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var model in handler.Models.Values) names[Path.GetFullPath(model.RawFilePath)] = (model.Name.Replace('\\', '/'), true);
            // New local files also make an inventory incomplete until a hash scan finishes.
            foreach (string folder in handler.FolderPaths)
            {
                try
                {
                    if (!Directory.Exists(folder)) continue;
                    foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                    {
                        if (!ArcEnCielLinkPaths.IsModelFile(file)) continue;
                        string path = Path.GetFullPath(file);
                        if (!names.ContainsKey(path)) names[path] = (Path.GetRelativePath(folder, path).Replace('\\', '/'), false);
                    }
                }
                catch (IOException) { complete = false; }
                catch (UnauthorizedAccessException) { complete = false; }
            }
            foreach (var file in cached)
            {
                string path = Path.GetFullPath(file.Path);
                if (names.ContainsKey(path)) continue;
                foreach (string folder in handler.FolderPaths)
                {
                    string relative = Path.GetRelativePath(folder, path).Replace('\\', '/');
                    if (relative != ".." && !relative.StartsWith("../") && !Path.IsPathRooted(relative)) { names[path] = (relative, false); break; }
                }
            }
            foreach (var pairFile in names.OrderByDescending(p => p.Value.Selectable).ThenBy(p => p.Key, StringComparer.Ordinal))
            {
                try
                {
                    // A stale catalog/cache entry for a deleted file is not an incomplete scan.
                    // GetAttributes distinguishes absence from a denied/unreadable path.
                    if (File.GetAttributes(pairFile.Key).HasFlag(FileAttributes.Directory)) continue;
                    FileInfo info = new(pairFile.Key);
                    if (!info.Exists) { complete = false; continue; }
                    var known = cachedByPath[pairFile.Key].FirstOrDefault(c => c.Mtime == info.LastWriteTimeUtc.Ticks && c.Size == info.Length);
                    if (string.IsNullOrEmpty(known.Hash) || known.Hash.Length != 64 || known.Hash.Any(c => !char.IsAsciiHexDigit(c))) { complete = false; continue; }
                    if (!used.Add(pair.Value + ":" + pairFile.Value.Name)) { complete = false; continue; }
                    entries.Add(new(pair.Value, known.Hash.ToLowerInvariant(), pairFile.Value.Name, info.Length, pairFile.Value.Selectable));
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                catch (IOException) { complete = false; }
                catch (UnauthorizedAccessException) { complete = false; }
            }
        }
        if (entries.Count > 10000) { entries = entries.Take(10000).ToList(); complete = false; }
        return new(1, complete, entries);
    }
}
