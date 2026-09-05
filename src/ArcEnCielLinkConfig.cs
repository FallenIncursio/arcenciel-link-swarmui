using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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

    [JsonIgnore]
    public string? RuntimeError { get; private set; }

    private static string ConfigPath => Path.Combine(Program.DataDir, "Extensions", "ArcEnCielLink", "config.json");

    private static ArcEnCielLinkConfig ReadStored()
    {
        try
        {
            return File.Exists(ConfigPath)
                ? JsonSerializer.Deserialize<ArcEnCielLinkConfig>(File.ReadAllText(ConfigPath)) ?? new()
                : new();
        }
        catch (Exception)
        {
            Logs.Error("[AEC-LINK] Failed to read config; using defaults");
            return new();
        }
    }

    public static ArcEnCielLinkConfig Load()
    {
        ArcEnCielLinkConfig config = ReadStored();
        if (File.Exists(ConfigPath))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(ConfigPath));
                if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.EnumerateObject().Any(
                    property => property.Name.Equals("ApiKey", StringComparison.OrdinalIgnoreCase) ||
                                property.Name.Equals("api_key", StringComparison.OrdinalIgnoreCase)))
                    config.Save();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { /* ReadStored already reported the invalid configuration. */ }
        }
        config.ApplyEnvironment();
        return config;
    }

    public void ApplyEnvironment()
    {
        string? rawUrl = Environment.GetEnvironmentVariable("ARCENCIEL_LINK_URL");
        string? rawKey = Environment.GetEnvironmentVariable("ARCENCIEL_LINK_KEY");
        string? rawEnabled = Environment.GetEnvironmentVariable("ARCENCIEL_LINK_ENABLED");
        RuntimeError = null;
        if (rawUrl is not null) BaseUrl = rawUrl.Trim().TrimEnd('/');
        if (rawKey is not null) LinkKey = rawKey.Trim();
        if (rawEnabled is not null)
        {
            string normalized = rawEnabled.Trim().ToLowerInvariant();
            Enabled = normalized is "1" or "true" or "yes" or "on";
            if (normalized is not ("1" or "true" or "yes" or "on" or "0" or "false" or "no" or "off"))
                RuntimeError = "ARCENCIEL_LINK_ENABLED must be one of 1/0, true/false, yes/no, or on/off";
        }
        if (!TryNormalizeBaseUrl(BaseUrl, out string normalizedUrl, out _))
        {
            RuntimeError = "ARCENCIEL_LINK_URL must be an absolute HTTPS URL without credentials, query, or fragment";
            BaseUrl = "https://link.arcenciel.io/api/link";
            LinkKey = "";
        }
        else BaseUrl = normalizedUrl;
        LinkKey = (LinkKey ?? "").Trim();
        if (LinkKey.Length > 0 && !Regex.IsMatch(LinkKey, @"^lk_[A-Za-z0-9_-]{32}$"))
        {
            RuntimeError ??= "ARCENCIEL_LINK_KEY has an invalid format; expected lk_ followed by 32 URL-safe characters";
            LinkKey = "";
        }
        if (Enabled && LinkKey.Length == 0)
            RuntimeError ??= "A Link key is required before enabling the worker (ARCENCIEL_LINK_KEY)";
        if (RuntimeError is not null)
        {
            Enabled = false;
            Logs.Error($"[AEC-LINK] {RuntimeError}; worker disabled");
        }
    }

    public void ValidateWorkerChange(bool enable, string? linkKey)
    {
        string? managed = Environment.GetEnvironmentVariable("ARCENCIEL_LINK_KEY");
        if (managed is not null && linkKey is not null && linkKey != managed.Trim())
            throw new ArgumentException("ARCENCIEL_LINK_KEY is managed by the runtime environment; update it and restart");
        if (enable)
        {
            bool repairingDesktopKey = managed is null && !string.IsNullOrWhiteSpace(linkKey) && RuntimeError is not null &&
                (RuntimeError.StartsWith("A Link key is required", StringComparison.Ordinal) ||
                 RuntimeError.StartsWith("ARCENCIEL_LINK_KEY has an invalid format", StringComparison.Ordinal));
            if (RuntimeError is not null && !repairingDesktopKey) throw new ArgumentException(RuntimeError);
            if (string.IsNullOrWhiteSpace(linkKey ?? LinkKey))
                throw new ArgumentException("A Link key is required before enabling the worker (ARCENCIEL_LINK_KEY)");
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

            ArcEnCielLinkConfig payload = (ArcEnCielLinkConfig)MemberwiseClone();
            ArcEnCielLinkConfig stored = ReadStored();
            if (Environment.GetEnvironmentVariable("ARCENCIEL_LINK_URL") is not null) payload.BaseUrl = stored.BaseUrl;
            if (Environment.GetEnvironmentVariable("ARCENCIEL_LINK_KEY") is not null) payload.LinkKey = stored.LinkKey;
            if (Environment.GetEnvironmentVariable("ARCENCIEL_LINK_ENABLED") is not null) payload.Enabled = stored.Enabled;
            string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
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
