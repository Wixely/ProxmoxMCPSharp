using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ProxmoxMCPSharp.Configuration;
using ProxmoxMCPSharp.Hosting;
using ProxmoxMCPSharp.Services;
using ProxmoxMCPSharp.Tools;
using Xunit;

namespace ProxmoxMCPSharp.Tests;

public sealed class FailoverTests
{
    private const string Pve1 = "https://pve1.example.invalid:8006/";
    private const string Pve2 = "https://pve2.example.invalid:8006/";

    [Fact]
    public async Task PreferredEndpointServesWhileHealthy()
    {
        await using var c = new ClusterFixture();
        Assert.Contains("\"servedBy\":\"pve1\"", await ClusterTools.GetVersion(c.Client, default));
        Assert.All(c.Requests, r => Assert.Equal("pve1", r.Host));
        Assert.Equal(Pve1, c.Client.ActiveEndpoint);
    }

    [Theory]
    [InlineData(NodeState.Down, "unreachable")]
    [InlineData(NodeState.ConnectTimeout, "connect-timeout")]
    [InlineData(NodeState.TlsFailure, "tls-failure")]
    [InlineData(NodeState.Unauthorized, "authentication-rejected")]
    public async Task ReadToolsFailOverAndReportTheDistinctFailure(NodeState state, string failure)
    {
        await using var c = new ClusterFixture();
        c.State["pve1"] = state;
        foreach (var read in new Func<Task<string>>[]
        {
            () => ClusterTools.GetVersion(c.Client, default),
            () => NodeTools.ListNodes(c.Client, default),
            () => ClusterTools.ListClusterResources(c.Client, "vm", default),
            () => QemuTools.ListVms(c.Client, "pve1"),
            () => AptTools.ListUpdates(c.Client, "pve1"),
        })
            Assert.Contains("\"servedBy\":\"pve2\"", await read());

        Assert.Equal(Pve2, c.Client.ActiveEndpoint);
        var pve1 = c.Client.EndpointStatuses().Single(e => e.Url == Pve1);
        Assert.Equal(failure, pve1.LastFailure);
        Assert.Equal("cooling-down", pve1.State);
        // The failed endpoint is tried once, then skipped for the cooldown instead of delaying every call.
        Assert.Single(c.Requests, r => r.Host == "pve1");
    }

    [Fact]
    public async Task PreferredEndpointBecomesEligibleAgainWithoutRestart()
    {
        await using var c = new ClusterFixture();
        c.State["pve1"] = NodeState.Down;
        await ClusterTools.GetVersion(c.Client, default);
        c.State["pve1"] = NodeState.Up;
        c.Time.Advance(29);
        Assert.Contains("pve2", await ClusterTools.GetVersion(c.Client, default));
        c.Time.Advance(2);
        Assert.Contains("pve1", await ClusterTools.GetVersion(c.Client, default));
        Assert.Equal("available", c.Client.EndpointStatuses().Single(e => e.Url == Pve1).State);
    }

    [Fact]
    public async Task RepeatedFailuresBackOffBoundedly()
    {
        await using var c = new ClusterFixture();
        c.State["pve1"] = NodeState.Down;
        var cooldowns = new List<double>();
        for (var i = 0; i < 6; i++)
        {
            await ClusterTools.GetVersion(c.Client, default);
            var status = c.Client.EndpointStatuses().Single(e => e.Url == Pve1);
            cooldowns.Add((status.RetryAfterUtc!.Value - c.Time.Now).TotalSeconds);
            c.Time.Now = status.RetryAfterUtc.Value;
        }
        Assert.Equal(new double[] { 30, 60, 120, 240, 240, 240 }, cooldowns);
    }

    [Fact]
    public async Task AllEndpointsDownFailsWithEachCauseAndRecovers()
    {
        await using var c = new ClusterFixture();
        c.State["pve1"] = NodeState.Down;
        c.State["pve2"] = NodeState.TlsFailure;
        var error = await Assert.ThrowsAsync<ProxmoxEndpointException>(() => ClusterTools.GetVersion(c.Client, default));
        Assert.Equal(ProxmoxEndpointException.AllUnavailable, error.Kind);
        Assert.Equal(new[] { (Pve1, "unreachable"), (Pve2, "tls-failure") }, error.Failures);
        Assert.Contains("pve1.example.invalid", error.Message);
        Assert.DoesNotContain("synthetic-token", error.Message);

        // Both cooling down: they are still tried as a last resort, so the pool recovers without waiting.
        c.State["pve2"] = NodeState.Up;
        Assert.Contains("pve2", await ClusterTools.GetVersion(c.Client, default));
    }

    [Fact]
    public async Task HungEndpointIsBoundedByTheRequestBudget()
    {
        await using var c = new ClusterFixture();
        c.Options.RequestTimeoutSeconds = 1;
        c.State["pve1"] = NodeState.Hang;
        c.State["pve2"] = NodeState.Hang;
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ClusterTools.GetVersion(c.Client, default));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
        // A budget expiry is not an endpoint verdict and does not start a cooldown.
        Assert.All(c.Client.EndpointStatuses(), e => Assert.Null(e.LastFailure));
    }

    [Fact]
    public async Task CallerCancellationStopsWithoutFailover()
    {
        await using var c = new ClusterFixture();
        c.State["pve1"] = NodeState.Hang;
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ClusterTools.GetVersion(c.Client, cancel.Token));
        Assert.DoesNotContain(c.Requests, r => r.Host == "pve2");
        Assert.All(c.Client.EndpointStatuses(), e => Assert.Null(e.LastFailure));
    }

    [Fact]
    public async Task MutationsSelectAHealthyEndpointFirstAndAreSentOnce()
    {
        await using var c = new ClusterFixture();
        c.Options.ReadOnly = false;
        c.State["pve1"] = NodeState.Down;
        await QemuTools.Start(c.Client, 100, "pve1");
        Assert.Equal(new[] { ("pve1", "api2/json/version"), ("pve2", "api2/json/version"), ("pve2", "api2/json/nodes/pve1/qemu/100/status/start") },
            c.Requests.Select(r => (r.Host, r.Path)));

        // A recently healthy endpoint is used directly without another probe.
        c.Requests.Clear();
        await QemuTools.Start(c.Client, 100, "pve1");
        Assert.Equal(new[] { "pve2" }, c.Requests.Select(r => r.Host));
    }

    [Theory]
    [InlineData(NodeState.LoseResponse, ProxmoxEndpointException.MutationIndeterminate)]
    [InlineData(NodeState.Down, ProxmoxEndpointException.MutationNotDelivered)]
    [InlineData(NodeState.TlsFailure, ProxmoxEndpointException.MutationNotDelivered)]
    public async Task MutationTransportFailuresAreNeverReplayed(NodeState state, string kind)
    {
        await using var c = new ClusterFixture();
        c.Options.ReadOnly = false;
        await ClusterTools.GetVersion(c.Client, default);
        c.State["pve1"] = state;
        var error = await Assert.ThrowsAsync<ProxmoxEndpointException>(() => QemuTools.Start(c.Client, 100, "pve1"));
        Assert.Equal(kind, error.Kind);
        Assert.Single(c.Mutations);
        Assert.DoesNotContain(c.Requests, r => r.Host == "pve2");
        Assert.Contains("not", error.Message);
    }

    [Fact]
    public async Task LostManagementResponseIsReportedUnknownWithoutReplay()
    {
        await using var c = new ClusterFixture();
        c.Options.ReadOnly = false;
        await ClusterTools.GetVersion(c.Client, default);
        c.State["pve1"] = NodeState.LoseResponse;
        var outcome = await ManagementOperationProbe.Submit(c.Client);
        Assert.Equal("unknown", outcome);
        Assert.Single(c.Mutations);
    }

    [Fact]
    public async Task TicketIsSharedAcrossClusterEndpointsAndLoginFailsOver()
    {
        await using var c = new ClusterFixture();
        c.Options.ApiTokenId = null;
        c.Options.ApiTokenSecret = null;
        c.Options.Username = "monitor@pve";
        c.Options.Password = "synthetic-password";
        c.State["pve1"] = NodeState.Down;
        await ClusterTools.GetVersion(c.Client, default);
        Assert.Contains(c.Requests, r => r.Host == "pve2" && r.Path == "api2/json/access/ticket");
        c.State["pve1"] = NodeState.Up;
        c.Time.Advance(31);
        await ClusterTools.GetVersion(c.Client, default);
        var reuse = c.Requests.Last();
        Assert.Equal(("pve1", "PVEAuthCookie=PVE:cluster-ticket"), (reuse.Host, reuse.Cookie));
        // One refused attempt on the offline pve1, one login on pve2; pve1 reuses that cluster ticket after recovering.
        Assert.Equal(new[] { "pve1", "pve2" }, c.Requests.Where(r => r.Path == "api2/json/access/ticket").Select(r => r.Host));
    }

    [Fact]
    public async Task ProbeReportsEachEndpointIndependently()
    {
        await using var c = new ClusterFixture();
        c.State["pve2"] = NodeState.Unauthorized;
        var report = JsonDocument.Parse(await EndpointTools.GetEndpoints(c.Client, probe: true)).RootElement;
        Assert.Equal(new[] { "ok", "authentication-rejected" }, report.GetProperty("probe").EnumerateArray().Select(p => p.GetProperty("result").GetString()));
        Assert.Equal(Pve1, report.GetProperty("activeEndpoint").GetString());
        Assert.Equal("authentication-rejected", report.GetProperty("endpoints")[1].GetProperty("lastFailure").GetString());
        Assert.DoesNotContain("synthetic-token", report.GetRawText());
    }

    [Theory]
    [InlineData("ftp://pve1:8006/")]
    [InlineData("pve1:8006")]
    [InlineData("https://user:pw@pve1:8006/")]
    [InlineData("https://pve1:8006/?x=1")]
    [InlineData("")]
    public void InvalidEndpointsAreRejectedAtStartup(string url)
    {
        var options = new ProxmoxOptions { BaseUrls = ["https://pve2:8006/", url] };
        Assert.Throws<InvalidOperationException>(() => new ProxmoxClient(Microsoft.Extensions.Options.Options.Create(options), _ => new HttpClientHandler()));
    }

    [Fact]
    public void LegacyBaseUrlStillWorksAndDuplicatesCollapse()
    {
        var legacy = new ProxmoxClient(Microsoft.Extensions.Options.Options.Create(new ProxmoxOptions { BaseUrl = "https://pve1:8006" }), _ => new HttpClientHandler());
        Assert.Equal(new[] { "https://pve1:8006/" }, legacy.EndpointStatuses().Select(e => e.Url));
        var pool = new ProxmoxClient(Microsoft.Extensions.Options.Options.Create(new ProxmoxOptions
        {
            BaseUrl = "https://ignored:8006/",
            BaseUrls = ["https://pve1:8006", "https://PVE1:8006/", "https://pve2:8006/"],
        }), _ => new HttpClientHandler());
        Assert.Equal(new[] { "https://pve1:8006/", "https://pve2:8006/" }, pool.EndpointStatuses().Select(e => e.Url));
    }

    [Fact]
    public void EveryEndpointKeepsCertificateValidationAndBoundedConnects()
    {
        var strict = ProxmoxClient.BuildHandler(new ProxmoxOptions { EndpointConnectTimeoutSeconds = 3 });
        Assert.Null(strict.SslOptions.RemoteCertificateValidationCallback);
        Assert.Equal(TimeSpan.FromSeconds(3), strict.ConnectTimeout);
        Assert.NotNull(ProxmoxClient.BuildHandler(new ProxmoxOptions { IgnoreCertificateErrors = true }).SslOptions.RemoteCertificateValidationCallback);
    }

    [Fact]
    public async Task ToolResultsIdentifyTheEndpointUsedOverMcp()
    {
        await using var c = new ClusterFixture();
        c.State["pve1"] = NodeState.Down;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(c.Client);
        builder.Services.AddMcpServer().WithHttpTransport().WithToolsFromAssembly(typeof(TaskTools).Assembly).WithProxmoxEndpointMetadata();
        await using var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();
        using var http = app.GetTestClient();
        await using var transport = new HttpClientTransport(new() { Endpoint = new Uri("http://localhost/mcp") }, http);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);

        var result = await client.CallToolAsync("list_nodes", new Dictionary<string, object?>(), cancellationToken: timeout.Token);
        Assert.NotEqual(true, result.IsError);
        Assert.Equal(Pve2, result.Meta![EndpointMetadata.EndpointsKey]![0]!.GetValue<string>());
        var failover = result.Meta[EndpointMetadata.FailoverKey]![0]!;
        Assert.Equal((Pve1, "unreachable"), (failover["endpoint"]!.GetValue<string>(), failover["failure"]!.GetValue<string>()));

        var second = await client.CallToolAsync("list_nodes", new Dictionary<string, object?>(), cancellationToken: timeout.Token);
        Assert.False(second.Meta!.ContainsKey(EndpointMetadata.FailoverKey));
        await app.StopAsync();
    }
}

/// <summary>Runs one guarded management submission through the shared, non-retrying operation helper.</summary>
internal static class ManagementOperationProbe
{
    public static async Task<string> Submit(ProxmoxClient pve)
    {
        var method = typeof(ProxmoxClient).Assembly.GetType("ProxmoxMCPSharp.Services.ManagementOperation")!.GetMethod("Submit")!;
        var task = (Task)method.Invoke(null, [pve, "pve1", "test", HttpMethod.Post, "nodes/pve1/qemu/100/status/start", new Dictionary<string, object?>(), 0, CancellationToken.None])!;
        await task;
        var outcome = task.GetType().GetProperty("Result")!.GetValue(task)!;
        return (string)outcome.GetType().GetProperty("State")!.GetValue(outcome)!;
    }
}
