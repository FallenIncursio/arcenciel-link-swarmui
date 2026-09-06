using System.Net;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json.Linq;

namespace ArcEnCiel.Link.Swarm;

internal sealed class ArcEnCielLinkAttempt : IAsyncDisposable
{
    private sealed class AttemptEndedException(CancellationToken token) : OperationCanceledException("Download attempt ended", token) { }
    public static readonly string RuntimeId = Guid.NewGuid().ToString();
    private readonly HttpClient _http;
    private readonly Action<HttpRequestMessage> _authenticate;
    private readonly string _url;
    private readonly CancellationTokenSource _stop;
    private readonly CancellationTokenSource _finished = new();
    private Task? _heartbeat;
    public int JobId { get; }
    public string? AttemptId { get; }
    public bool Cancelled { get; private set; }
    public long BytesDownloaded { get; set; }
    public CancellationToken Token => _stop.Token;

    public ArcEnCielLinkAttempt(int jobId, string? attemptId, string baseUrl, HttpClient http,
        Action<HttpRequestMessage> authenticate, CancellationToken shutdown)
    {
        JobId = jobId;
        AttemptId = attemptId;
        _url = $"{baseUrl}/queue/{jobId}/lease";
        _http = http;
        _authenticate = authenticate;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
    }

    public JObject Fields() => new() { ["attemptId"] = AttemptId, ["runtimeId"] = RuntimeId, ["bytesDownloaded"] = BytesDownloaded };

    public async Task StartAsync()
    {
        await RenewAsync("ack");
        if (AttemptId is not null) _heartbeat = HeartbeatAsync();
    }

    public bool Cancel(int jobId, string? attemptId, string? runtimeId)
    {
        if (jobId != JobId || (AttemptId is not null && (attemptId != AttemptId || runtimeId != RuntimeId))) return false;
        Cancelled = true;
        _stop.Cancel();
        return true;
    }

    public async Task RenewAsync(string action = "heartbeat")
    {
        if (AttemptId is null) return;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(action == "cancel_ack" ? 5 : 10));
        JObject body = Fields();
        body["action"] = action;
        using HttpRequestMessage request = new(HttpMethod.Post, _url)
        { Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json") };
        _authenticate(request);
        using HttpResponseMessage response = await _http.SendAsync(request, timeout.Token);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            or HttpStatusCode.NotFound or HttpStatusCode.Conflict)
        {
            JObject result = JObject.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            if (result.Value<string>("state") == "CANCELLED") Cancelled = true;
            _stop.Cancel();
            throw new AttemptEndedException(Token);
        }
        response.EnsureSuccessStatusCode();
        // Stop locally well before the server's 90-second lease can be reassigned.
        if (action != "cancel_ack") _stop.CancelAfter(TimeSpan.FromSeconds(40));
    }

    private async Task HeartbeatAsync()
    {
        while (!_finished.IsCancellationRequested && !Token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), _finished.Token);
                await RenewAsync();
            }
            catch (OperationCanceledException) { return; }
            catch (Exception) { /* The independent cancellation deadline remains in force. */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _finished.Cancel();
        if (_heartbeat is not null) await _heartbeat;
        if (Cancelled && AttemptId is not null)
        {
            for (int retry = 0; retry < 3; retry++)
            {
                try { await RenewAsync("cancel_ack"); break; }
                catch (AttemptEndedException) { break; }
                catch (Exception) { if (retry < 2) await Task.Delay(TimeSpan.FromSeconds(retry + 1)); }
            }
        }
        _finished.Dispose();
        _stop.Dispose();
    }
}
