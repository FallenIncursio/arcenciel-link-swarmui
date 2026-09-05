using System.Net;
using System.Text;
using ArcEnCiel.Link.Swarm;

namespace ContractTests;
internal static class SetupCheckTests
{
    public static async Task Run()
    {
        void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
        string root = Path.Combine(Path.GetTempPath(), "aec-link-probe-test-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            byte[] pattern = Encoding.UTF8.GetBytes("Arc en Ciel Link setup check v1\n");
            byte[] data = Enumerable.Range(0, 4096).Select(i => pattern[i % pattern.Length]).ToArray();
            string existing = Path.Combine(root, "existing.model");
            await File.WriteAllTextAsync(existing, "keep");
            var success = await ArcEnCielLinkSetupCheck.Probe(root, _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) }), CancellationToken.None);
            Assert(success.Value<bool>("ok") && success.Value<bool>("cleaned"), "Setup did not verify/clean");
            Assert(success.Value<string>("sha256") == ArcEnCielLinkSetupCheck.Sha256 && success.Value<int>("bytes") == 4096, "Invalid proof");
            success["type"] = "setup_check_result";
            using (var message = System.Text.Json.JsonDocument.Parse(ArcEnCielLinkProtocol.SerializePayload(success)))
            {
                Assert(message.RootElement.GetProperty("type").GetString() == "setup_check_result", "Protocol discriminator is not a string");
                Assert(message.RootElement.GetProperty("ok").GetBoolean() && message.RootElement.GetProperty("bytes").GetInt32() == 4096, "Probe fields lost their JSON types");
            }
            using (var legacy = System.Text.Json.JsonDocument.Parse(ArcEnCielLinkProtocol.SerializePayload(new { type = "poll" })))
                Assert(legacy.RootElement.GetProperty("type").GetString() == "poll", "Legacy message serialization changed");
            Assert(success.Value<string>("target") == root && await File.ReadAllTextAsync(existing) == "keep", "Wrong folder or existing file changed");
            Assert(Directory.GetFiles(root).Length == 1, "Test file remains");
            foreach (byte[] invalid in new[] { data[..10], new byte[4096], data.Concat(new byte[1]).ToArray() })
            {
                var result = await ArcEnCielLinkSetupCheck.Probe(root, _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(invalid) }), CancellationToken.None);
                Assert(!result.Value<bool>("ok") && result.Value<string>("code") == "HASH_MISMATCH", "Corrupt transfer accepted");
                Assert(result.Value<bool>("cleaned") && Directory.GetFiles(root).Length == 1, "Corrupt transfer not cleaned");
            }
            foreach (HttpStatusCode status in new[] { HttpStatusCode.Redirect, HttpStatusCode.Forbidden, HttpStatusCode.NotFound })
            {
                var result = await ArcEnCielLinkSetupCheck.Probe(root, _ => Task.FromResult(new HttpResponseMessage(status)), CancellationToken.None);
                Assert(!result.Value<bool>("ok") && result.Value<string>("code") == "TRANSFER_FAILED", "HTTP failure accepted");
            }
            var failed = await ArcEnCielLinkSetupCheck.Probe(root, _ => throw new Exception("private key"), CancellationToken.None);
            Assert(failed.Value<string>("code") == "TRANSFER_FAILED" && !failed.ToString().Contains("private key"), "Private error leaked");
            var unavailable = await ArcEnCielLinkSetupCheck.Probe(existing, _ => throw new Exception("should not download"), CancellationToken.None);
            Assert(unavailable.Value<string>("code") == "TARGET_UNAVAILABLE", "Invalid model root accepted");
            using CancellationTokenSource cancelled = new(); cancelled.Cancel();
            var stopped = await ArcEnCielLinkSetupCheck.Probe(root, token => { token.ThrowIfCancellationRequested(); return Task.FromResult(new HttpResponseMessage()); }, cancelled.Token);
            Assert(!stopped.Value<bool>("ok") && stopped.Value<bool>("cleaned"), "Cancelled probe accepted or not cleaned");
            Assert(ArcEnCielLinkSetupCheck.Target("lora") == "models/Lora" && ArcEnCielLinkSetupCheck.Target("checkpoint") == "models/Checkpoint" && ArcEnCielLinkSetupCheck.Target("vae") == "models/VAE" && ArcEnCielLinkSetupCheck.Target("embedding") == "embeddings" && ArcEnCielLinkSetupCheck.Target("../invalid") == null, "Category contract drift");
            Console.WriteLine("PASS: setup transfer, readback, SHA-256, cleanup, corruption, HTTP failures, cancellation and model paths");
        }
        finally { Directory.Delete(root, true); }
    }
}
