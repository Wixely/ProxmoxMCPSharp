using System.Net;
using System.Security.Authentication;
using System.Text;
using ProxmoxMCPSharp.Configuration;
using ProxmoxMCPSharp.Services;

namespace ProxmoxMCPSharp.Tests;

public enum NodeState { Up, Down, TlsFailure, ConnectTimeout, Unauthorized, Hang, LoseResponse }

/// <summary>Two or more synthetic cluster API endpoints whose availability can be switched per test.</summary>
internal sealed class ClusterFixture : IAsyncDisposable
{
    public ManualTime Time { get; } = new();
    public ProxmoxOptions Options { get; } = new()
    {
        BaseUrls = ["https://pve1.example.invalid:8006/", "https://pve2.example.invalid:8006/"],
        DefaultNode = "pve1",
        ApiTokenId = "fixture@pve!reader",
        ApiTokenSecret = "synthetic-token",
        RequestTimeoutSeconds = 5,
    };
    public Dictionary<string, NodeState> State { get; } = new() { ["pve1"] = NodeState.Up, ["pve2"] = NodeState.Up };
    public List<(string Host, HttpMethod Method, string Path, string? Cookie)> Requests { get; } = [];
    public IEnumerable<(string Host, HttpMethod Method, string Path, string? Cookie)> Mutations => Requests.Where(r => r.Method != HttpMethod.Get && !r.Path.EndsWith("access/ticket"));

    private ProxmoxClient? _client;
    public ProxmoxClient Client => _client ??= new ProxmoxClient(Microsoft.Extensions.Options.Options.Create(Options), uri => new Node(this, uri.Host.Split('.')[0]), Time);

    public ValueTask DisposeAsync() => _client?.DisposeAsync() ?? ValueTask.CompletedTask;

    private sealed class Node(ClusterFixture cluster, string host) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.PathAndQuery.TrimStart('/');
            lock (cluster.Requests) cluster.Requests.Add((host, request.Method, path, request.Headers.TryGetValues("Cookie", out var c) ? c.Single() : null));
            switch (cluster.State[host])
            {
                case NodeState.Down: throw new HttpRequestException(HttpRequestError.ConnectionError, "synthetic connection refused");
                case NodeState.TlsFailure: throw new HttpRequestException(HttpRequestError.SecureConnectionError, "synthetic handshake failure", new AuthenticationException("untrusted certificate"));
                case NodeState.ConnectTimeout: throw new TaskCanceledException("synthetic connect timeout");
                case NodeState.Unauthorized: return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"data\":null}") };
                case NodeState.Hang: await Task.Delay(Timeout.Infinite, ct); break;
            }
            var response = Respond(request.Method, path);
            if (cluster.State[host] == NodeState.LoseResponse && request.Method != HttpMethod.Get)
                throw new HttpRequestException(HttpRequestError.ResponseEnded, "synthetic lost response");
            return response;
        }

        private HttpResponseMessage Respond(HttpMethod method, string path)
        {
            if (path == "api2/json/access/ticket")
                return Json("{\"ticket\":\"PVE:cluster-ticket\",\"CSRFPreventionToken\":\"csrf\"}");
            if (method != HttpMethod.Get) return Json($"\"UPID:pve1:0001:start:{host}:\"");
            return path switch
            {
                "api2/json/version" => Json($"{{\"version\":\"8.2\",\"servedBy\":\"{host}\"}}"),
                "api2/json/nodes" => Json($"[{{\"node\":\"pve1\",\"servedBy\":\"{host}\"}},{{\"node\":\"pve2\"}}]"),
                _ => Json($"[{{\"servedBy\":\"{host}\"}}]"),
            };
        }

        private static HttpResponseMessage Json(string data) =>
            new(HttpStatusCode.OK) { Content = new StringContent("{\"data\":" + data + "}", Encoding.UTF8, "application/json") };
    }
}

internal sealed class ManualTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(int seconds) => Now = Now.AddSeconds(seconds);
}
