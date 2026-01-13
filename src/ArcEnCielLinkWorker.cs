using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SwarmUI.Utils;

namespace ArcEnCiel.Link.Swarm;

internal sealed class ArcEnCielLinkWorker : IDisposable
{
    private const int HeartbeatIntervalSeconds = 5;
    private const int ProgressMinStep = 2;
    private const double ProgressMinIntervalSeconds = 1.5;
    private const int ReconnectBaseDelaySeconds = 1;
    private const int ReconnectMaxDelaySeconds = 10;
    private const int UnauthorizedCloseCode = 4401;
    private const int RateLimitedCloseCode = 4429;
    private const int ServiceDisabledCloseCode = 1013;

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentQueue<LinkJobPayload> _jobQueue = new();
    private readonly SemaphoreSlim _jobSignal = new(0, int.MaxValue);
    private readonly ManualResetEventSlim _runEvent = new(false);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ArcEnCielLinkHashes _hashes = new();
    private readonly HashSet<string> _knownHashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _knownHashesLock = new();
    private readonly Regex _randomPrefix = new(@"^(?:\d+_|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}_)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private ClientWebSocket? _socket;
    private Task? _socketTask;
    private Task? _workerTask;
    private Task? _inventoryTask;
    private bool _started;
    private bool _socketEnabled = true;
    private bool _credentialsDirty;
    private bool _workerEnabled;
    private int _reconnectAttempts;
    private DateTimeOffset? _suspendUntil;

    private string _baseUrl;
    private string _linkKey;
    private string _apiKey;
    private int _minFreeMb;
    private int _maxRetries;
    private int _backoffBase;
    private bool _saveHtmlPreview;

    public ArcEnCielLinkWorker(ArcEnCielLinkConfig config)
    {
        _baseUrl = (config.BaseUrl ?? "").Trim().TrimEnd('/');
        _linkKey = (config.LinkKey ?? "").Trim();
        _apiKey = (config.ApiKey ?? "").Trim();
        _minFreeMb = config.MinFreeMb;
        _maxRetries = config.MaxRetries;
        _backoffBase = config.BackoffBase;
        _saveHtmlPreview = config.SaveHtmlPreview;
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ArcEnCiel-Link/SwarmUI");
    }

    public ArcEnCielLinkHashes Hashes => _hashes;

    public bool IsWorkerRunning => _workerEnabled;

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _socketTask = Task.Run(() => RunSocketLoopAsync(_shutdown.Token));
        _workerTask = Task.Run(() => RunWorkerLoopAsync(_shutdown.Token));
        _inventoryTask = Task.Run(() => RunInventoryLoopAsync(_shutdown.Token));
    }

    public void UpdateConfig(ArcEnCielLinkConfig config)
    {
        bool credsChanged = false;

        string baseUrl = (config.BaseUrl ?? "").Trim().TrimEnd('/');
        string linkKey = (config.LinkKey ?? "").Trim();
        string apiKey = (config.ApiKey ?? "").Trim();

        if (!string.Equals(_baseUrl, baseUrl, StringComparison.OrdinalIgnoreCase))
        {
            _baseUrl = baseUrl;
            credsChanged = true;
        }

        if (!string.Equals(_linkKey, linkKey, StringComparison.Ordinal))
        {
            _linkKey = linkKey;
            credsChanged = true;
        }

        if (!string.Equals(_apiKey, apiKey, StringComparison.Ordinal))
        {
            _apiKey = apiKey;
            credsChanged = true;
        }

        _minFreeMb = config.MinFreeMb;
        _maxRetries = config.MaxRetries;
        _backoffBase = config.BackoffBase;
        _saveHtmlPreview = config.SaveHtmlPreview;

        if (credsChanged)
        {
            _credentialsDirty = true;
            RequestReconnect();
        }
    }

    public void SetWorkerEnabled(bool enable)
    {
        _workerEnabled = enable;
        if (enable)
        {
            _runEvent.Set();
        }
        else
        {
            _runEvent.Reset();
        }

        _ = SendWorkerStateAsync(_shutdown.Token);
    }

    public async Task GenerateSidecarsAsync()
    {
        ArcEnCielLinkConfig snapshot = new()
        {
            BaseUrl = _baseUrl,
            LinkKey = _linkKey,
            ApiKey = _apiKey,
            SaveHtmlPreview = _saveHtmlPreview,
        };

        await ArcEnCielLinkSidecars.GenerateForExistingAsync(this, snapshot, _http, _shutdown.Token);
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        try
        {
            _runEvent.Set();
            _jobSignal.Release();
        }
        catch
        {
            // ignored
        }

        CloseSocket();
        _sendLock.Dispose();
        _jobSignal.Dispose();
        _runEvent.Dispose();
        _http.Dispose();
    }

    public static void ApplyAuthHeaders(HttpRequestMessage request, ArcEnCielLinkConfig config)
    {
        string linkKey = (config.LinkKey ?? "").Trim();
        string apiKey = (config.ApiKey ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(linkKey))
        {
            request.Headers.TryAddWithoutValidation("x-link-key", linkKey);
        }
        else if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        }
    }

    public static string UniqueFilename(string dir, string name)
    {
        string stem = Path.GetFileNameWithoutExtension(name) ?? "_";
        string ext = Path.GetExtension(name);
        string candidate = Path.Combine(dir, name);
        int idx = 1;
        while (File.Exists(candidate) || File.Exists(candidate + ".part"))
        {
            candidate = Path.Combine(dir, $"{stem}_{idx}{ext}");
            idx += 1;
        }
        return candidate;
    }

    private async Task RunSocketLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (!_socketEnabled || !HasCredentials())
            {
                await Task.Delay(TimeSpan.FromSeconds(1), token);
                continue;
            }

            if (_suspendUntil.HasValue)
            {
                TimeSpan remaining = _suspendUntil.Value - DateTimeOffset.UtcNow;
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(5, remaining.TotalSeconds)), token);
                    continue;
                }
                _suspendUntil = null;
            }

            Uri wsUri;
            try
            {
                wsUri = BuildWsUri();
            }
            catch (Exception ex)
            {
                Logs.Error($"[AEC-LINK] Invalid base URL: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(5), token);
                continue;
            }

            using ClientWebSocket socket = new();
            ConfigureSocket(socket);

            try
            {
                Logs.Info($"[AEC-LINK] Connecting to {wsUri}");
                await socket.ConnectAsync(wsUri, token);
            }
            catch (Exception ex)
            {
                Logs.Error($"[AEC-LINK] WebSocket connect failed: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(NextReconnectDelaySeconds()), token);
                continue;
            }

            _socket = socket;
            _reconnectAttempts = 0;
            _credentialsDirty = false;

            await SendWorkerStateAsync(token);
            await SendPollAsync(token);

            await ReceiveLoopAsync(socket, token);

            HandleSocketClose(socket.CloseStatus, socket.CloseStatusDescription);
            _socket = null;
            CloseSocket();

            await Task.Delay(TimeSpan.FromSeconds(NextReconnectDelaySeconds()), token);
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken token)
    {
        byte[] buffer = new byte[8192];
        while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            string? message = await ReceiveMessageAsync(socket, buffer, token);
            if (message is null)
            {
                break;
            }

            try
            {
                JObject msg = JObject.Parse(message);
                string type = msg.Value<string>("type") ?? "";
                switch (type)
                {
                    case "job":
                        if (TryParseJob(msg, out LinkJobPayload? job))
                        {
                            EnqueueJob(job);
                        }
                        break;
                    case "control":
                        await HandleControlAsync(msg, token);
                        break;
                }
            }
            catch (Exception ex)
            {
                Logs.Error($"[AEC-LINK] WebSocket message error: {ex.Message}");
            }
        }
    }

    private async Task HandleControlAsync(JObject msg, CancellationToken token)
    {
        string? command = msg.Value<string>("command");
        string? requestId = msg.Value<string>("requestId");
        if (string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        if (command == "set_worker_state")
        {
            bool enable = msg["enable"]?.Value<bool?>() ?? true;
            string? linkKey = msg.ContainsKey("linkKey") ? msg.Value<string>("linkKey") ?? "" : null;
            string? apiKey = msg.ContainsKey("apiKey") ? msg.Value<string>("apiKey") ?? "" : null;

            if (linkKey is not null && !string.IsNullOrWhiteSpace(linkKey) && !IsValidLinkKey(linkKey))
            {
                await SendMessageAsync(
                    new
                    {
                        type = "control_ack",
                        command,
                        requestId,
                        ok = false,
                        message = "Invalid link key format"
                    },
                    token
                );
                return;
            }

            ArcEnCielLinkRuntime.ApplyWorkerState(enable, linkKey, apiKey);

            await SendMessageAsync(
                new
                {
                    type = "control_ack",
                    command,
                    requestId,
                    ok = true,
                    enable,
                    running = _workerEnabled
                },
                token
            );
            return;
        }

        if (command == "list_subfolders")
        {
            string kind = (msg.Value<string>("kind") ?? "").Trim().ToLowerInvariant();
            try
            {
                IReadOnlyList<string> folders = ArcEnCielLinkPaths.ListSubfolders(kind);
                await SendMessageAsync(
                    new
                    {
                        type = "folders_result",
                        requestId,
                        ok = true,
                        kind,
                        folders
                    },
                    token
                );
            }
            catch (Exception ex)
            {
                await SendMessageAsync(
                    new
                    {
                        type = "folders_result",
                        requestId,
                        ok = false,
                        error = ex.Message,
                        kind
                    },
                    token
                );
            }
        }
    }

    private async Task RunWorkerLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            _runEvent.Wait(token);

            bool signaled = await _jobSignal.WaitAsync(TimeSpan.FromSeconds(HeartbeatIntervalSeconds), token);
            if (!signaled)
            {
                await SendPollAsync(token);
                continue;
            }

            if (!_jobQueue.TryDequeue(out LinkJobPayload? job))
            {
                continue;
            }

            await ProcessJobAsync(job, token);
        }
    }

    private async Task RunInventoryLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            _runEvent.Wait(token);
            try
            {
                List<string> hashes = _hashes.ListModelHashes();
                await SyncInventoryAsync(hashes, token);
            }
            catch (Exception ex)
            {
                Logs.Error($"[AEC-LINK] Inventory update failed: {ex.Message}");
            }

            await Task.Delay(TimeSpan.FromHours(1), token);
        }
    }

    private async Task ProcessJobAsync(LinkJobPayload job, CancellationToken token)
    {
        string? url = job.DownloadUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            await ReportProgressAsync(job.Id, state: "ERROR", message: "No download URL provided by server", token: token);
            return;
        }

        url = ResolveDownloadUrl(url);
        string urlPath = Uri.UnescapeDataString(new Uri(url).AbsolutePath);
        string rawName = Path.GetFileName(urlPath);
        string cleanName = _randomPrefix.Replace(rawName, "");
        if (string.IsNullOrWhiteSpace(cleanName))
        {
            cleanName = rawName;
        }

        string targetDir;
        try
        {
            targetDir = ArcEnCielLinkPaths.ResolveTargetPath(job.TargetPath);
        }
        catch (Exception ex)
        {
            await ReportProgressAsync(job.Id, state: "ERROR", message: ex.Message, token: token);
            return;
        }

        Directory.CreateDirectory(targetDir);
        if (!HasEnoughFreeSpace(targetDir))
        {
            await ReportProgressAsync(job.Id, state: "ERROR", message: $"Less than {_minFreeMb} MB free", token: token);
            return;
        }

        string destPath = UniqueFilename(targetDir, cleanName);
        string tmpPath = destPath + ".part";

        await ReportProgressAsync(job.Id, progress: 0, state: "DOWNLOADING", token: token);

        Dictionary<string, object> progressState = new()
        {
            ["pct"] = 0,
            ["ts"] = DateTimeOffset.UtcNow
        };

        void ProgressCallback(double fraction)
        {
            int pct = Math.Clamp((int)(fraction * 100), 0, 100);
            int lastPct = (int)progressState["pct"];
            DateTimeOffset lastTs = (DateTimeOffset)progressState["ts"];
            if (pct != 0 && pct != 100)
            {
                if (pct - lastPct < ProgressMinStep && (DateTimeOffset.UtcNow - lastTs).TotalSeconds < ProgressMinIntervalSeconds)
                {
                    return;
                }
            }

            progressState["pct"] = pct;
            progressState["ts"] = DateTimeOffset.UtcNow;
            _ = ReportProgressAsync(job.Id, progress: pct, token: _shutdown.Token);
        }

        try
        {
            await DownloadWithRetryAsync(url, tmpPath, ProgressCallback, token);
            string shaLocal = ArcEnCielLinkHashesCompute(tmpPath);
            if (!string.IsNullOrWhiteSpace(job.Sha256) && !string.Equals(job.Sha256, shaLocal, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(tmpPath);
                await ReportProgressAsync(job.Id, state: "ERROR", message: "SHA-256 mismatch", token: token);
                return;
            }

            File.Move(tmpPath, destPath);

            if (job.Meta is not null)
            {
                ArcEnCielLinkConfig snapshot = new()
                {
                    BaseUrl = _baseUrl,
                    LinkKey = _linkKey,
                    ApiKey = _apiKey,
                    SaveHtmlPreview = _saveHtmlPreview,
                };

                await ArcEnCielLinkSidecars.WriteSidecarsAsync(this, snapshot, _http, job.Meta, shaLocal, destPath, token);
            }

            List<string> hashes = _hashes.UpdateCachedHash(destPath, shaLocal);
            await SyncInventoryAsync(hashes, token);
            await ReportProgressAsync(job.Id, progress: 100, state: "DONE", token: token);
        }
        catch (Exception ex)
        {
            Logs.Error($"[AEC-LINK] Job failed: {ex.Message}");
            try
            {
                if (File.Exists(tmpPath))
                {
                    File.Delete(tmpPath);
                }
            }
            catch
            {
                // ignored
            }
            await ReportProgressAsync(job.Id, state: "ERROR", message: ex.Message, token: token);
        }
    }

    private async Task DownloadWithRetryAsync(string url, string tmpPath, Action<double> progress, CancellationToken token)
    {
        for (int attempt = 1; attempt <= _maxRetries; attempt++)
        {
            try
            {
                await DownloadFileAsync(url, tmpPath, progress, token);
                return;
            }
            catch
            {
                try
                {
                    if (File.Exists(tmpPath))
                    {
                        File.Delete(tmpPath);
                    }
                }
                catch
                {
                    // ignored
                }

                if (attempt == _maxRetries)
                {
                    throw;
                }

                double delay = Math.Pow(_backoffBase, attempt) + new Random().NextDouble();
                await Task.Delay(TimeSpan.FromSeconds(delay), token);
            }
        }
    }

    private async Task DownloadFileAsync(string url, string tmpPath, Action<double> progress, CancellationToken token)
    {
        using HttpResponseMessage response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();

        long total = response.Content.Headers.ContentLength ?? 0;
        await using Stream stream = await response.Content.ReadAsStreamAsync(token);
        await using FileStream output = new(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None);
        byte[] buffer = new byte[1024 * 1024];
        long read = 0;

        while (true)
        {
            int bytes = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token);
            if (bytes == 0)
            {
                break;
            }

            await output.WriteAsync(buffer.AsMemory(0, bytes), token);
            read += bytes;
            if (total > 0)
            {
                progress(read / (double)total);
            }
        }
    }

    private async Task ReportProgressAsync(int jobId, int? progress = null, string? state = null, string? message = null, CancellationToken token = default)
    {
        Dictionary<string, object?> payload = new()
        {
            ["type"] = "progress",
            ["jobId"] = jobId
        };
        if (progress.HasValue) payload["progress"] = progress.Value;
        if (!string.IsNullOrWhiteSpace(state)) payload["state"] = state;
        if (!string.IsNullOrWhiteSpace(message)) payload["message"] = message;

        if (await SendMessageAsync(payload, token))
        {
            if (state == "DONE")
            {
                await SendPollAsync(token);
            }
            return;
        }

        string url = $"{_baseUrl}/queue/{jobId}/progress";
        JObject body = new();
        if (progress.HasValue) body["progress"] = progress.Value;
        if (!string.IsNullOrWhiteSpace(state)) body["state"] = state;
        if (!string.IsNullOrWhiteSpace(message)) body["message"] = message;

        using HttpRequestMessage request = new(HttpMethod.Patch, url)
        {
            Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json")
        };
        ApplyAuthHeaders(request);
        try
        {
            await _http.SendAsync(request, token);
        }
        catch (Exception ex)
        {
            Logs.Error($"[AEC-LINK] Progress update failed: {ex.Message}");
        }
    }

    private async Task SyncInventoryAsync(List<string> hashes, CancellationToken token)
    {
        bool changed;
        lock (_knownHashesLock)
        {
            if (hashes.Count == _knownHashes.Count && _knownHashes.SetEquals(hashes))
            {
                changed = false;
            }
            else
            {
                _knownHashes.Clear();
                foreach (string hash in hashes)
                {
                    _knownHashes.Add(hash);
                }
                changed = true;
            }
        }

        if (!changed)
        {
            return;
        }

        Dictionary<string, object> payload = new()
        {
            ["type"] = "inventory",
            ["hashes"] = hashes
        };

        if (await SendMessageAsync(payload, token))
        {
            return;
        }

        string url = $"{_baseUrl}/inventory";
        JObject body = new() { ["hashes"] = JArray.FromObject(hashes) };
        using HttpRequestMessage request = new(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json")
        };
        ApplyAuthHeaders(request);
        try
        {
            await _http.SendAsync(request, token);
        }
        catch (Exception ex)
        {
            Logs.Error($"[AEC-LINK] Inventory update failed: {ex.Message}");
        }
    }

    private async Task SendWorkerStateAsync(CancellationToken token)
    {
        Dictionary<string, object> payload = new()
        {
            ["type"] = "worker_state",
            ["running"] = _workerEnabled
        };
        await SendMessageAsync(payload, token);
    }

    private async Task SendPollAsync(CancellationToken token)
    {
        Dictionary<string, object> payload = new()
        {
            ["type"] = "poll"
        };
        await SendMessageAsync(payload, token);
    }

    private async Task<bool> SendMessageAsync(object payload, CancellationToken token)
    {
        ClientWebSocket? socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open)
        {
            return false;
        }

        string json = JsonSerializer.Serialize(payload);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        await _sendLock.WaitAsync(token);
        try
        {
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
            return true;
        }
        catch (Exception ex)
        {
            Logs.Error($"[AEC-LINK] WebSocket send failed: {ex.Message}");
            return false;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void EnqueueJob(LinkJobPayload job)
    {
        _jobQueue.Enqueue(job);
        _jobSignal.Release();
    }

    private static bool TryParseJob(JObject msg, out LinkJobPayload? job)
    {
        job = null;
        if (msg["data"] is not JObject data)
        {
            return false;
        }

        int id = data.Value<int?>("id") ?? 0;
        string? targetPath = data.Value<string>("targetPath");
        if (id <= 0 || string.IsNullOrWhiteSpace(targetPath))
        {
            return false;
        }

        JObject? version = data["version"] as JObject;
        string? url = version?.Value<string>("externalDownloadUrl") ?? version?.Value<string>("filePath");
        string? sha = version?.Value<string>("sha256");
        JObject? meta = version?["meta"] as JObject;

        job = new LinkJobPayload
        {
            Id = id,
            TargetPath = targetPath,
            DownloadUrl = url,
            Sha256 = sha,
            Meta = meta
        };
        return true;
    }

    private bool HasCredentials()
    {
        return !string.IsNullOrWhiteSpace(_linkKey) || !string.IsNullOrWhiteSpace(_apiKey);
    }

    private Uri BuildWsUri()
    {
        Uri baseUri = new(_baseUrl);
        string scheme = baseUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
        string path = baseUri.AbsolutePath.TrimEnd('/') + "/ws";
        UriBuilder builder = new(baseUri)
        {
            Scheme = scheme,
            Path = path,
        };
        string query = builder.Query;
        string modeParam = "mode=worker";
        builder.Query = string.IsNullOrWhiteSpace(query) ? modeParam : query.TrimStart('?') + "&" + modeParam;
        return builder.Uri;
    }

    private void ConfigureSocket(ClientWebSocket socket)
    {
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

        if (!string.IsNullOrWhiteSpace(_linkKey))
        {
            socket.Options.SetRequestHeader("x-link-key", _linkKey);
            string proto = BuildSubprotocol("link-key", _linkKey);
            if (!string.IsNullOrWhiteSpace(proto))
            {
                socket.Options.AddSubProtocol(proto);
            }
        }
        else if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            socket.Options.SetRequestHeader("x-api-key", _apiKey);
            string proto = BuildSubprotocol("api-key", _apiKey);
            if (!string.IsNullOrWhiteSpace(proto))
            {
                socket.Options.AddSubProtocol(proto);
            }
        }
    }

    private static string BuildSubprotocol(string kind, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string encoded = EncodeProtocolValue(value);
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return string.Empty;
        }

        return $"aec-link.{kind}.{encoded}";
    }

    private static string EncodeProtocolValue(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        string base64 = Convert.ToBase64String(bytes);
        return base64.Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private async Task<string?> ReceiveMessageAsync(ClientWebSocket socket, byte[] buffer, CancellationToken token)
    {
        using MemoryStream stream = new();
        while (true)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private void HandleSocketClose(WebSocketCloseStatus? status, string? reason)
    {
        if (!status.HasValue)
        {
            return;
        }

        if (status == (WebSocketCloseStatus)UnauthorizedCloseCode)
        {
            Logs.Error("[AEC-LINK] Authentication failed; check Link key or API key.");
            _suspendUntil = DateTimeOffset.UtcNow.AddMinutes(10);
            return;
        }

        if (status == (WebSocketCloseStatus)RateLimitedCloseCode)
        {
            double waitSeconds = ParseRetryAfter(reason) ?? 900;
            Logs.Warning($"[AEC-LINK] Rate limited; retrying in {Math.Floor(waitSeconds)}s.");
            _suspendUntil = DateTimeOffset.UtcNow.AddSeconds(waitSeconds);
            return;
        }

        if (status == (WebSocketCloseStatus)ServiceDisabledCloseCode)
        {
            Logs.Warning("[AEC-LINK] Link service disabled by server; retrying shortly.");
            _suspendUntil = DateTimeOffset.UtcNow.AddSeconds(30);
        }
    }

    private static double? ParseRetryAfter(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return null;
        }

        if (reason.StartsWith("RATE_LIMITED:", StringComparison.OrdinalIgnoreCase))
        {
            string value = reason.Split(':', 2)[1];
            if (double.TryParse(value, out double seconds))
            {
                return seconds;
            }
        }

        return null;
    }

    private int NextReconnectDelaySeconds()
    {
        int delay = Math.Min(ReconnectMaxDelaySeconds, ReconnectBaseDelaySeconds * (1 << _reconnectAttempts));
        _reconnectAttempts = Math.Min(_reconnectAttempts + 1, 6);
        return delay;
    }

    private void RequestReconnect()
    {
        if (_credentialsDirty)
        {
            CloseSocket();
        }
    }

    private void CloseSocket()
    {
        try
        {
            if (_socket is { State: WebSocketState.Open })
            {
                _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None).Wait(TimeSpan.FromSeconds(1));
            }
        }
        catch
        {
            // ignored
        }
        finally
        {
            _socket?.Dispose();
            _socket = null;
        }
    }

    private void ApplyAuthHeaders(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(_linkKey))
        {
            request.Headers.TryAddWithoutValidation("x-link-key", _linkKey);
        }
        else if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            request.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
        }
    }

    private string ResolveDownloadUrl(string urlRaw)
    {
        if (urlRaw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            urlRaw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return urlRaw;
        }

        int apiIndex = _baseUrl.IndexOf("/api/", StringComparison.OrdinalIgnoreCase);
        string root = apiIndex >= 0 ? _baseUrl[..apiIndex] : _baseUrl;
        Uri baseUri = new(root.TrimEnd('/') + "/");
        return new Uri(baseUri, urlRaw.TrimStart('/')).ToString();
    }

    private bool HasEnoughFreeSpace(string path)
    {
        try
        {
            string root = Path.GetPathRoot(path) ?? path;
            DriveInfo drive = new(root);
            long freeMb = drive.AvailableFreeSpace / (1024 * 1024);
            return freeMb >= _minFreeMb;
        }
        catch
        {
            return true;
        }
    }

    private static string ArcEnCielLinkHashesCompute(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using var sha = System.Security.Cryptography.SHA256.Create();
        byte[] hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool IsValidLinkKey(string value)
    {
        return Regex.IsMatch(value, "^lk_[A-Za-z0-9_-]{32}$");
    }

    private sealed class LinkJobPayload
    {
        public int Id { get; init; }
        public string TargetPath { get; init; } = "";
        public string? DownloadUrl { get; init; }
        public string? Sha256 { get; init; }
        public JObject? Meta { get; init; }
    }
}
