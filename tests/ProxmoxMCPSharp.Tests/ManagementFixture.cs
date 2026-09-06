using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ProxmoxMCPSharp.Configuration;
using ProxmoxMCPSharp.Services;
using Xunit;

namespace ProxmoxMCPSharp.Tests;

internal sealed class ManagementFixture : HttpMessageHandler, IAsyncDisposable
{
    public const string Digest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    public const string NewDigest = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    public const long GiB = 1073741824;
    public ProxmoxOptions Options { get; } = new() { DefaultNode = "node-a", ApiTokenId = "fixture@pve!reader", ApiTokenSecret = "synthetic-token" };
    public ProxmoxClient Client { get; }
    public List<(HttpMethod Method, Uri Uri, string? Body)> Requests { get; } = [];
    public Dictionary<string, object?> Config { get; } = new() { ["digest"] = Digest, ["scsi0"] = "data-a:vm-100-disk-0,size=64G", ["rootfs"] = "data-a:vm-100-disk-0,size=64G" };
    public long VolumeSize { get; set; } = 64 * GiB;
    public long? Available { get; set; } = 100 * GiB;
    public string Format { get; set; } = "raw";
    public string Backend { get; set; } = "lvmthin";
    public int Owner { get; set; } = 100;
    public string GuestStatus { get; set; } = "running";
    public string Pending { get; set; } = "[]";
    public string TaskExit { get; set; } = "OK";
    public string TaskLog { get; set; } = "[]";
    public bool TaskRunning { get; set; }
    public bool LoseWriteResponse { get; set; }
    public bool Synchronous { get; set; }
    public bool SkipReadbackGrowth { get; set; }
    public Action<HttpRequestMessage>? BeforeRequest { get; set; }
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage?>>? Override { get; set; }
    public ManagementFixture() => Client = new(Microsoft.Extensions.Options.Options.Create(Options), new HttpClient(this) { BaseAddress = new Uri("https://pve.example.invalid/") });
    public void EnableResize() { Options.ReadOnly = false; Options.AllowDiskResize = true; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Requests.Add((request.Method, request.RequestUri!, body));
        BeforeRequest?.Invoke(request);
        if (Override is not null && await Override(request, ct) is { } replacement) return replacement;
        var path = request.RequestUri!.AbsolutePath;
        if (request.Method != HttpMethod.Get)
        {
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.EndsWith("/resize", path);
            var form = body!.Split('&').Select(v => v.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));
            Assert.Equal(Digest, form["digest"]);
            Assert.DoesNotContain("+", form["size"]);
            if (!SkipReadbackGrowth)
            {
                VolumeSize = long.Parse(form["size"], System.Globalization.CultureInfo.InvariantCulture);
                Config[form["disk"]] = "data-a:vm-100-disk-0,size=" + VolumeSize;
                Config["digest"] = NewDigest;
            }
            if (LoseWriteResponse) throw new HttpRequestException("synthetic-secret transport detail");
            return Fixture.Json(Synchronous ? "null" : "\"UPID:node-a:123:task:\"");
        }
        if (path.EndsWith("/config")) return Fixture.Json(JsonSerializer.Serialize(Config));
        if (path.EndsWith("/pending")) return Fixture.Json(Pending);
        if (path.EndsWith("/status/current")) return Fixture.Json(JsonSerializer.Serialize(new { status = GuestStatus }));
        if (path.Contains("/tasks/") && path.EndsWith("/status")) return Fixture.Json(JsonSerializer.Serialize(new { status = TaskRunning ? "running" : "stopped", exitstatus = TaskExit }));
        if (path.EndsWith("/log")) return Fixture.Json(TaskLog, 2);
        if (path.EndsWith("/status")) return Fixture.Json(JsonSerializer.Serialize(new { active = 1, enabled = 1, avail = Available, total = 200 * GiB }));
        if (path.EndsWith("/content")) return request.RequestUri.Query.Contains("content=backup") ? Fixture.Json("[]") : Fixture.Json(JsonSerializer.Serialize(new[] {
            new { volid = "data-a:vm-100-disk-0", vmid = Owner, size = VolumeSize, format = Format } }));
        if (path.EndsWith("/storage/data-a")) return Fixture.Json(JsonSerializer.Serialize(new { type = Backend }));
        throw new InvalidOperationException("Unexpected test endpoint.");
    }

    public ValueTask DisposeAsync() => Client.DisposeAsync();
}
