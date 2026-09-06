using System.Text.Json;
using ProxmoxMCPSharp.Tools;
using Xunit;

namespace ProxmoxMCPSharp.Tests;

public sealed class StructuredConfigurationTests
{
    private static void Configure(ManagementFixture f, bool normalizeReadback = false)
    {
        f.Options.ReadOnly = false;
        f.Options.AllowResourceChanges = true;
        f.Options.AllowDiskManagement = true;
        f.Config["memory"] = 1024;
        f.Config["cores"] = 2;
        f.Config["boot"] = "order=scsi0";
        f.Override = async (request, ct) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method != HttpMethod.Get)
            {
                Assert.Equal(HttpMethod.Put, request.Method);
                Assert.EndsWith("/config", path);
                var form = Form(await request.Content!.ReadAsStringAsync(ct));
                Assert.Equal(ManagementFixture.Digest, form["digest"]);
                foreach (var field in form) f.Config[field.Key] = field.Value;
                if (normalizeReadback)
                {
                    f.Config["net0"] = "tag=20,firewall=1,bridge=vmbr0,virtio=02:00:00:00:AA:01";
                    f.Config["startup"] = "up=30,order=1";
                }
                f.Config["digest"] = ManagementFixture.NewDigest;
                return Fixture.Json("null");
            }
            if (path == "/api2/json/nodes/node-a/status") return Fixture.Json("{\"cpuinfo\":{\"cpus\":16},\"memory\":{\"free\":8589934592}}");
            if (path.EndsWith("/storage/data-a")) return Fixture.Json("{\"type\":\"lvmthin\",\"content\":\"images,rootdir\"}");
            return null;
        };
    }

    private static Dictionary<string, string> Form(string body) => body.Split('&').Select(v => v.Split('=', 2))
        .ToDictionary(p => Uri.UnescapeDataString(p[0].Replace('+', ' ')), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MetadataResourcesAndNetworkUseOneValidatedWrite(bool lxc)
    {
        await using var f = new ManagementFixture(); Configure(f);
        var fields = Fixture.Parse(lxc
            ? "{\"hostname\":\"guest-a\",\"memory\":2048,\"cores\":4,\"net0\":\"name=eth0,bridge=vmbr0,ip=dhcp\"}"
            : "{\"name\":\"guest-a\",\"memory\":2048,\"cores\":4,\"net0\":\"virtio=02:00:00:00:00:01,bridge=vmbr0,tag=20,firewall=1\"}");
        var response = lxc ? await LxcTools.UpdateConfig(f.Client, 100, fields, ManagementFixture.Digest, dryRun: false, waitSeconds: 0)
            : await QemuTools.UpdateConfig(f.Client, 100, fields: fields, expectedDigest: ManagementFixture.Digest, dryRun: false, waitSeconds: 0);
        Assert.True(Fixture.Parse(response).GetProperty("currentMatchesRequested").GetBoolean());
        var form = Form(Assert.Single(f.Requests, r => r.Method != HttpMethod.Get).Body!);
        Assert.Equal("2048", form["memory"]);
        Assert.Contains("bridge=vmbr0", form["net0"]);
        Assert.DoesNotContain("bridge", form.Keys);
        Assert.Equal(5, form.Count);
    }

    [Fact]
    public async Task LegacyCommaPropertiesAndEnvelopeArePreserved()
    {
        await using var f = new ManagementFixture(); Configure(f);
        var response = Fixture.Parse(await QemuTools.UpdateConfig(f.Client, 100,
            "name=guest-a,description=first, second,startup=order=1,up=30,down=20,net0=virtio,bridge=vmbr0,tag=20,memory=2048", "node-a"));
        Assert.Equal(JsonValueKind.Null, response.GetProperty("data").ValueKind);
        Assert.True(response.GetProperty("management").GetProperty("currentMatchesRequested").GetBoolean());
        var form = Form(Assert.Single(f.Requests, r => r.Method != HttpMethod.Get).Body!);
        Assert.Equal("first, second", form["description"]);
        Assert.Equal("order=1,up=30,down=20", form["startup"]);
        Assert.Contains("bridge=vmbr0,tag=20", form["net0"]);
        Assert.DoesNotContain("up", form.Keys);
        Assert.Equal(ManagementFixture.Digest, form["digest"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiskPropertiesRouteThroughFocusedAllocation(bool legacy)
    {
        await using var f = new ManagementFixture(); Configure(f);
        var fields = Fixture.Parse("{\"scsi1\":\"data-a:8,cache=none,discard=on\"}");
        if (legacy) await QemuTools.UpdateConfig(f.Client, 100, "scsi1=data-a:8,cache=none,discard=on", waitSeconds: 0);
        else await QemuTools.UpdateConfig(f.Client, 100, fields: fields, expectedDigest: ManagementFixture.Digest, dryRun: false, waitSeconds: 0);
        var form = Form(Assert.Single(f.Requests, r => r.Method != HttpMethod.Get).Body!);
        Assert.Equal("data-a:8,format=raw,cache=none,discard=on", form["scsi1"]);
        Assert.Equal(2, form.Count);
    }

    [Theory]
    [InlineData("{\"delete\":\"unused0\"}")]
    [InlineData("{\"skiplock\":1}")]
    [InlineData("{\"scsi1\":\"data-a:8,size=100G\"}")]
    [InlineData("{\"scsi1\":\"data-a:8\",\"name\":\"guest-a\"}")]
    [InlineData("{\"net0\":\"virtio,bridge=vmbr0,script=/tmp/a\"}")]
    [InlineData("{\"name\":\"guest-a\",\"name\":\"guest-b\"}")]
    [InlineData("{\"memory\":null}")]
    [InlineData("{\"memory\":8192,\"balloon\":9000}")]
    public async Task UnsafeStructuredPayloadNeverWrites(string payload)
    {
        await using var f = new ManagementFixture(); Configure(f);
        var error = await Record.ExceptionAsync(() => QemuTools.UpdateConfig(f.Client, 100, fields: Fixture.Parse(payload), expectedDigest: ManagementFixture.Digest, dryRun: false));
        Assert.True(error is ArgumentException or InvalidOperationException);
        Assert.All(f.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Theory]
    [InlineData("resource")]
    [InlineData("disk")]
    [InlineData("network")]
    public async Task SharedGatesDenyBeforeUpstreamReads(string category)
    {
        await using var f = new ManagementFixture(); Configure(f);
        f.Options.AllowResourceChanges = false;
        f.Options.AllowDiskManagement = false;
        f.Options.EnableNetwork = false;
        var payload = category switch { "resource" => "{\"memory\":2048}", "disk" => "{\"scsi1\":\"data-a:8\"}", _ => "{\"net0\":\"virtio,bridge=vmbr0\"}" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => QemuTools.UpdateConfig(f.Client, 100, fields: Fixture.Parse(payload), expectedDigest: ManagementFixture.Digest, dryRun: false));
        Assert.Empty(f.Requests);
    }

    [Fact]
    public async Task StructuredDefaultIsPreviewAndRequiresDigest()
    {
        await using var f = new ManagementFixture(); Configure(f);
        f.Options.ReadOnly = true;
        var fields = Fixture.Parse("{\"name\":\"guest-a\"}");
        await Assert.ThrowsAsync<ArgumentException>(() => QemuTools.UpdateConfig(f.Client, 100, fields: fields));
        Assert.Empty(f.Requests);
        var result = Fixture.Parse(await QemuTools.UpdateConfig(f.Client, 100, fields: fields, expectedDigest: ManagementFixture.Digest));
        Assert.True(result.GetProperty("dryRun").GetBoolean());
        Assert.All(f.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Fact]
    public async Task PendingNetworkChangeBlocksMixedResourceWrite()
    {
        await using var f = new ManagementFixture(); Configure(f);
        f.Pending = "[{\"key\":\"net0\",\"pending\":\"virtio,bridge=vmbr1\"}]";
        await Assert.ThrowsAsync<InvalidOperationException>(() => QemuTools.UpdateConfig(f.Client, 100,
            fields: Fixture.Parse("{\"memory\":2048,\"net0\":\"virtio,bridge=vmbr0\"}"), expectedDigest: ManagementFixture.Digest, dryRun: false));
        Assert.All(f.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Fact]
    public async Task ReadbackAcceptsPveAliasesOrderingAndMacCase()
    {
        await using var f = new ManagementFixture(); Configure(f, normalizeReadback: true);
        var result = Fixture.Parse(await QemuTools.UpdateConfig(f.Client, 100,
            fields: Fixture.Parse("{\"net0\":\"model=virtio,macaddr=02:00:00:00:aa:01,bridge=vmbr0,firewall=true,tag=20\",\"startup\":\"order=1,up=30\"}"),
            expectedDigest: ManagementFixture.Digest, dryRun: false));
        Assert.True(result.GetProperty("currentMatchesRequested").GetBoolean());
    }
}
