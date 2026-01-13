using System.IO;
using System.Text.Json;
using SwarmUI.Core;
using SwarmUI.Utils;

namespace ArcEnCiel.Link.Swarm;

public class ArcEnCielLinkConfig
{
    public string BaseUrl { get; set; } = "https://link.arcenciel.io/api/link";
    public string LinkKey { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public bool Enabled { get; set; } = false;
    public int MinFreeMb { get; set; } = 2048;
    public int MaxRetries { get; set; } = 5;
    public int BackoffBase { get; set; } = 2;
    public bool SaveHtmlPreview { get; set; } = false;
    public bool AllowPrivateOrigins { get; set; } = false;

    private static string ConfigPath => Path.Combine(Program.DataDir, "Extensions", "ArcEnCielLink", "config.json");

    public static ArcEnCielLinkConfig Load()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                return new ArcEnCielLinkConfig();
            }

            string json = File.ReadAllText(ConfigPath);
            ArcEnCielLinkConfig? config = JsonSerializer.Deserialize<ArcEnCielLinkConfig>(json);
            return config ?? new ArcEnCielLinkConfig();
        }
        catch (System.Exception ex)
        {
            Logs.Error($"[AEC-LINK] Failed to load config: {ex.Message}");
            return new ArcEnCielLinkConfig();
        }
    }

    public void Save()
    {
        try
        {
            string? dir = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
        }
        catch (System.Exception ex)
        {
            Logs.Error($"[AEC-LINK] Failed to save config: {ex.Message}");
        }
    }
}
