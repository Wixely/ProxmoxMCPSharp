using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ProxmoxMCPSharp.Configuration;
using ProxmoxMCPSharp.Services;
using Xunit;

namespace ProxmoxMCPSharp.Tests;

internal sealed class Fixture : HttpMessageHandler, IAsyncDisposable
{
    public ProxmoxOptions Options { get; } = new() { DefaultNode = "node-a", ApiTokenId = "fixture@pve!reader", ApiTokenSecret = "synthetic-token" };
    public List<Uri> Requests { get; } = [];
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } = (_, _) => Task.FromResult(Json("[]"));
    public ProxmoxClient Client { get; }
    public Fixture() => Client = new ProxmoxClient(Microsoft.Extensions.Options.Options.Create(Options), new HttpClient(this) { BaseAddress = new Uri("https://pve.example.invalid/") });
    public static HttpResponseMessage Json(string data, int? total = null) => new(HttpStatusCode.OK) {
        Content = new StringContent("{\"data\":" + data + (total.HasValue ? ",\"total\":" + total : "") + "}", Encoding.UTF8, "application/json") };
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Assert.Equal(HttpMethod.Get, request.Method);
        Requests.Add(request.RequestUri!);
        return Respond(request, cancellationToken);
    }
    public ValueTask DisposeAsync() => Client.DisposeAsync();
    public static JsonElement Parse(string value) => JsonDocument.Parse(value).RootElement.Clone();
}
