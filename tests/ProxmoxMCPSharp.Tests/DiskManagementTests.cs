using System.Text.Json;
using ProxmoxMCPSharp.Services;
using ProxmoxMCPSharp.Tools;
using Xunit;

namespace ProxmoxMCPSharp.Tests;

public sealed class DiskManagementTests
{
    private sealed record DiskChange(string Kind, int Vmid, string Action, string Disk, string ExpectedDigest,
        string? Storage, decimal SizeGiB, string Unused, string MountPath, bool DryRun, int WaitSeconds,
        bool DeleteSource = false, bool? IoThread = null);

    // Exercise the public tool boundary, including its translation to the shared service.
    private sealed class DiskToolCalls(ProxmoxClient client)
    {
        public Task<string> Execute(DiskChange c, CancellationToken ct) => (c.Kind, c.Action) switch
        {
            ("qemu", "add") => GuestDiskManagementTools.AddQemu(client, c.Vmid, c.Disk, c.Storage!, c.SizeGiB, c.ExpectedDigest, ioThread: c.IoThread, dryRun: c.DryRun, waitSeconds: c.WaitSeconds, ct: ct),
            ("qemu", "attach") => GuestDiskManagementTools.AttachQemu(client, c.Vmid, c.Disk, c.Unused, c.ExpectedDigest, dryRun: c.DryRun, waitSeconds: c.WaitSeconds, ct: ct),
            ("qemu", "detach") => GuestDiskManagementTools.DetachQemu(client, c.Vmid, c.Disk, c.ExpectedDigest, dryRun: c.DryRun, waitSeconds: c.WaitSeconds, ct: ct),
            ("qemu", "move") => GuestDiskManagementTools.MoveQemu(client, c.Vmid, c.Disk, c.Storage!, c.ExpectedDigest, deleteSource: c.DeleteSource, dryRun: c.DryRun, waitSeconds: c.WaitSeconds, ct: ct),
            ("lxc", "add") => GuestDiskManagementTools.AddLxcMount(client, c.Vmid, c.Disk, c.Storage!, c.SizeGiB, c.MountPath, c.ExpectedDigest, dryRun: c.DryRun, waitSeconds: c.WaitSeconds, ct: ct),
            ("lxc", "detach") => GuestDiskManagementTools.DetachLxcMount(client, c.Vmid, c.Disk, c.ExpectedDigest, dryRun: c.DryRun, waitSeconds: c.WaitSeconds, ct: ct),
            ("lxc", "move") => GuestDiskManagementTools.MoveLxc(client, c.Vmid, c.Disk, c.Storage!, c.ExpectedDigest, deleteSource: c.DeleteSource, dryRun: c.DryRun, waitSeconds: c.WaitSeconds, ct: ct),
            _ => throw new InvalidOperationException("Unexpected test action.")
        };
    }

    private static DiskChange Change(string kind, string action, bool dryRun = true) =>
        new(kind, 100, action, kind == "qemu" ? "scsi1" : "mp0", ManagementFixture.Digest,
            Storage: action is "add" or "move" ? "data-b" : null, SizeGiB: 8, Unused: "unused0",
            MountPath: "/srv/data", DryRun: dryRun, WaitSeconds: 0);

    private static void Configure(ManagementFixture f, string kind, string action, bool pending = false, bool loseResponse = false)
    {
        f.Options.ReadOnly = false;
        f.Options.AllowDiskManagement = true;
        f.GuestStatus = "stopped";
        f.Config.Clear();
        f.Config["digest"] = ManagementFixture.Digest;
        f.Config["boot"] = "order=scsi0";
        var slot = kind == "qemu" ? "scsi1" : "mp0";
        const string source = "data-a:vm-100-disk-0";
        const string target = "data-b:vm-100-disk-1";
        var sourceVisible = true;
        var targetVisible = false;
        if (action != "add") f.Config[action == "attach" ? "unused0" : slot] = source + (kind == "lxc" ? ",mp=/srv/data" : "");
        f.Override = async (request, ct) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method != HttpMethod.Get)
            {
                Assert.Equal(action == "move" ? HttpMethod.Post : HttpMethod.Put, request.Method);
                Assert.EndsWith(action == "move" ? kind == "qemu" ? "/move_disk" : "/move_volume" : "/config", path);
                var form = (await request.Content!.ReadAsStringAsync(ct)).Split('&').Select(v => v.Split('=', 2))
                    .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));
                Assert.Equal(ManagementFixture.Digest, form["digest"]);
                Assert.DoesNotContain("force", form.Keys);
                if (action == "detach")
                {
                    Assert.Equal(slot, form["delete"]);
                    f.Config.Remove(slot);
                    f.Config["unused0"] = source;
                }
                else if (action == "attach")
                {
                    Assert.StartsWith(source, form[slot]);
                    f.Config.Remove("unused0");
                    f.Config[slot] = form[slot];
                }
                else
                {
                    if (action == "move")
                    {
                        Assert.Equal(slot, form[kind == "qemu" ? "disk" : "volume"]);
                        Assert.Equal("data-b", form["storage"]);
                        Assert.DoesNotContain("target-vmid", form.Keys);
                        sourceVisible = form["delete"] == "0";
                        if (sourceVisible) f.Config["unused0"] = source;
                    }
                    else Assert.StartsWith("data-b:8,", form[slot]);
                    targetVisible = true;
                    if (pending) f.Pending = JsonSerializer.Serialize(new[] { new { key = slot, pending = target } });
                    else f.Config[slot] = target;
                }
                f.Config["digest"] = ManagementFixture.NewDigest;
                if (loseResponse) throw new HttpRequestException("synthetic sensitive upstream detail");
                return Fixture.Json("\"UPID:node-a:123:task:\"");
            }
            if (path.EndsWith("/storage/data-b")) return Fixture.Json("{\"type\":\"lvmthin\",\"content\":\"images,rootdir\"}");
            if (path.EndsWith("/content"))
            {
                var isTarget = path.Contains("/data-b/");
                return Fixture.Json(JsonSerializer.Serialize((isTarget ? targetVisible : sourceVisible)
                    ? new[] { new { volid = isTarget ? target : source, vmid = f.Owner, size = 64 * ManagementFixture.GiB, format = "raw" } }
                    : []));
            }
            return null;
        };
    }

    [Theory]
    [InlineData("qemu", "add")]
    [InlineData("qemu", "attach")]
    [InlineData("qemu", "detach")]
    [InlineData("qemu", "move")]
    [InlineData("lxc", "add")]
    [InlineData("lxc", "detach")]
    [InlineData("lxc", "move")]
    public async Task PreviewsUseOnlyReadsAndWritesReportObservedState(string kind, string action)
    {
        await using var f = new ManagementFixture(); Configure(f, kind, action);
        f.Options.ReadOnly = true;
        f.Options.AllowDiskManagement = false;
        var service = new DiskToolCalls(f.Client);
        var preview = Fixture.Parse(await service.Execute(Change(kind, action), default));
        Assert.True(preview.GetProperty("canSubmit").GetBoolean());
        Assert.All(f.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
        f.Options.ReadOnly = false;
        f.Options.AllowDiskManagement = true;
        var result = Fixture.Parse(await service.Execute(Change(kind, action, false), default));
        var readback = result.GetProperty("readback");
        Assert.True(readback.GetProperty("requestedConfigurationActive").GetBoolean());
        if (action is "detach" or "move")
        {
            Assert.True(readback.GetProperty("sourceRetentionVerified").GetBoolean());
            Assert.Equal("unused0", readback.GetProperty("retainedUnusedReferences")[0].GetString());
        }
        if (action is "add" or "move") Assert.True(readback.GetProperty("targetCapacityVerified").GetBoolean());
        Assert.Single(f.Requests, r => r.Method != HttpMethod.Get);
    }

    [Fact]
    public async Task SourceDeletionRequiresSeparateGateAndReportsOnlyVisibility()
    {
        await using var f = new ManagementFixture(); Configure(f, "qemu", "move");
        var change = Change("qemu", "move", false) with { DeleteSource = true };
        var service = new DiskToolCalls(f.Client);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.Execute(change, default));
        Assert.Empty(f.Requests);
        f.Options.AllowDestroy = true;
        var result = Fixture.Parse(await service.Execute(change, default));
        Assert.Equal("not-visible-in-scoped-inventory", result.GetProperty("readback").GetProperty("sourceDeletion").GetString());
    }

    [Theory]
    [InlineData("occupied")]
    [InlineData("pending")]
    [InlineData("stale")]
    [InlineData("controller")]
    public async Task UnsafeAddIsRejectedBeforeMutation(string conflict)
    {
        await using var f = new ManagementFixture(); Configure(f, "qemu", "add");
        if (conflict == "occupied") f.Config["scsi1"] = "data-a:vm-100-disk-0";
        if (conflict == "pending") f.Pending = "[{\"key\":\"scsi1\",\"pending\":\"data-a:vm-100-disk-0\"}]";
        if (conflict == "stale") f.Config["digest"] = ManagementFixture.NewDigest;
        var change = Change("qemu", "add", false) with { IoThread = conflict == "controller" ? true : null };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new DiskToolCalls(f.Client).Execute(change, default));
        Assert.All(f.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Fact]
    public async Task BootDiskAndUnownedVolumeCannotBeDetached()
    {
        await using var f = new ManagementFixture(); Configure(f, "qemu", "detach");
        var service = new DiskToolCalls(f.Client);
        f.Config["boot"] = "order=scsi1";
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.Execute(Change("qemu", "detach", false), default));
        f.Config["boot"] = "order=scsi0";
        f.Owner = 101;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.Execute(Change("qemu", "detach", false), default));
        Assert.All(f.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Fact]
    public async Task PendingMountPathCollisionIsRejected()
    {
        await using var f = new ManagementFixture(); Configure(f, "lxc", "add");
        f.Pending = "[{\"key\":\"mp1\",\"pending\":\"data-a:vm-100-disk-0,mp=/srv/data\"}]";
        await Assert.ThrowsAsync<InvalidOperationException>(() => new DiskToolCalls(f.Client).Execute(Change("lxc", "add", false), default));
        Assert.All(f.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Fact]
    public async Task RunningContainerMoveHasBlockerAndNeverStopsGuest()
    {
        await using var f = new ManagementFixture(); Configure(f, "lxc", "move");
        f.GuestStatus = "running";
        var service = new DiskToolCalls(f.Client);
        var preview = Fixture.Parse(await service.Execute(Change("lxc", "move"), default));
        Assert.False(preview.GetProperty("canSubmit").GetBoolean());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.Execute(Change("lxc", "move", false), default));
        Assert.All(f.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Fact]
    public async Task PendingAllocationIsNotReportedActive()
    {
        await using var f = new ManagementFixture(); Configure(f, "qemu", "add", pending: true);
        var result = Fixture.Parse(await new DiskToolCalls(f.Client).Execute(Change("qemu", "add", false), default));
        Assert.False(result.GetProperty("readback").GetProperty("requestedConfigurationActive").GetBoolean());
        Assert.Single(result.GetProperty("readback").GetProperty("pendingChanges").EnumerateArray());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    public async Task MissingOrInsufficientCapacityBlocksWrites(long? available)
    {
        await using var f = new ManagementFixture(); Configure(f, "qemu", "add");
        f.Available = available;
        var service = new DiskToolCalls(f.Client);
        var preview = Fixture.Parse(await service.Execute(Change("qemu", "add"), default));
        Assert.False(preview.GetProperty("canSubmit").GetBoolean());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.Execute(Change("qemu", "add", false), default));
        Assert.All(f.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Theory]
    [InlineData("/dev/disk")]
    [InlineData("/srv/../etc")]
    [InlineData("/")]
    [InlineData("/data,ro=0")]
    public async Task UnsafeMountPathsAreRejected(string path)
    {
        await using var f = new ManagementFixture(); Configure(f, "lxc", "add");
        await Assert.ThrowsAsync<ArgumentException>(() => new DiskToolCalls(f.Client).Execute(Change("lxc", "add", false) with { MountPath = path }, default));
        Assert.All(f.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Fact]
    public async Task AcceptedMoveWithLostResponseIsReadBackWithoutRetry()
    {
        await using var f = new ManagementFixture(); Configure(f, "qemu", "move", loseResponse: true);
        var result = await new DiskToolCalls(f.Client).Execute(Change("qemu", "move", false), default);
        Assert.DoesNotContain("sensitive upstream", result);
        Assert.True(Fixture.Parse(result).GetProperty("readback").GetProperty("sourceRetentionVerified").GetBoolean());
        Assert.Single(f.Requests, r => r.Method != HttpMethod.Get);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new DiskToolCalls(f.Client).Execute(Change("qemu", "move", false), default));
        Assert.Single(f.Requests, r => r.Method != HttpMethod.Get);
    }
}
