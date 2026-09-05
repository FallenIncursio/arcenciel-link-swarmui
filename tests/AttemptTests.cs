using System.Net;
using System.Net.Http;
using Newtonsoft.Json.Linq;
using ArcEnCiel.Link.Swarm;

namespace ContractTests;
internal static class AttemptTests
{
    private sealed class Handler : HttpMessageHandler
    {
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string State = "DOWNLOADING";
        public readonly List<JObject> Requests = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(JObject.Parse(await request.Content!.ReadAsStringAsync(token)));
            return new HttpResponseMessage(Status) { Content = new StringContent(new JObject { ["state"] = State }.ToString()) };
        }
    }
    public static async Task Run()
    {
        static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        using Handler handler = new();
        using HttpClient http = new(handler);
        await using (ArcEnCielLinkAttempt attempt = new(1, "attempt-123", "http://localhost", http, _ => { }, CancellationToken.None))
        {
            await attempt.StartAsync();
            Check(handler.Requests[0].Value<string>("action") == "ack", "Transfer started without ack");
            Check(!attempt.Cancel(2, "attempt-123", ArcEnCielLinkAttempt.RuntimeId), "Wrong job cancelled");
            Check(!attempt.Cancel(1, "old-attempt", ArcEnCielLinkAttempt.RuntimeId), "Wrong attempt cancelled");
            Check(!attempt.Cancel(1, "attempt-123", "old-runtime"), "Wrong runtime cancelled");
            Task blocked = Task.Delay(TimeSpan.FromSeconds(30), attempt.Token);
            Check(attempt.Cancel(1, "attempt-123", ArcEnCielLinkAttempt.RuntimeId), "Cancel was not accepted");
            try { await blocked; throw new Exception("Blocking transfer was not interrupted"); } catch (OperationCanceledException) { }
        }
        Check(handler.Requests.Last().Value<string>("action") == "cancel_ack", "Cancellation cleanup was not acknowledged");
        foreach (HttpStatusCode status in new[] { HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.NotFound, HttpStatusCode.Conflict })
        {
            handler.Status = status;
            await using ArcEnCielLinkAttempt attempt = new(1, "attempt-123", "http://localhost", http, _ => { }, CancellationToken.None);
            bool stopped = false;
            try { await attempt.StartAsync(); } catch (OperationCanceledException) { stopped = true; }
            Check(stopped, "Rejected ack did not fail closed");
        }
        handler.Status = HttpStatusCode.OK;
        int previous = handler.Requests.Count;
        await using (ArcEnCielLinkAttempt legacy = new(1, null, "http://localhost", http, _ => { }, CancellationToken.None))
        {
            await legacy.StartAsync();
            Check(legacy.Cancel(1, null, null), "Legacy cancellation failed");
        }
        Check(handler.Requests.Count == previous, "Legacy job called unsupported lease endpoint");
        Console.WriteLine("PASS: attempt acknowledgement, job/runtime fencing, cancellation and cleanup, rejected claims, legacy compatibility");
    }
}
