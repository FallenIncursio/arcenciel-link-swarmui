using System.Security.Cryptography;
using System.Collections.Concurrent;
using Newtonsoft.Json.Linq;
using ArcEnCiel.Link.Swarm;
namespace ContractTests;
internal static class DeviceToolsTests
{
    public static async Task Run()
    {
        void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
        string root = Path.Combine(Path.GetTempPath(), "aec-tools-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        string model = Path.Combine(root, "model.pt"); await File.WriteAllTextAsync(model, "model");
        try {
            async Task<JObject> Wait(ArcEnCielLinkDeviceTools tools) {
                for (int n = 0; n < 200; n++) { var state = tools.Status; if (state?.Value<string>("state") != "RUNNING") { await Task.Delay(20); return state!; } await Task.Delay(10); }
                throw new Exception("Device tool did not finish");
            }
            JObject Message(string action = "rescan") => new() { ["taskId"] = Guid.NewGuid().ToString(), ["runtimeId"] = "runtime-test", ["action"] = action };
            ConcurrentQueue<(string Url, JObject Body)> calls = new();
            Task<JObject> Post(string url, JObject body, CancellationToken token) { token.ThrowIfCancellationRequested(); calls.Enqueue((url, (JObject)body.DeepClone())); return Task.FromResult(url.EndsWith("/sidecars/meta") ? new JObject { [Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(model))).ToLowerInvariant()] = new JObject { ["modelId"] = 1 } } : new JObject()); }
            Task<(bool Changed, bool Warning)> Repair(JObject meta, string hash, string path, CancellationToken token) => Task.FromResult((true, true));
            var tools = new ArcEnCielLinkDeviceTools(); List<string>? synced = null;
            var wrong = Message(); wrong["runtimeId"] = "wrong";
            Assert(!tools.Start(wrong, "runtime-test", () => [root], Post, Repair, _ => {}, false), "Wrong runtime accepted");
            Assert(tools.Start(Message("repair_sidecars"), "runtime-test", () => [root, root], Post, Repair, h => synced = h, false), "Repair refused");
            var result = await Wait(tools);
            Assert(result.Value<string>("state") == "DONE" && result.Value<int>("repaired") == 1 && result.Value<int>("warnings") == 1 && result.Value<int>("processed") == 2, "Repair counts or warnings lost");
            Assert(synced?.Count == 1 && calls.Any(c => c.Url == "/inventory" && c.Body.Value<string>("runtimeId") == "runtime-test"), "Inventory runtime missing");
            Assert(await File.ReadAllTextAsync(model) == "model", "Model changed");
            await File.WriteAllTextAsync(model, "changed model");
            Assert(tools.Start(Message(), "runtime-test", () => [root], Post, Repair, h => synced = h, false), "Rescan refused"); await Wait(tools);
            Assert(synced![0] == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(model))).ToLowerInvariant(), "Changed model used stale hash");
            File.Delete(model);
            Assert(tools.Start(Message(), "runtime-test", () => [root], Post, Repair, h => synced = h, false), "Empty scan refused"); await Wait(tools); Assert(synced!.Count == 0, "Removed model retained");
            Assert(tools.Start(Message(), "runtime-test", () => [root], Post, Repair, _ => {}, true), "Busy error not accepted"); result = await Wait(tools); Assert(result.Value<string>("code") == "BUSY" && result.Value<string>("state") == "ERROR", "Busy worker did file work");
            var started = new TaskCompletionSource(); var cancellation = Message();
            async Task<JObject> Block(string url, JObject body, CancellationToken token) { started.TrySetResult(); await Task.Delay(30000, token); return new(); }
            Assert(tools.Start(cancellation, "runtime-test", () => [root], Block, Repair, _ => {}, false), "Cancellation task refused"); await started.Task;
            Assert(!tools.Start(Message(), "runtime-test", () => [root], Post, Repair, _ => {}, false), "Concurrent tool accepted");
            Assert(!tools.Cancel(Message(), "runtime-test"), "Wrong task cancelled"); Assert(tools.Cancel(cancellation, "runtime-test"), "Cancel refused"); result = await Wait(tools); Assert(result.Value<string>("state") == "CANCELLED", "Cancellation not observed");
            Assert(tools.Start(Message(), "runtime-test", () => [root], (_, _, _) => throw new IOException("private key"), Repair, _ => {}, false), "Network test refused"); result = await Wait(tools); Assert(result.Value<string>("state") == "ERROR" && !result.ToString().Contains("private key"), "Network failure succeeded or leaked");
            Console.WriteLine("PASS: device tools runtime fencing, deduplication, hash invalidation, empty inventory, warning counters, busy guard, cancellation and network failure");
        } finally { Directory.Delete(root, true); }
    }
}
