using System.IO;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace ArcEnCiel.Link.Swarm;

// Cache reuse is metadata-based, not a new integrity verification. Samples only
// reject candidates; they never establish identity without the local file ID.
internal sealed class ArcEnCielLinkHashCache
{
    internal sealed class Entry
    {
        public long MTime { get; set; }
        public long Size { get; set; } = -1;
        public string Hash { get; set; } = "";
        public int Schema { get; set; }
        public string? Identity { get; set; }
        public string? Change { get; set; }
        public string? Sample { get; set; }
    }
    public static readonly ArcEnCielLinkHashCache Shared = new();
    private readonly object gate = new();
    private readonly StringComparer comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private Dictionary<string, Entry> entries = new();
    private string loadedPath = "";
    private bool dirty;
    private int catalogDirty;
    public bool TakeCatalogChange() => Interlocked.Exchange(ref catalogDirty, 0) == 1;
    private long lastSave;
    private static string CachePath => Path.Combine(SwarmUI.Core.Program.DataDir, "Extensions", "ArcEnCielLink", "hashes.json");
    internal int FullHashReads { get; private set; }
    internal string LastSource { get; private set; } = "";

    private void Load()
    {
        if (loadedPath == CachePath) return;
        entries = new(comparer);
        if (File.Exists(CachePath))
        {
            try { entries = new(JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(CachePath)) ?? new(), comparer); }
            catch (JsonException) { }
        }
        loadedPath = CachePath; dirty = false;
    }
    public List<(string Path, string Hash, long Mtime, long Size)> Snapshot()
    {
        lock (gate) { Load(); return entries.Where(p => p.Value is not null).Select(p => (p.Key, p.Value.Hash, p.Value.MTime, p.Value.Size)).ToList(); }
    }
    public void Flush()
    {
        lock (gate)
        {
            if (!dirty) return;
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            string temporary = CachePath + "." + Guid.NewGuid() + ".tmp";
            try
            {
                using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { JsonSerializer.Serialize(output, entries); output.Flush(true); }
                File.Move(temporary, CachePath, true);
                dirty = false; lastSave = Environment.TickCount64;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
    public List<string> RecordDownload(string path, string digest)
    {
        lock (gate)
        {
            Load(); FileInfo info = new(path);
            entries[Path.GetFullPath(path)] = new() { MTime = info.LastWriteTimeUtc.Ticks, Size = info.Length, Hash = digest };
            dirty = true; Flush();
            return entries.Where(p => File.Exists(p.Key)).Select(p => p.Value.Hash).Distinct().ToList();
        }
    }
    private static bool Valid(Entry? entry, FileInfo info) => entry is not null && entry.Size == info.Length && entry.MTime == info.LastWriteTimeUtc.Ticks && entry.Hash.Length == 64 && entry.Hash.All(char.IsAsciiHexDigit);
    public string Get(string path, CancellationToken token = default, bool force = false)
    {
        lock (gate)
        {
            Load(); token.ThrowIfCancellationRequested(); path = Path.GetFullPath(path);
            using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            FileInfo before = new(path); before.Refresh();
            var (identity, change) = FileIdentity(input.SafeFileHandle, path);
            entries.TryGetValue(path, out Entry? entry);
            string? digest = null, sample = null;
            LastSource = "hashed";
            if (!force && Valid(entry, before) && (entry!.Schema != 2 || (entry.Identity == identity && entry.Change == change)))
            { digest = entry.Hash; sample = entry.Sample; LastSource = "cached"; }
            string? oldPath = null;
            if (!force && digest is null && identity is not null)
            {
                var candidates = entries.Where(p => !comparer.Equals(p.Key, path) && Valid(p.Value, before) && p.Value.Schema == 2 && p.Value.Identity == identity && !File.Exists(p.Key)).Take(2).ToList();
                if (candidates.Count == 1)
                {
                    sample = Sample(input, token);
                    if (sample == candidates[0].Value.Sample)
                    { digest = candidates[0].Value.Hash; LastSource = "moved"; oldPath = candidates[0].Key; }
                }
            }
            if (digest is null)
            {
                FullHashReads++;
                SwarmUI.Utils.Logs.Info($"[AEC-LINK] Hashing model content ({Path.GetFileName(path)})");
                using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[1024 * 1024]; int count;
                while ((count = input.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, count); }
                digest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                sample = Sample(input, token);
            }
            token.ThrowIfCancellationRequested();
            FileInfo after = new(path);
            using FileStream reopened = File.OpenRead(path);
            if (before.Length != after.Length || before.LastWriteTimeUtc != after.LastWriteTimeUtc || FileIdentity(reopened.SafeFileHandle, path) != (identity, change)) throw new IOException("Model changed during inventory scan");
            if (LastSource != "cached")
            {
                entries[path] = new() { Schema = 2, Size = after.Length, MTime = after.LastWriteTimeUtc.Ticks, Hash = digest, Identity = identity, Change = change, Sample = sample };
                if (oldPath is not null) entries.Remove(oldPath);
                dirty = true;
                Interlocked.Exchange(ref catalogDirty, 1);
                if (Environment.TickCount64 - lastSave >= 3000) Flush();
                SwarmUI.Utils.Logs.Info($"[AEC-LINK] Model hash: {LastSource} ({Path.GetFileName(path)})");
            }
            return digest;
        }
    }
    private static string Sample(FileStream input, CancellationToken token)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[65536];
        foreach (long offset in new[] { 0L, Math.Max(0, input.Length / 2 - 32768), Math.Max(0, input.Length - 65536) }.Distinct().Order())
        { token.ThrowIfCancellationRequested(); input.Position = offset; int length = (int)Math.Min(buffer.Length, input.Length - offset); input.ReadExactly(buffer.AsSpan(0, length)); hash.AppendData(buffer, 0, length); }
        input.Position = 0; return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)] private static extern int Statx(int fd, string path, int flags, uint mask, byte[] output);
    [DllImport("libc", EntryPoint = "fstatfs", SetLastError = true)] private static extern int Statfs(int fd, byte[] output);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int type, byte[] output, uint size);
    internal static (string? Identity, string? Change) FileIdentity(SafeFileHandle handle, string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (path.StartsWith(@"\\") || new DriveInfo(Path.GetPathRoot(path)!).DriveType != DriveType.Fixed) return (null, null);
                byte[] id = new byte[24], basic = new byte[40];
                if (!GetFileInformationByHandleEx(handle, 18, id, 24) || !GetFileInformationByHandleEx(handle, 0, basic, 40)) return (null, null);
                long birth = BinaryPrimitives.ReadInt64LittleEndian(basic);
                if (birth <= 0 || id.AsSpan(8).IndexOfAnyExcept((byte)0) < 0) return (null, null);
                return ($"windows:{Convert.ToHexString(id)}:{birth}", BinaryPrimitives.ReadInt64LittleEndian(basic.AsSpan(24)).ToString());
            }
            if (OperatingSystem.IsLinux())
            {
                byte[] fs = new byte[256], stat = new byte[256]; int fd = handle.DangerousGetHandle().ToInt32();
                if (Statx(fd, "", 0x1000, 0xFFF, stat) != 0) return (null, File.GetCreationTimeUtc(path).Ticks.ToString());
                string change = $"{BitConverter.ToUInt64(stat, 32)}:{BitConverter.ToInt64(stat, 96)}:{BitConverter.ToUInt32(stat, 104)}";
                if (Statfs(fd, fs) != 0 || BitConverter.ToInt64(fs) is not (0xEF53 or 0x58465342 or 0x9123683E) || (BitConverter.ToUInt32(stat) & 0x800) == 0 || BitConverter.ToInt64(stat, 80) == 0) return (null, change);
                return ($"linux:{BitConverter.ToUInt32(stat, 136)}:{BitConverter.ToUInt32(stat, 140)}:{BitConverter.ToUInt64(stat, 32)}:{BitConverter.ToInt64(stat, 80)}:{BitConverter.ToUInt32(stat, 88)}", change);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EntryPointNotFoundException) { }
        return (null, null);
    }
}
