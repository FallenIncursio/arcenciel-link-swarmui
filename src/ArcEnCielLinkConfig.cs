using System.IO;
using System.Text.Json;
using SwarmUI.Core;
using SwarmUI.Utils;

namespace ArcEnCiel.Link.Swarm;

public class ArcEnCielLinkConfig
{
    public string BaseUrl { get; set; } = "https://link.arcenciel.io/api/link";
    public string LinkKey { get; set; } = "";
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
            config ??= new ArcEnCielLinkConfig();

            using JsonDocument document = JsonDocument.Parse(json);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (property.Name.Equals("ApiKey", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Equals("api_key", StringComparison.OrdinalIgnoreCase))
                {
                    config.Save();
                    break;
                }
            }

            return config;
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
            string temporaryPath = $"{ConfigPath}.tmp";
            File.WriteAllText(temporaryPath, json);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temporaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            File.Move(temporaryPath, ConfigPath, true);
        }
        catch (System.Exception ex)
        {
            Logs.Error($"[AEC-LINK] Failed to save config: {ex.Message}");
        }
    }

    public static bool TryNormalizeBaseUrl(string? raw, out string normalized, out string error)
    {
        normalized = "";
        error = "Base URL must be an absolute HTTPS URL";
        if (!Uri.TryCreate(raw?.Trim(), UriKind.Absolute, out Uri? uri) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        bool developmentMode = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ARCENCIEL_DEV"));
        if (!uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) &&
            !(developmentMode && uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)))
        {
            error = "Base URL must use HTTPS (HTTP is only allowed with ARCENCIEL_DEV)";
            return false;
        }

        normalized = uri.ToString().TrimEnd('/');
        return true;
    }
}
