using System.Net;
using System.Text.Json;
using ProxmoxMCPSharp.Tools;
using Xunit;

namespace ProxmoxMCPSharp.Tests;

public sealed class ResizeTests
{
    [Theory]
    [InlineData("OK", "success")]
    [InlineData("ERROR", "failure-or-warning")]
    public async Task TerminalStatusSurvivesDeniedTaskLog(string exit, string outcome)
    {
        await using var f = new ManagementFixture(); f.EnableResize();
        f.TaskExit = exit;
        f.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.AbsolutePath.EndsWith("/log")
            ? new(HttpStatusCode.Forbidden) { Content = new StringContent("synthetic-secret") } : null);
        var result = await GuestDiskTools.ResizeLxc(f.Client, 100, "rootfs", "+10G", ManagementFixture.Digest, dryRun: false, waitSeconds: 0);
        var report = Fixture.Parse(result);
        Assert.Equal(outcome, report.GetProperty("operation").GetProperty("TaskOutcome").GetString());
        Assert.Contains("Task log unavailable", result);
        Assert.DoesNotContain("synthetic-secret", result);
        Assert.Equal("unverified", report.GetProperty("guestFilesystem").GetString());
        Assert.Single(f.Requests, r => r.Method != HttpMethod.Get);
    }
    [Fact]
    public async Task PreviewWorksReadOnlyAndResolvesBinaryIncrement()
    {
        await using var f = new ManagementFixture();
        var report = Fixture.Parse(await GuestDiskTools.ResizeQemu(f.Client, 100, "scsi0", "+10G", ManagementFixture.Digest));
        Assert.Equal(74 * ManagementFixture.GiB, report.GetProperty("targetBytes").GetInt64());
        Assert.Equal(64 * ManagementFixture.GiB, report.GetProperty("beforeBytes").GetInt64());
        Assert.Equal("preview", report.GetProperty("outcome").GetString());
        Assert.True(report.GetProperty("canSubmit").GetBoolean());
        Assert.All(f.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Theory]
    [InlineData("qemu", "+10G")]
    [InlineData("qemu", "74G")]
    [InlineData("lxc", "+10G")]
    public async Task GrowthSubmitsOnceAndVerifiesActualCapacity(string kind, string size)
    {
        await using var f = new ManagementFixture();
        f.EnableResize();
        var report = Fixture.Parse(kind == "qemu" ? await GuestDiskTools.ResizeQemu(f.Client, 100, "scsi0", size, ManagementFixture.Digest, dryRun: false, waitSeconds: 0)
            : await GuestDiskTools.ResizeLxc(f.Client, 100, "rootfs", size, ManagementFixture.Digest, dryRun: false, waitSeconds: 0));
        Assert.True(report.GetProperty("capacityVerified").GetBoolean());
        Assert.Equal("success", report.GetProperty("operation").GetProperty("TaskOutcome").GetString());
        Assert.Equal("unverified", report.GetProperty("guestFilesystem").GetString());
        Assert.Single(f.Requests, r => r.Method != HttpMethod.Get);
    }

    [Fact]
    public async Task LostResponseReconcilesWithoutReapplyingIncrement()
    {
        await using var f = new ManagementFixture();
        f.EnableResize(); f.LoseWriteResponse = true;
        var value = await GuestDiskTools.ResizeQemu(f.Client, 100, "scsi0", "+10G", ManagementFixture.Digest, dryRun: false, waitSeconds: 0);
        var report = Fixture.Parse(value);
        Assert.Equal("unknown", report.GetProperty("operation").GetProperty("State").GetString());
        Assert.True(report.GetProperty("capacityVerified").GetBoolean());
        Assert.DoesNotContain("synthetic-secret", value);
        await Assert.ThrowsAsync<InvalidOperationException>(() => GuestDiskTools.ResizeQemu(f.Client, 100, "scsi0", "+10G", ManagementFixture.Digest, dryRun: false));
        Assert.Single(f.Requests, r => r.Method != HttpMethod.Get);
        Assert.Equal(74 * ManagementFixture.GiB, f.VolumeSize);
    }

    [Theory]
    [InlineData("0G")]
    [InlineData("-10G")]
    [InlineData("+0G")]
    [InlineData("63G")]
    [InlineData("64G")]
    [InlineData("9999999999999999999999999999999999999999T")]
    [InlineData("74.00000000001G")]
    public async Task InvalidOrNonGrowingTargetsNeverWrite(string size)
    {
        await using var f = new ManagementFixture(); f.EnableResize();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => GuestDiskTools.ResizeQemu(f.Client, 100, "scsi0", size, ManagementFixture.Digest, dryRun: false));
        Assert.All(f.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Theory]
    [InlineData("readOnly")]
    [InlineData("allowance")]
    [InlineData("category")]
    [InlineData("storage")]
    [InlineData("node")]
    [InlineData("guest")]
    public async Task ClosedGatesDenyBeforeAnyRequest(string gate)
    {
        await using var f = new ManagementFixture(); f.EnableResize();
        switch (gate)
        {
            case "readOnly": f.Options.ReadOnly = true; break;
            case "allowance": f.Options.AllowDiskResize = false; break;
            case "category": f.Options.EnableQemu = false; break;
            case "storage": f.Options.EnableStorage = false; break;
            case "node": f.Options.BlockedNodes.Add("node-a"); break;
            case "guest": f.Options.BlockedVmIds.Add(100); break;
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => GuestDiskTools.ResizeQemu(f.Client, 100, "scsi0", "+10G", ManagementFixture.Digest, dryRun: false));
        Assert.Empty(f.Requests);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("locked")]
    [InlineData("owner")]
    [InlineData("optical")]
    [InlineData("bind")]
    [InlineData("backend")]
    [InlineData("format")]
    [InlineData("capacity")]
    [InlineData("unknownCapacity")]
    [InlineData("digest")]
    [InlineData("changedSize")]
    [InlineData("changedBeforeWrite")]
    public async Task UnsafeEvidencePreventsSubmission(string reason)
    {
        await using var f = new ManagementFixture(); f.EnableResize();
        switch (reason)
        {
            case "pending": f.Pending = "[{\"key\":\"scsi0\",\"delete\":1}]"; break;
            case "locked": f.Config["lock"] = "backup"; break;
            case "owner": f.Owner = 101; break;
            case "optical": f.Config["scsi0"] = "data-a:vm-100-disk-0,media=cdrom"; break;
            case "bind": f.Config["scsi0"] = "/dev/sda,size=64G"; break;
            case "backend": f.Backend = "unknown"; break;
            case "format": f.Format = "vmdk"; break;
            case "capacity": f.Available = 1; break;
            case "unknownCapacity": f.Available = null; break;
            case "digest": f.Config["digest"] = ManagementFixture.NewDigest; break;
            case "changedSize": f.VolumeSize = 65 * ManagementFixture.GiB; break;
            case "changedBeforeWrite":
                var reads = 0;
                f.BeforeRequest = r => { if (r.RequestUri!.AbsolutePath.EndsWith("/config") && ++reads == 2) f.Config["digest"] = ManagementFixture.NewDigest; };
                break;
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => GuestDiskTools.ResizeQemu(f.Client, 100, "scsi0", "+10G", ManagementFixture.Digest, dryRun: false));
        Assert.All(f.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Fact]
    public async Task LxcFilesystemWarningDoesNotBecomeUsableSpaceSuccess()
    {
        await using var f = new ManagementFixture(); f.EnableResize();
        f.TaskLog = "[{\"n\":1,\"t\":\"WARN: Failed to update filesystem; synthetic-secret detail\"}]";
        var report = await GuestDiskTools.ResizeLxc(f.Client, 100, "rootfs", "+10G", ManagementFixture.Digest, dryRun: false, waitSeconds: 0);
        Assert.Contains("warning/failure evidence", report);
        Assert.DoesNotContain("synthetic-secret", report);
        Assert.Equal("unverified", Fixture.Parse(report).GetProperty("guestFilesystem").GetString());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task SynchronousAndRunningTasksRetainHonestReadback(bool synchronous, bool running)
    {
        await using var f = new ManagementFixture(); f.EnableResize();
        f.Synchronous = synchronous; f.TaskRunning = running; f.SkipReadbackGrowth = running;
        var report = Fixture.Parse(await GuestDiskTools.ResizeQemu(f.Client, 100, "scsi0", "+10G", ManagementFixture.Digest, dryRun: false, waitSeconds: 0));
        Assert.Equal(!running, report.GetProperty("capacityVerified").GetBoolean());
        Assert.Equal(running ? "submitted" : "synchronous-response", report.GetProperty("operation").GetProperty("State").GetString());
    }

    [Fact]
    public async Task CurrentAndPendingReadsHaveExplicitSemantics()
    {
        await using var f = new ManagementFixture();
        await QemuTools.GetConfig(f.Client, 100, "node-a", false);
        Assert.Equal("?current=1", f.Requests[^1].Uri.Query);
        await QemuTools.GetConfig(f.Client, 100, "node-a", true);
        Assert.Equal("?current=0", f.Requests[^1].Uri.Query);
        await GuestDiskTools.QemuPending(f.Client, 100);
        Assert.EndsWith("/qemu/100/pending", f.Requests[^1].Uri.AbsolutePath);
        await GuestDiskTools.LxcPending(f.Client, 100);
        Assert.EndsWith("/lxc/100/pending", f.Requests[^1].Uri.AbsolutePath);
    }

    [Theory]
    [InlineData("delete=unused0")]
    [InlineData("scsi0=data-a:10")]
    [InlineData("memory=8192")]
    [InlineData("skiplock=1")]
    public async Task LegacyConfigCannotBypassManagementSafeguards(string values)
    {
        await using var f = new ManagementFixture(); f.Options.ReadOnly = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => QemuTools.UpdateConfig(f.Client, 100, values, "node-a"));
        Assert.Empty(f.Requests);
    }

    [Fact]
    public async Task CancellationAfterAcceptedWriteReturnsUnknownAndNeverRetries()
    {
        await using var f = new ManagementFixture(); f.EnableResize();
        using var cancellation = new CancellationTokenSource();
        f.Override = (request, _) =>
        {
            if (request.Method == HttpMethod.Put)
            {
                f.VolumeSize = 74 * ManagementFixture.GiB;
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        var report = Fixture.Parse(await GuestDiskTools.ResizeQemu(f.Client, 100, "scsi0", "+10G", ManagementFixture.Digest, dryRun: false, ct: cancellation.Token));
        Assert.Equal("unknown", report.GetProperty("operation").GetProperty("State").GetString());
        Assert.False(report.GetProperty("capacityVerified").GetBoolean());
        Assert.Single(f.Requests, r => r.Method == HttpMethod.Put);
        Assert.Equal(HttpMethod.Put, f.Requests[^1].Method);
    }

    [Fact]
    public async Task GuestAgentInspectionIsReadOnlyAndDoesNotAssertFilesystemGrowth()
    {
        await using var f = new ManagementFixture();
        f.Config["agent"] = "1";
        f.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.AbsolutePath.EndsWith("/get-fsinfo")
            ? Fixture.Json("{\"result\":[{\"name\":\"guest-volume\",\"total-bytes\":1234,\"unused-field\":\"synthetic-secret\"}]}") : null);
        var report = await GuestDiskTools.ResizeQemu(f.Client, 100, "scsi0", "+10G", ManagementFixture.Digest);
        Assert.Equal("unverified", Fixture.Parse(report).GetProperty("guestFilesystem").GetString());
        Assert.Equal(1234, Fixture.Parse(report).GetProperty("filesystemEvidence")[0].GetProperty("total-bytes").GetInt32());
        Assert.DoesNotContain("synthetic-secret", report);
        Assert.All(f.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }
}
