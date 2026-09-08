using ArcEnCiel.Link.Swarm;
internal static class HashCacheTests
{
    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "aec-hash-tests-" + Guid.NewGuid());
        string oldRoot = SwarmUI.Core.Program.DataDir;
        void Assert(bool value, string reason) { if (!value) throw new Exception(reason); }
        try
        {
            SwarmUI.Core.Program.DataDir = root; Directory.CreateDirectory(root);
            string path = Path.Combine(root, "model.safetensors"), moved = Path.Combine(root, "moved.safetensors");
            File.WriteAllBytes(path, new byte[300000]);
            var cache = new ArcEnCielLinkHashCache();
            string digest = cache.Get(path); cache.Flush();
            Assert(cache.FullHashReads == 1, "initial hash missing");
            cache.Get(path); Assert(cache.FullHashReads == 1, "unchanged file rehashed");
            using (var file = File.OpenRead(path))
            {
                var identity = ArcEnCielLinkHashCache.FileIdentity(file.SafeFileHandle, path);
                file.Dispose();
                File.Move(path, moved);
                Assert(cache.Get(moved) == digest, "move changed hash");
                Assert(cache.LastSource == (identity.Identity is null ? "hashed" : "moved"), "wrong move policy");
            }
            cache.Flush(); var restarted = new ArcEnCielLinkHashCache();
            restarted.Get(moved); Assert(restarted.FullHashReads == 0, "restart lost cache");
            File.Copy(moved, path); File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(moved)); File.Delete(moved);
            restarted.Get(path); Assert(restarted.LastSource == "hashed", "copy mistaken for move");
            File.WriteAllBytes(path, Enumerable.Repeat((byte)1, 300000).ToArray());
            Assert(restarted.Get(path) != digest, "content change ignored");
            int reads = restarted.FullHashReads;
            restarted.Get(path, force: true); Assert(restarted.FullHashReads == reads + 1, "force integrity check ignored");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { restarted.Get(path, cancelled.Token); throw new Exception("cancel ignored"); } catch (OperationCanceledException) { }
            restarted.Flush(); Assert(!Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Any(), "temporary files leaked");
            Console.WriteLine("Hash cache: unchanged, move, restart, copy, content, force, cancellation passed");
        }
        finally { SwarmUI.Core.Program.DataDir = oldRoot; Directory.Delete(root, true); }
    }
}
