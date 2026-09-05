using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Utils;

namespace ArcEnCiel.Link.Swarm;

internal static class ArcEnCielLinkSidecars
{
    public static async Task GenerateForExistingAsync(
        ArcEnCielLinkWorker worker,
        ArcEnCielLinkConfig config,
        HttpClient http,
        CancellationToken token
    )
    {
        IReadOnlyDictionary<string, string> fileMap = worker.Hashes.GetModelFilesByHash();
        if (fileMap.Count == 0)
        {
            return;
        }

        JObject? metas = await FetchSidecarMetaAsync(config, http, fileMap.Keys, token);
        if (metas is null)
        {
            return;
        }

        foreach (KeyValuePair<string, string> entry in fileMap)
        {
            if (!metas.TryGetValue(entry.Key, out JToken? metaToken))
            {
                continue;
            }

            if (metaToken is not JObject meta)
            {
                continue;
            }

            string path = entry.Value;
            if (HasArcEnCielSidecar(path))
            {
                continue;
            }

            await WriteSidecarsAsync(worker, config, http, meta, entry.Key, path, token);
        }
    }

    public static async Task<bool> WriteSidecarsAsync(
        ArcEnCielLinkWorker worker,
        ArcEnCielLinkConfig config,
        HttpClient http,
        JObject meta,
        string hash,
        string modelPath,
        CancellationToken token,
        bool missingOnly = false
    )
    {
        string? previewName = await SavePreviewAsync(http, meta.Value<string>("preview"), modelPath, token, missingOnly);
        token.ThrowIfCancellationRequested();
        if (!missingOnly || !File.Exists(Path.ChangeExtension(modelPath, ".json"))) WriteJsonSidecar(meta, hash, modelPath, missingOnly);
        if (config.SaveHtmlPreview && (!missingOnly || !File.Exists(Path.ChangeExtension(modelPath, ".arcenciel.html"))))
        {
            WriteHtmlPreview(meta, hash, previewName, modelPath, missingOnly);
        }
        return !string.IsNullOrWhiteSpace(meta.Value<string>("preview")) && previewName is null;
    }

    public static void WriteJsonSidecar(JObject meta, string hash, string modelPath, bool missingOnly = false)
    {
        string? arcencielUrl = meta.Value<int?>("modelId") is { } modelId
            ? $"https://arcenciel.io/models/{modelId}"
            : null;
        string activationText = string.Join("; ", meta["activationTags"]?.Values<string>() ?? Array.Empty<string>());

        string? about = meta.Value<string>("aboutThisVersion") ?? meta.Value<string>("about");
        string? description = string.IsNullOrWhiteSpace(about) ? arcencielUrl : about;

        Dictionary<string, object?> sdMeta = new()
        {
            ["description"] = description,
            ["sd version"] = "unknown",
            ["activation text"] = activationText,
            ["modelspec.trigger_phrase"] = activationText,
            ["trigger_phrase"] = activationText,
            ["preferred weight"] = 1.0,
            ["notes"] = arcencielUrl,
        };
        string sdJson = JsonSerializer.Serialize(sdMeta, new JsonSerializerOptions { WriteIndented = true });
        WriteText(Path.ChangeExtension(modelPath, ".json"), sdJson, missingOnly);
    }

    public static void WriteHtmlPreview(JObject meta, string hash, string? previewName, string modelPath, bool missingOnly = false)
    {
        StringBuilder html = new();
        html.AppendLine("<!doctype html><html lang=\"en\"><meta charset=\"utf-8\">");
        html.AppendLine($"<title>{EscapeHtml(meta.Value<string>("modelTitle") ?? "ArcEnCiel Model")}</title>");
        html.AppendLine("<style>");
        html.AppendLine("body{font-family:system-ui, sans-serif; max-width:720px; margin:2rem auto; line-height:1.5}");
        html.AppendLine("img{max-width:100%; border-radius:8px; box-shadow:0 2px 8px #0003}");
        html.AppendLine("pre{background:#f8f8f8; padding:0.75rem 1rem; border-radius:6px; overflow:auto}");
        html.AppendLine(".tag{display:inline-block; background:#eef; color:#226; padding:2px 6px;");
        html.AppendLine("border-radius:4px; margin:2px; font-size:90%}");
        html.AppendLine("</style>");
        html.AppendLine($"<h1>{EscapeHtml(meta.Value<string>("modelTitle") ?? "")}</h1>");

        if (!string.IsNullOrWhiteSpace(previewName))
        {
            html.AppendLine($"<img src=\"{EscapeHtml(previewName)}\" alt=\"preview\">");
        }

        if (!string.IsNullOrWhiteSpace(meta.Value<string>("aboutThisVersion")))
        {
            html.AppendLine($"<h2>About this version</h2><p>{EscapeHtml(meta.Value<string>("aboutThisVersion") ?? "")}</p>");
        }

        IEnumerable<string> tags = meta["activationTags"]?.Values<string>().OfType<string>() ?? Array.Empty<string>();
        if (tags.Any())
        {
            html.Append("<h2>Activation Tags</h2>");
            foreach (string tag in tags)
            {
                html.Append($"<span class=\"tag\">{EscapeHtml(tag)}</span>");
            }
            html.AppendLine();
        }

        html.AppendLine($"<hr><p><small>Generated by <b>Arc en Ciel Link</b><br>sha256: {EscapeHtml(hash)}</small></p></html>");
        WriteText(Path.ChangeExtension(modelPath, ".arcenciel.html"), html.ToString(), missingOnly);
    }

    private static async Task<JObject?> FetchSidecarMetaAsync(
        ArcEnCielLinkConfig config,
        HttpClient http,
        IEnumerable<string> hashes,
        CancellationToken token
    )
    {
        string url = $"{config.BaseUrl.TrimEnd('/')}/sidecars/meta";
        JObject payload = new()
        {
            ["hashes"] = new JArray(hashes)
        };

        HttpRequestMessage request = new(HttpMethod.Post, url)
        {
            Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json")
        };
        ArcEnCielLinkWorker.ApplyAuthHeaders(request, config);

        try
        {
            HttpResponseMessage response = await http.SendAsync(request, token);
            if (!response.IsSuccessStatusCode)
            {
                Logs.Error($"[AEC-LINK] Sidecar meta fetch failed: {response.StatusCode}");
                return null;
            }

            string json = await response.Content.ReadAsStringAsync(token);
            return JObject.Parse(json);
        }
        catch (Exception ex)
        {
            Logs.Error($"[AEC-LINK] Sidecar meta fetch failed: {ex.Message}");
            return null;
        }
    }

    private static async Task<string?> SavePreviewAsync(HttpClient http, string? url, string modelPath, CancellationToken token, bool keepExisting = false)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        string previewPath = Path.ChangeExtension(modelPath, ".preview.png");
        if (File.Exists(previewPath))
        {
            if (keepExisting) return Path.GetFileName(previewPath);
            previewPath = ArcEnCielLinkWorker.UniqueFilename(Path.GetDirectoryName(previewPath) ?? ".", Path.GetFileName(previewPath));
        }

        string? temporary = null;
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo)) throw new IOException("Invalid preview URL");
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using HttpResponseMessage response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            temporary = Path.Combine(Path.GetDirectoryName(previewPath)!, $".aec-sidecar-{Guid.NewGuid():N}.tmp");
            await using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write))
            await using (Stream input = await response.Content.ReadAsStreamAsync(timeout.Token))
            {
                byte[] buffer = new byte[65536]; int size; long total = 0;
                while ((size = await input.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    total += size;
                    if (total > 20 * 1024 * 1024) throw new IOException("Preview too large");
                    await output.WriteAsync(buffer.AsMemory(0, size), timeout.Token);
                }
            }
            token.ThrowIfCancellationRequested();
            PublishMissing(temporary, previewPath);
            return Path.GetFileName(previewPath);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            Logs.Error("[AEC-LINK] Preview download failed");
            return null;
        }
        finally { if (temporary is not null) File.Delete(temporary); }
    }

    private static void PublishMissing(string temporary, string path)
    {
        try { File.Move(temporary, path, overwrite: false); }
        catch (IOException) when (File.Exists(path)) { }
    }

    private static void WriteText(string path, string text, bool missingOnly)
    {
        if (!missingOnly) { File.WriteAllText(path, text, Encoding.UTF8); return; }
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, $".aec-sidecar-{Guid.NewGuid():N}.tmp");
        try { File.WriteAllText(temporary, text, Encoding.UTF8); PublishMissing(temporary, path); }
        finally { File.Delete(temporary); }
    }

    public static async Task<(bool Changed, bool Warning)> RepairMissingAsync(ArcEnCielLinkWorker worker, ArcEnCielLinkConfig config, HttpClient http, JObject meta, string hash, string modelPath, CancellationToken token)
    {
        string[] paths = new[] { ".json", ".arcenciel.info", ".arcenciel.html", ".preview.png" }.Select(s => Path.ChangeExtension(modelPath, s)).ToArray();
        HashSet<string> existed = paths.Where(File.Exists).ToHashSet();
        token.ThrowIfCancellationRequested();
        bool warning = await WriteSidecarsAsync(worker, config, http, meta, hash, modelPath, token, true);
        token.ThrowIfCancellationRequested();
        JObject info = new() { ["schema"] = 1, ["modelId"] = meta["modelId"], ["versionId"] = meta["versionId"], ["name"] = meta["modelTitle"], ["sha256"] = hash, ["arcencielUrl"] = $"https://arcenciel.io/models/{meta.Value<int?>("modelId")}" };
        WriteText(Path.ChangeExtension(modelPath, ".arcenciel.info"), info.ToString(), true);
        return (paths.Any(p => !existed.Contains(p) && File.Exists(p)), warning);
    }

    private static string EscapeHtml(string value)
    {
        return System.Net.WebUtility.HtmlEncode(value);
    }

    private static bool HasArcEnCielSidecar(string modelPath)
    {
        string jsonPath = Path.ChangeExtension(modelPath, ".json");
        if (!File.Exists(jsonPath))
        {
            return false;
        }

        try
        {
            string content = File.ReadAllText(jsonPath);
            return content.Contains("arcenciel.io/models/", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
