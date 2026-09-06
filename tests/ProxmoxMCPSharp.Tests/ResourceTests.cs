using System.Net;
using System.Text.Json;
using ProxmoxMCPSharp.Tools;
using Xunit;

namespace ProxmoxMCPSharp.Tests;

public sealed class ResourceTests
{
    private static void Configure(ManagementFixture f, bool pending = false)
    {
        f.Options.ReadOnly = false;
        f.Options.AllowResourceChanges = true;
        f.Config["memory"] = 1024;
        f.Config["cores"] = 2;
        f.Config["sockets"] = 1;
        f.Config["balloon"] = 512;
        f.Override = async (request, ct) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Put)
            {
                Assert.EndsWith("/config", path);
                var form = (await request.Content!.ReadAsStringAsync(ct)).Split('&').Select(v => v.Split('=', 2))
                    .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));
                Assert.Equal(ManagementFixture.Digest, form["digest"]);
                if (pending) f.Pending = JsonSerializer.Serialize(form.Where(p => p.Key != "digest").Select(p => new { key = p.Key, value = f.Config.GetValueOrDefault(p.Key), pending = p.Value }));
                else foreach (var field in form.Where(p => p.Key != "digest")) f.Config[field.Key] = field.Value;
                f.Config["digest"] = ManagementFixture.NewDigest;
                return Fixture.Json("null");
            }
            if (path == "/api2/json/nodes/node-a/status") return Fixture.Json("{\"cpuinfo\":{\"cpus\":16},\"memory\":{\"free\":8589934592}}");
            return null;
        };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QemuResourcesReportCurrentAndPendingWithoutRestart(bool pending)
    {
        await using var f = new ManagementFixture(); Configure(f, pending);
        var report = Fixture.Parse(await GuestResourceTools.UpdateQemu(f.Client, 100, ManagementFixture.Digest,
            cores: 4, memoryMiB: 4096, balloonMiB: 2048, dryRun: false, waitSeconds: 0));
        Assert.Equal(!pending, report.GetProperty("currentMatchesRequested").GetBoolean());
        Assert.Equal(pending ? "pending-changes-may-require-restart" : "no-pending-requested-values-observed", report.GetProperty("restart").GetString());
        Assert.Single(f.Requests, r => r.Method != HttpMethod.Get);
        Assert.DoesNotContain(f.Requests, r => r.Uri.AbsolutePath.EndsWith("/reboot") || r.Uri.AbsolutePath.EndsWith("/stop"));
    }

    [Fact]
    public async Task LxcSwapAndCpuLimitUseTypedInvariantValues()
    {
        await using var f = new ManagementFixture(); Configure(f);
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
            var report = Fixture.Parse(await GuestResourceTools.UpdateLxc(f.Client, 100, ManagementFixture.Digest,
                memoryMiB: 2048, swapMiB: 1024, cpuLimit: 1.5, dryRun: false, waitSeconds: 0));
            Assert.True(report.GetProperty("currentMatchesRequested").GetBoolean());
            var write = Assert.Single(f.Requests, r => r.Method == HttpMethod.Put);
            Assert.Contains("cpulimit=1.5", write.Body);
            Assert.Contains("swap=1024", write.Body);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
    }

    [Fact]
    public async Task PreviewReportsCapacityBlockerWhileSubmissionRejectsIt()
    {
        await using var f = new ManagementFixture(); Configure(f);
        var preview = Fixture.Parse(await GuestResourceTools.UpdateQemu(f.Client, 100, ManagementFixture.Digest, memoryMiB: 32768));
        Assert.False(preview.GetProperty("canSubmit").GetBoolean());
        await Assert.ThrowsAsync<InvalidOperationException>(() => GuestResourceTools.UpdateQemu(f.Client, 100, ManagementFixture.Digest, memoryMiB: 32768, dryRun: false));
        Assert.All(f.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Theory]
    [InlineData(0, null, null, null)]
    [InlineData(2, 8, null, null)]
    [InlineData(2, null, 512, 1024)]
    [InlineData(2, null, -1, null)]
    public async Task InvalidResourceCombinationsNeverSubmit(int cores, int? vcpus, int? memory, int? balloon)
    {
        await using var f = new ManagementFixture(); Configure(f);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => GuestResourceTools.UpdateQemu(f.Client, 100, ManagementFixture.Digest,
            cores: cores, vcpus: vcpus, memoryMiB: memory, balloonMiB: balloon, dryRun: false));
        Assert.All(f.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Fact]
    public async Task GatesAndRelatedPendingResourcesPreventChanges()
    {
        await using var f = new ManagementFixture(); Configure(f);
        f.Options.AllowResourceChanges = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => GuestResourceTools.UpdateQemu(f.Client, 100, ManagementFixture.Digest, cores: 4, dryRun: false));
        Assert.Empty(f.Requests);
        f.Options.AllowResourceChanges = true;
        f.Pending = "[{\"key\":\"sockets\",\"pending\":8}]";
        await Assert.ThrowsAsync<InvalidOperationException>(() => GuestResourceTools.UpdateQemu(f.Client, 100, ManagementFixture.Digest, cores: 4, dryRun: false));
        Assert.All(f.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Fact]
    public async Task QemuMemoryPropertySyntaxIsUnderstood()
    {
        await using var f = new ManagementFixture(); Configure(f);
        f.Config["memory"] = "current=1024";
        var report = Fixture.Parse(await GuestResourceTools.UpdateQemu(f.Client, 100, ManagementFixture.Digest, memoryMiB: 2048));
        Assert.True(report.GetProperty("canSubmit").GetBoolean());
    }
}
