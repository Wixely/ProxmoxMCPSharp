using System.Net;
using System.Text;
using ProxmoxMCPSharp.Services;
using ProxmoxMCPSharp.Tools;
using Xunit;

namespace ProxmoxMCPSharp.Tests;

public sealed class ReadTests
{
    [Fact]
    public async Task TaskFiltersAndPagingPreserveEnvelope()
    {
        await using var f = new Fixture();
        f.Respond = (r, _) => Task.FromResult(Fixture.Json(r.RequestUri!.Query.Contains("start=500") ? "[{\"type\":\"vzdump\",\"id\":\"100\"}]" : "[]", 501));
        var result = Fixture.Parse(await TaskTools.ListTasks(f.Client, start: 500, since: 1700000000, until: 1800000000, typeFilter: "vzdump", vmid: 100, statusFilter: "ok,error"));
        Assert.Equal(501, result.GetProperty("total").GetInt32());
        Assert.Single(result.GetProperty("data").EnumerateArray());
        var query = f.Requests.Single().Query;
        foreach (var value in new[] { "start=500", "limit=50", "source=archive", "since=1700000000", "until=1800000000", "typefilter=vzdump", "vmid=100", "statusfilter=ok%2Cerror" }) Assert.Contains(value, query);
    }

    [Fact]
    public async Task RunningUsesActivePageAndRetainsUpstreamTotal()
    {
        await using var f = new Fixture();
        f.Respond = (_, _) => Task.FromResult(Fixture.Json("[{\"status\":\"OK\",\"endtime\":12},{\"status\":\"RUNNING\"},{\"upid\":\"active-without-status\"}]", 3));
        var result = Fixture.Parse(await TaskTools.ListTasks(f.Client, running: true, source: "all"));
        Assert.Equal(2, result.GetProperty("data").GetArrayLength());
        Assert.Equal(3, result.GetProperty("total").GetInt32());
        Assert.Contains("source=active", f.Requests[0].Query);
        Assert.DoesNotContain("running", f.Requests[0].Query);
    }

    [Theory]
    [InlineData(0, 0, null, false)]
    [InlineData(501, 0, null, false)]
    [InlineData(50, -1, null, false)]
    [InlineData(50, 0, "invalid", false)]
    [InlineData(50, 0, "archive", true)]
    public async Task InvalidTasksDoNotSend(int limit, int start, string? source, bool running)
    {
        await using var f = new Fixture();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => TaskTools.ListTasks(f.Client, limit: limit, start: start, source: source, running: running));
        Assert.Empty(f.Requests);
    }

    [Fact]
    public async Task LogPagesKeepLineIdentifiersAndMultibyteText()
    {
        await using var f = new Fixture();
        var line = new string('界', 2000);
        f.Respond = (r, _) => Task.FromResult(Fixture.Json(r.RequestUri!.Query.Contains("start=50") ? "[{\"n\":51,\"t\":\"" + line + "\"}]" : "[{\"n\":1,\"t\":\"first\"}]", 51));
        await TaskTools.GetTaskLog(f.Client, "UPID:node-a:123:");
        var result = Fixture.Parse(await TaskTools.GetTaskLog(f.Client, "UPID:node-a:123:", start: 50));
        Assert.Contains("limit=50", f.Requests[0].Query);
        Assert.Contains("UPID%3Anode-a%3A123%3A", f.Requests[0].AbsoluteUri);
        Assert.Equal(51, result.GetProperty("total").GetInt32());
        Assert.Equal(51, result.GetProperty("data")[0].GetProperty("n").GetInt32());
        Assert.Equal(line, result.GetProperty("data")[0].GetProperty("t").GetString());
    }

    [Fact]
    public async Task HardwareAndSyslogRequestsUseSupportedParameters()
    {
        await using var f = new Fixture();
        f.Respond = (_, _) => Task.FromResult(Fixture.Json("{\"health\":\"UNKNOWN\",\"text\":\"vendor data\"}"));
        var smart = await DiskTools.GetSmart(f.Client, "/dev/nvme0n1", healthOnly: true);
        Assert.Contains("UNKNOWN", smart);
        Assert.Contains("disk=%2Fdev%2Fnvme0n1&healthonly=1", f.Requests[^1].Query);
        await NodeTools.ListNodeDisks(f.Client, skipSmart: true, includePartitions: true);
        Assert.Contains("skipsmart=1&include-partitions=1", f.Requests[^1].Query);
        await ZfsTools.GetPoolStatus(f.Client, "pool-a");
        Assert.EndsWith("/disks/zfs/pool-a", f.Requests[^1].AbsolutePath);
        await NodeTools.GetNodeSyslog(f.Client, service: "pvescheduler", start: 20, since: "2024-04-24 10:00");
        Assert.Contains("start=20&limit=200", f.Requests[^1].Query);
        Assert.Contains("since=2024-04-24%2010%3A00", f.Requests[^1].Query);
        await Assert.ThrowsAsync<ArgumentException>(() => ZfsTools.GetPoolStatus(f.Client, "../pool-a"));
    }

    [Fact]
    public async Task DeniedReadsNeverIncludeUpstreamBody()
    {
        await using var f = new Fixture();
        f.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("synthetic-secret-body") });
        var ex = await Assert.ThrowsAsync<ProxmoxApiException>(() => DiskTools.GetSmart(f.Client, "/dev/sda"));
        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.DoesNotContain("synthetic-secret", ex.Message);
    }

    [Fact]
    public async Task ByteBudgetCancellationAndTimeoutAreEnforced()
    {
        await using var f = new Fixture();
        f.Respond = (_, _) => Task.FromResult(Fixture.Json("\"" + new string('界', 100) + "\""));
        await Assert.ThrowsAsync<ResponseBudgetException>(() => f.Client.ReadAsync("nodes", maxBytes: 50));
        f.Respond = async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Fixture.Json("[]"); };
        using var ct = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TaskTools.ListTasks(f.Client, ct: ct.Token));
        f.Options.RequestTimeoutSeconds = 1;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TaskTools.ListTasks(f.Client));
    }

    [Fact]
    public async Task CategoriesNodesAndGuestRulesApplyBeforeReads()
    {
        await using var f = new Fixture();
        f.Options.BlockedVmIds.Add(100);
        await Assert.ThrowsAsync<InvalidOperationException>(() => TaskTools.ListTasks(f.Client, vmid: 100));
        f.Options.BlockedNodes.Add("node-a");
        await Assert.ThrowsAsync<InvalidOperationException>(() => ZfsTools.ListPools(f.Client));
        f.Options.EnableNodes = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => DiskTools.GetSmart(f.Client, "/dev/sda"));
        f.Options.EnableTasks = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => TaskTools.GetTaskLog(f.Client, "UPID:test"));
        Assert.Empty(f.Requests);
    }
}
