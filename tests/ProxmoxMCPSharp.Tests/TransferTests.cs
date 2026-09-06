using System.Text.Json;
using ProxmoxMCPSharp.Tools;
using Xunit;

namespace ProxmoxMCPSharp.Tests;

public sealed class TransferTests
{
    private static void Configure(ManagementFixture f, string kind, bool clone, bool ha = false, bool loseResponse = false)
    {
        f.Options.ReadOnly = false;
        f.Options.AllowDiskManagement = true;
        f.GuestStatus = "stopped";
        var submitted = false;
        f.Override = async (request, ct) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method != HttpMethod.Get)
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.EndsWith(clone ? "/clone" : "/migrate", path);
                var form = (await request.Content!.ReadAsStringAsync(ct)).Split('&').Select(v => v.Split('=', 2))
                    .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));
                Assert.Equal("node-b", form["target"]);
                Assert.DoesNotContain("digest", form.Keys);
                Assert.DoesNotContain("force", form.Keys);
                if (clone) Assert.Equal("101", form["newid"]);
                submitted = true;
                if (loseResponse) throw new HttpRequestException("synthetic secret detail");
                return Fixture.Json("\"UPID:node-a:123:task:\"");
            }
            if (path.EndsWith("/cluster/resources"))
            {
                var rows = new List<object> { new { vmid = 100, type = kind, node = submitted && !clone ? "node-b" : "node-a", status = f.GuestStatus } };
                if (submitted && clone) rows.Add(new { vmid = 101, type = kind, node = "node-b", status = "stopped" });
                return Fixture.Json(JsonSerializer.Serialize(rows));
            }
            if (path.EndsWith("/cluster/ha/resources")) return Fixture.Json(ha ? JsonSerializer.Serialize(new[] { new { sid = (kind == "qemu" ? "vm:" : "ct:") + "100" } }) : "[]");
            return null;
        };
    }

    private static Task<string> Call(ManagementFixture f, string kind, bool clone, bool dryRun = false, bool online = false, bool restart = false) => (kind, clone) switch
    {
        ("qemu", true) => QemuTools.Clone(f.Client, 100, 101, null, true, null, "node-b", "node-a", dryRun: dryRun, waitSeconds: 0),
        ("qemu", false) => QemuTools.Migrate(f.Client, 100, "node-b", online, "node-a", dryRun: dryRun, waitSeconds: 0),
        ("lxc", true) => LxcTools.Clone(f.Client, 100, 101, ManagementFixture.Digest, targetNode: "node-b", dryRun: dryRun, waitSeconds: 0),
        _ => LxcTools.Migrate(f.Client, 100, "node-b", ManagementFixture.Digest, restartMigration: restart, dryRun: dryRun, waitSeconds: 0)
    };

    [Theory]
    [InlineData("qemu", true)]
    [InlineData("qemu", false)]
    [InlineData("lxc", true)]
    [InlineData("lxc", false)]
    public async Task PreviewReadsOnlyAndSubmissionReportsTaskAndDestination(string kind, bool clone)
    {
        await using var f = new ManagementFixture(); Configure(f, kind, clone);
        f.Options.ReadOnly = true;
        f.Options.AllowDiskManagement = false;
        var preview = Fixture.Parse(await Call(f, kind, clone, dryRun: true));
        Assert.True(preview.GetProperty("canSubmit").GetBoolean());
        Assert.All(f.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
        f.Options.ReadOnly = false;
        f.Options.AllowDiskManagement = true;
        var result = Fixture.Parse(await Call(f, kind, clone));
        Assert.True(result.GetProperty("readback").GetProperty("destinationObserved").GetBoolean());
        Assert.Equal(clone, result.GetProperty("readback").GetProperty("sourceObserved").GetBoolean());
        Assert.Equal("success", result.GetProperty("operation").GetProperty("TaskOutcome").GetString());
        Assert.Single(f.Requests, r => r.Method != HttpMethod.Get);
    }

    [Theory]
    [InlineData("qemu", true)]
    [InlineData("qemu", false)]
    [InlineData("lxc", true)]
    [InlineData("lxc", false)]
    public async Task DestinationAuthorizationIsCheckedBeforeReading(string kind, bool clone)
    {
        await using var f = new ManagementFixture(); Configure(f, kind, clone);
        f.Options.BlockedNodes = ["node-b"];
        await Assert.ThrowsAsync<InvalidOperationException>(() => Call(f, kind, clone));
        Assert.Empty(f.Requests);
        if (clone)
        {
            f.Options.BlockedNodes = [];
            f.Options.BlockedVmIds = [101];
            await Assert.ThrowsAsync<InvalidOperationException>(() => Call(f, kind, clone));
            Assert.Empty(f.Requests);
        }
    }

    [Theory]
    [InlineData("qemu")]
    [InlineData("lxc")]
    public async Task RunningGuestsNeedExplicitMigrationMode(string kind)
    {
        await using var f = new ManagementFixture(); Configure(f, kind, false);
        f.GuestStatus = "running";
        var preview = Fixture.Parse(await Call(f, kind, false, dryRun: true));
        Assert.False(preview.GetProperty("canSubmit").GetBoolean());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Call(f, kind, false));
        Assert.All(f.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
        var result = Fixture.Parse(await Call(f, kind, false, online: true, restart: true));
        Assert.Equal(kind == "qemu" ? "online-migration-requested-with-possible-pause" : "explicit-shutdown-and-restart-requested", result.GetProperty("downtime").GetString());
        var write = Assert.Single(f.Requests, r => r.Method != HttpMethod.Get);
        Assert.Contains(kind == "qemu" ? "online=1" : "restart=1", write.Body);
    }

    [Theory]
    [InlineData("qemu")]
    [InlineData("lxc")]
    public async Task HaMigrationIsBlockedWithoutSubmittingOrStopping(string kind)
    {
        await using var f = new ManagementFixture(); Configure(f, kind, false, ha: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Call(f, kind, false));
        Assert.All(f.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Fact]
    public async Task TemplatesCanBeClonedAndResponseLossDoesNotRetry()
    {
        await using var f = new ManagementFixture(); Configure(f, "lxc", true, loseResponse: true);
        f.Config["template"] = 1;
        var report = await Call(f, "lxc", true);
        Assert.DoesNotContain("secret detail", report);
        Assert.True(Fixture.Parse(report).GetProperty("readback").GetProperty("destinationObserved").GetBoolean());
        Assert.Single(f.Requests, r => r.Method != HttpMethod.Get);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Call(f, "lxc", true));
        Assert.Single(f.Requests, r => r.Method != HttpMethod.Get);
    }
}
