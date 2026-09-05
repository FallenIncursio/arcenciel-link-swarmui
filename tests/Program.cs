using System.Text.Json;
using ArcEnCiel.Link.Swarm;

namespace ContractTests
{
    internal static class Runner
    {
        public static void Main(string[] args)
        {
            string root = Path.Combine(Path.GetTempPath(), "aec-link-config-" + Guid.NewGuid());
            SwarmUI.Core.Program.DataDir = root;
            string configPath = Path.Combine(root, "Extensions", "ArcEnCielLink", "config.json");
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            string[] variables = ["ARCENCIEL_LINK_URL", "ARCENCIEL_LINK_KEY", "ARCENCIEL_LINK_ENABLED", "ARCENCIEL_DEV"];
            void ClearEnvironment() { foreach (string name in variables) Environment.SetEnvironmentVariable(name, null); }
            void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
            try
            {
                using JsonDocument fixtures = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures/runtime-config-v1.json")));
                int count = 0;
                foreach (JsonElement test in fixtures.RootElement.GetProperty("cases").EnumerateArray())
                {
                    if (args.Length == 0)
                    {
                        System.Diagnostics.ProcessStartInfo child = new("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
                        child.ArgumentList.Add(typeof(Runner).Assembly.Location);
                        child.ArgumentList.Add(count.ToString());
                        foreach (string variable in variables) child.Environment.Remove(variable);
                        foreach (JsonProperty variable in test.GetProperty("env").EnumerateObject()) child.Environment[variable.Name] = variable.Value.GetString()!;
                        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(child)!;
                        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                        process.WaitForExit();
                        Assert(process.ExitCode == 0, output);
                        count++;
                        continue;
                    }
                    if (count++ != int.Parse(args[0])) continue;
                    if (File.Exists(configPath)) File.Delete(configPath);
                    JsonElement stored = test.GetProperty("stored");
                    if (stored.ValueKind != JsonValueKind.Null)
                    {
                        ArcEnCielLinkConfig desktop = new();
                        if (stored.TryGetProperty("base_url", out JsonElement url)) desktop.BaseUrl = url.GetString()!;
                        if (stored.TryGetProperty("link_key", out JsonElement key)) desktop.LinkKey = key.GetString()!;
                        if (stored.TryGetProperty("enabled", out JsonElement enabled)) desktop.Enabled = enabled.GetBoolean();
                        File.WriteAllText(configPath, JsonSerializer.Serialize(desktop));
                    }
                    ArcEnCielLinkConfig config = ArcEnCielLinkConfig.Load();
                    foreach (JsonProperty expected in test.GetProperty("expected").EnumerateObject())
                    {
                        bool matches = expected.Name switch
                        {
                            "base_url" => config.BaseUrl == expected.Value.GetString(),
                            "link_key" => config.LinkKey == expected.Value.GetString(),
                            "enabled" => config.Enabled == expected.Value.GetBoolean(),
                            _ => false
                        };
                        Assert(matches, $"{test.GetProperty("name").GetString()}: {expected.Name}");
                    }
                    return;
                }
                ClearEnvironment();
                string desktopKey = "lk_" + new string('a', 32);
                string runtimeKey = "lk_" + new string('b', 32);
                File.WriteAllText(configPath, JsonSerializer.Serialize(new ArcEnCielLinkConfig { LinkKey = desktopKey, Enabled = false }));
                Environment.SetEnvironmentVariable("ARCENCIEL_LINK_KEY", runtimeKey);
                Environment.SetEnvironmentVariable("ARCENCIEL_LINK_ENABLED", "1");
                ArcEnCielLinkConfig hosted = ArcEnCielLinkConfig.Load();
                hosted.ValidateWorkerChange(false, null);
                hosted.Enabled = false;
                hosted.Save();
                Assert(!File.ReadAllText(configPath).Contains(runtimeKey), "Runtime key persisted");
                Assert(ArcEnCielLinkConfig.Load().Enabled, "Restart did not restore environment startup state");
                bool rejected = false;
                try { hosted.ValidateWorkerChange(true, desktopKey); } catch (ArgumentException) { rejected = true; }
                Assert(rejected, "Managed key replaced");
                ClearEnvironment();
                ArcEnCielLinkConfig restored = ArcEnCielLinkConfig.Load();
                Assert(restored.LinkKey == desktopKey && !restored.Enabled, "Desktop settings were overwritten");
                File.Delete(configPath);
                rejected = false;
                try { ArcEnCielLinkConfig.Load().ValidateWorkerChange(true, null); } catch (ArgumentException) { rejected = true; }
                Assert(rejected, "Missing key accepted");
                File.WriteAllText(configPath, JsonSerializer.Serialize(new ArcEnCielLinkConfig { Enabled = true }));
                ArcEnCielLinkConfig.Load().ValidateWorkerChange(true, desktopKey);
                AttemptTests.Run().GetAwaiter().GetResult();
                SetupCheckTests.Run().GetAwaiter().GetResult();
                DeviceToolsTests.Run().GetAwaiter().GetResult();
                Console.WriteLine($"PASS: {count} shared startup cases plus persistence, restart, managed-key and missing-key checks");
            }
            finally { ClearEnvironment(); Directory.Delete(root, true); }

        }
    }
}

namespace SwarmUI.Core { public static class Program { public static string DataDir = ""; } }
namespace SwarmUI.Utils { public static class Logs { public static void Error(string message) { } } }
