using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.Utils;

namespace ArcEnCiel.Link.Swarm;

public class ArcEnCielLinkExtension : Extension
{
    public static ArcEnCielLinkConfig Config { get; private set; } = ArcEnCielLinkConfig.Load();

    public override void OnInit()
    {
        ExtensionAuthor = "ArcEnCiel";
        Description = "ArcEnCiel Link worker extension for SwarmUI.";
        License = "MIT";
        ScriptFiles.Add("Assets/arcenciel_link_settings.js");
        ArcEnCielLinkRuntime.Initialize();
    }

    public override void OnPreLaunch()
    {
        if (WebServer.WebApp is null)
        {
            Logs.Error("[AEC-LINK] WebApp not ready; endpoints not registered.");
            return;
        }

        ArcEnCielLinkEndpoints.Map(WebServer.WebApp);
    }

    public override void OnShutdown()
    {
        ArcEnCielLinkRuntime.Shutdown();
    }
}

internal static class ArcEnCielLinkEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapMethods("/arcenciel-link/{**path}", ["OPTIONS"], (HttpContext context) =>
        {
            if (!ArcEnCielLinkCors.TryGetAllowedOrigin(context, out string? origin))
            {
                return Results.StatusCode(403);
            }

            ArcEnCielLinkCors.ApplyCorsHeaders(context.Response, origin);
            return Results.StatusCode(204);
        });

        app.MapGet("/arcenciel-link/ping", (HttpContext context) =>
        {
            if (!ArcEnCielLinkCors.TryGetAllowedOrigin(context, out string? origin))
            {
                return Results.StatusCode(403);
            }

            ArcEnCielLinkCors.ApplyCorsHeaders(context.Response, origin, allowCredentials: false);
            return Results.Text("ok", "text/plain");
        });

        app.MapGet("/arcenciel-link/settings", (HttpContext context) =>
        {
            if (!TryAuthorizeSettings(context, requireEdit: false, out _, out string? origin, out IResult? failure))
            {
                return failure!;
            }

            ArcEnCielLinkConfig config = ArcEnCielLinkConfig.Load();
            ArcEnCielLinkCors.ApplyCorsHeaders(context.Response, origin);
            return Results.Json(new
            {
                baseUrl = config.BaseUrl,
                linkKeySet = !string.IsNullOrWhiteSpace(config.LinkKey),
                apiKeySet = !string.IsNullOrWhiteSpace(config.ApiKey),
                enabled = config.Enabled,
                minFreeMb = config.MinFreeMb,
                maxRetries = config.MaxRetries,
                backoffBase = config.BackoffBase,
                saveHtmlPreview = config.SaveHtmlPreview,
                allowPrivateOrigins = config.AllowPrivateOrigins,
                workerOnline = ArcEnCielLinkRuntime.Worker.IsWorkerRunning
            });
        });

        app.MapPost("/arcenciel-link/settings", async (HttpContext context) =>
        {
            if (!TryAuthorizeSettings(context, requireEdit: true, out _, out string? origin, out IResult? failure))
            {
                return failure!;
            }

            SettingsPayload? payload = await context.Request.ReadFromJsonAsync<SettingsPayload>();
            if (payload is null)
            {
                ArcEnCielLinkCors.ApplyCorsHeaders(context.Response, origin);
                return Results.Json(new { error = "payload required" }, statusCode: 400);
            }

            string? baseUrl = payload.BaseUrl?.Trim();
            if (payload.BaseUrl is not null)
            {
                if (string.IsNullOrWhiteSpace(baseUrl))
                {
                    ArcEnCielLinkCors.ApplyCorsHeaders(context.Response, origin);
                    return Results.Json(new { error = "Base URL required" }, statusCode: 400);
                }

                if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri) ||
                    (uri.Scheme != "http" && uri.Scheme != "https"))
                {
                    ArcEnCielLinkCors.ApplyCorsHeaders(context.Response, origin);
                    return Results.Json(new { error = "Base URL must be http(s)" }, statusCode: 400);
                }
            }

            string? linkKey = payload.LinkKey?.Trim();
            if (payload.LinkKey is not null && !string.IsNullOrWhiteSpace(linkKey) && !IsValidLinkKey(linkKey))
            {
                ArcEnCielLinkCors.ApplyCorsHeaders(context.Response, origin);
                return Results.Json(new { error = "Invalid link key format" }, statusCode: 400);
            }

            string? apiKey = payload.ApiKey?.Trim();
            if (payload.MinFreeMb.HasValue && payload.MinFreeMb.Value < 0)
            {
                ArcEnCielLinkCors.ApplyCorsHeaders(context.Response, origin);
                return Results.Json(new { error = "Min free MB must be >= 0" }, statusCode: 400);
            }

            if (payload.MaxRetries.HasValue && payload.MaxRetries.Value < 1)
            {
                ArcEnCielLinkCors.ApplyCorsHeaders(context.Response, origin);
                return Results.Json(new { error = "Max retries must be >= 1" }, statusCode: 400);
            }

            if (payload.BackoffBase.HasValue && payload.BackoffBase.Value < 1)
            {
                ArcEnCielLinkCors.ApplyCorsHeaders(context.Response, origin);
                return Results.Json(new { error = "Backoff base must be >= 1" }, statusCode: 400);
            }

            ArcEnCielLinkRuntime.ApplyConfig(config =>
            {
                if (payload.BaseUrl is not null && baseUrl is not null)
                {
                    config.BaseUrl = baseUrl;
                }

                if (payload.LinkKey is not null)
                {
                    config.LinkKey = linkKey ?? "";
                }

                if (payload.ApiKey is not null)
                {
                    config.ApiKey = apiKey ?? "";
                }

                if (payload.Enabled.HasValue)
                {
                    config.Enabled = payload.Enabled.Value;
                }

                if (payload.MinFreeMb.HasValue)
                {
                    config.MinFreeMb = payload.MinFreeMb.Value;
                }

                if (payload.MaxRetries.HasValue)
                {
                    config.MaxRetries = payload.MaxRetries.Value;
                }

                if (payload.BackoffBase.HasValue)
                {
                    config.BackoffBase = payload.BackoffBase.Value;
                }

                if (payload.SaveHtmlPreview.HasValue)
                {
                    config.SaveHtmlPreview = payload.SaveHtmlPreview.Value;
                }

                if (payload.AllowPrivateOrigins.HasValue)
                {
                    config.AllowPrivateOrigins = payload.AllowPrivateOrigins.Value;
                }
            });

            ArcEnCielLinkCors.ApplyCorsHeaders(context.Response, origin);
            return Results.Json(new { ok = true, workerOnline = ArcEnCielLinkRuntime.Worker.IsWorkerRunning });
        });

        app.MapPost("/arcenciel-link/toggle_link", async (HttpContext context) =>
        {
            if (!ArcEnCielLinkCors.TryGetAllowedOrigin(context, out string? origin))
            {
                return Results.StatusCode(403);
            }

            TogglePayload? payload = await context.Request.ReadFromJsonAsync<TogglePayload>();
            if (payload?.Enable is null)
            {
                ArcEnCielLinkCors.ApplyCorsHeaders(context.Response, origin);
                return Results.Json(new { error = "enable flag required" }, statusCode: 400);
            }

            string? linkKey = payload.LinkKey;
            if (linkKey is not null)
            {
                linkKey = linkKey.Trim();
                if (!string.IsNullOrWhiteSpace(linkKey) && !IsValidLinkKey(linkKey))
                {
                    ArcEnCielLinkCors.ApplyCorsHeaders(context.Response, origin);
                    return Results.Json(new { error = "Invalid link key format" }, statusCode: 400);
                }
            }

            string? apiKey = payload.ApiKey;
            if (apiKey is not null)
            {
                apiKey = apiKey.Trim();
            }

            ArcEnCielLinkRuntime.ApplyWorkerState(payload.Enable.Value, linkKey, apiKey);
            bool workerOnline = ArcEnCielLinkRuntime.Worker.IsWorkerRunning;

            ArcEnCielLinkCors.ApplyCorsHeaders(context.Response, origin);
            return Results.Json(new { ok = true, workerOnline });
        });

        app.MapGet("/arcenciel-link/folders/{kind}", (HttpContext context, string kind) =>
        {
            if (!ArcEnCielLinkCors.TryGetAllowedOrigin(context, out string? origin))
            {
                return Results.StatusCode(403);
            }

            try
            {
                IReadOnlyList<string> folders = ArcEnCielLinkPaths.ListSubfolders(kind);
                ArcEnCielLinkCors.ApplyCorsHeaders(context.Response, origin);
                return Results.Json(new { folders });
            }
            catch (Exception ex)
            {
                ArcEnCielLinkCors.ApplyCorsHeaders(context.Response, origin);
                return Results.Json(new { error = ex.Message }, statusCode: 400);
            }
        });

        app.MapPost("/arcenciel-link/generate_sidecars", async (HttpContext context) =>
        {
            if (!ArcEnCielLinkCors.TryGetAllowedOrigin(context, out string? origin))
            {
                return Results.StatusCode(403);
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await ArcEnCielLinkRuntime.Worker.GenerateSidecarsAsync();
                }
                catch (Exception ex)
                {
                    Logs.Error($"[AEC-LINK] Sidecar generation failed: {ex.Message}");
                }
            });

            ArcEnCielLinkCors.ApplyCorsHeaders(context.Response, origin);
            return Results.Json(new { ok = true });
        });
    }

    private static bool IsValidLinkKey(string value)
    {
        return System.Text.RegularExpressions.Regex.IsMatch(value, "^lk_[A-Za-z0-9_-]{32}$");
    }

    private static bool TryAuthorizeSettings(HttpContext context, bool requireEdit, out User? user, out string? origin, out IResult? failure)
    {
        failure = null;
        user = null;

        if (!ArcEnCielLinkCors.TryGetSameOrigin(context, out origin))
        {
            failure = Results.StatusCode(403);
            return false;
        }

        user = WebServer.GetUserFor(context);
        if (user is null)
        {
            ArcEnCielLinkCors.ApplyCorsHeaders(context.Response, origin);
            failure = Results.StatusCode(401);
            return false;
        }

        PermInfo required = requireEdit ? Permissions.EditServerSettings : Permissions.ReadServerSettings;
        if (!user.HasPermission(required))
        {
            ArcEnCielLinkCors.ApplyCorsHeaders(context.Response, origin);
            failure = Results.StatusCode(403);
            return false;
        }

        return true;
    }

    private sealed class TogglePayload
    {
        public bool? Enable { get; set; }
        public string? LinkKey { get; set; }
        public string? ApiKey { get; set; }
    }

    private sealed class SettingsPayload
    {
        public string? BaseUrl { get; set; }
        public string? LinkKey { get; set; }
        public string? ApiKey { get; set; }
        public bool? Enabled { get; set; }
        public int? MinFreeMb { get; set; }
        public int? MaxRetries { get; set; }
        public int? BackoffBase { get; set; }
        public bool? SaveHtmlPreview { get; set; }
        public bool? AllowPrivateOrigins { get; set; }
    }
}
