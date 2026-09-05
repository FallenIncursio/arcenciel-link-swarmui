using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;

namespace ArcEnCiel.Link.Swarm;

internal static class ArcEnCielLinkSetupCheck
{
    public const int Size = 4096;
    public const string Sha256 = "d956b3266db2e34aebd97935fc60086319e2b35f028b78feab2b98bdf5e88b47";
    public static string? Target(string? kind) => kind switch
    {
        "checkpoint" => "models/Checkpoint", "lora" => "models/Lora", "vae" => "models/VAE", "embedding" => "embeddings", _ => null
    };

    public static async Task<JObject> Probe(string root, Func<CancellationToken, Task<HttpResponseMessage>> download, CancellationToken token)
    {
        JObject result = new() { ["ok"] = false, ["code"] = "TARGET_UNAVAILABLE" };
        string? temporary = null;
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        token = deadline.Token;
        try
        {
            root = Path.GetFullPath(root);
            Directory.CreateDirectory(root);
            long? free = null;
            try
            {
                StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                DriveInfo? drive = DriveInfo.GetDrives().Where(d => root == d.Name.TrimEnd(Path.DirectorySeparatorChar) || (root + Path.DirectorySeparatorChar).StartsWith(d.Name, comparison)).OrderByDescending(d => d.Name.Length).FirstOrDefault();
                if (drive is not null) free = drive.AvailableFreeSpace;
            }
            catch { /* Unknown capacity remains explicit in the result. */ }
            if (free is < 1048576) { result["code"] = "LOW_DISK_SPACE"; return result; }
            result["code"] = "WRITE_FAILED";
            temporary = Path.Combine(root, ".aec-link-check-" + Guid.NewGuid().ToString("N") + ".tmp");
            int size = 0;
            await using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                result["code"] = "TRANSFER_FAILED";
                using HttpResponseMessage response = await download(token);
                if (response.StatusCode != HttpStatusCode.OK) return result;
                await using Stream input = await response.Content.ReadAsStreamAsync(token);
                byte[] buffer = new byte[4096];
                int count;
                while ((count = await input.ReadAsync(buffer, token)) > 0)
                {
                    size += count;
                    if (size > Size) { result["code"] = "HASH_MISMATCH"; return result; }
                    result["code"] = "WRITE_FAILED";
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                    result["code"] = "TRANSFER_FAILED";
                }
                result["code"] = "WRITE_FAILED";
                output.Flush(true);
            }
            string hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(temporary, token))).ToLowerInvariant();
            if (size != Size || hash != Sha256) { result["code"] = "HASH_MISMATCH"; return result; }
            result["ok"] = true; result["code"] = "VERIFIED"; result["bytes"] = size;
            result["sha256"] = hash; result["target"] = root; result["freeBytes"] = free;
        }
        catch { result["ok"] = false; }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); result["cleaned"] = true; }
                catch { result["ok"] = false; result["code"] = "CLEANUP_FAILED"; result["cleaned"] = false; }
            }
        }
        return result;
    }
}
