using System.Net;
using System.Text.Json;
using ProxmoxMCPSharp.Tools;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace ProxmoxMCPSharp.Tests;

public sealed class AccessTests
{
    private static Task<string> Provision(AccessFixture f, bool dryRun = false, string user = "monitor@pve", string role = "PVEAuditor", string path = "/") =>
        AccessTools.Provision(f.Client, user, "monitor", role, path, dryRun: dryRun);

    [Fact]
    public async Task ProvisionsReadOnlyServiceIdentityAndReturnsSecretOnce()
    {
        await using var f = new AccessFixture();
        f.EnableWrites();
        var report = Fixture.Parse(await Provision(f));

        Assert.Equal("completed", report.GetProperty("outcome").GetString());
        Assert.Equal("monitor@pve!monitor", report.GetProperty("tokenId").GetString());
        Assert.Equal(AccessFixture.Secret, report.GetProperty("secret").GetString());
        Assert.All(report.GetProperty("steps").EnumerateArray(), s => Assert.Equal("completed", s.GetProperty("state").GetString()));
        var readback = report.GetProperty("readback");
        Assert.True(readback.GetProperty("token").GetProperty("privsep").GetBoolean());
        var effective = readback.GetProperty("tokenEffectivePermissions");
        Assert.Contains("Sys.Audit", effective.GetProperty("privileges").EnumerateArray().Select(v => v.GetString()));
        Assert.Empty(effective.GetProperty("nonAuditPrivileges").EnumerateArray());
        Assert.True(f.Effective("monitor@pve!monitor", "/").SetEquals(f.Effective("monitor@pve", "/")));

        var mutations = f.Mutations.ToArray();
        Assert.Equal(3, mutations.Length);
        Assert.DoesNotContain("password", mutations[0].Body);
        Assert.Contains("privsep=1", mutations[1].Body);
        Assert.Contains("users=monitor%40pve", mutations[2].Body);
        Assert.Contains("tokens=monitor%40pve%21monitor", mutations[2].Body);
        Assert.Contains("roles=PVEAuditor", mutations[2].Body);

        foreach (var listing in new[] { await AccessTools.ListTokens(f.Client, "monitor@pve"), await AccessTools.ListUsers(f.Client), await AccessTools.ListAcl(f.Client) })
        {
            Assert.DoesNotContain(AccessFixture.Secret, listing);
            Assert.DoesNotContain("never-listed", listing);
            Assert.DoesNotContain("private@example.invalid", listing);
        }
    }

    [Fact]
    public async Task RepeatedCreatesNeverRotateOrReplace()
    {
        await using var f = new AccessFixture();
        f.EnableWrites();
        await Provision(f);
        var count = f.Mutations.Count();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Provision(f));
        var token = await Assert.ThrowsAsync<InvalidOperationException>(() => AccessTools.CreateToken(f.Client, "monitor@pve", "monitor", dryRun: false));
        Assert.Contains("never rotated", token.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => AccessTools.CreateUser(f.Client, "monitor@pve", dryRun: false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => AccessTools.SetAcl(f.Client, "/", "PVEAuditor", "monitor@pve!monitor", dryRun: false));
        Assert.Equal(count, f.Mutations.Count());
        var preview = Fixture.Parse(await AccessTools.CreateToken(f.Client, "monitor@pve", "monitor"));
        Assert.False(preview.GetProperty("canSubmit").GetBoolean());
    }

    [Fact]
    public async Task PreviewIsTheDefaultAndOnlyReads()
    {
        await using var f = new AccessFixture();
        var report = Fixture.Parse(await Provision(f, dryRun: true));
        Assert.Equal("preview", report.GetProperty("outcome").GetString());
        Assert.True(report.GetProperty("canSubmit").GetBoolean());
        Assert.True(report.GetProperty("requested").GetProperty("role").GetProperty("readOnly").GetBoolean());
        Assert.Equal(JsonValueKind.Null, report.GetProperty("secret").ValueKind);
        foreach (var call in new Func<Task<string>>[]
        {
            () => AccessTools.CreateUser(f.Client, "monitor@pve"),
            () => AccessTools.CreateToken(f.Client, "fixture@pve", "other"),
            () => AccessTools.SetAcl(f.Client, "/", "PVEAuditor", "root@pam"),
            () => AccessTools.RemoveAcl(f.Client, "/", "PVEAuditor", "root@pam"),
            () => AccessTools.DeleteToken(f.Client, "fixture@pve", "other"),
            () => AccessTools.DeleteUser(f.Client, "monitor@pve"),
        })
            Assert.Equal("preview", Fixture.Parse(await call()).GetProperty("outcome").GetString());
        Assert.Empty(f.Mutations);
    }

    public static readonly TheoryData<string> Gates = new() { "read-only", "no-access-allowance", "no-destroy" };

    [Theory]
    [MemberData(nameof(Gates))]
    public async Task GatesBlockEveryIdentityMutation(string gate)
    {
        await using var f = new AccessFixture();
        f.EnableWrites(destroy: true);
        if (gate == "read-only") f.Options.ReadOnly = true;
        if (gate == "no-access-allowance") f.Options.AllowAccessManagement = false;
        if (gate == "no-destroy") f.Options.AllowDestroy = false;
        f.Users["old@pve"] = null;
        f.Tokens["old@pve"] = new() { ["t1"] = true };
        f.Acl.Add(("/", "token", "old@pve!t1", "PVEAuditor", true));
        var creates = new Func<Task<string>>[]
        {
            () => Provision(f),
            () => AccessTools.CreateUser(f.Client, "monitor@pve", dryRun: false),
            () => AccessTools.CreateToken(f.Client, "old@pve", "t2", dryRun: false),
            () => AccessTools.SetAcl(f.Client, "/", "PVEAuditor", "old@pve", dryRun: false),
        };
        var destroys = new Func<Task<string>>[]
        {
            () => AccessTools.RemoveAcl(f.Client, "/", "PVEAuditor", "old@pve!t1", dryRun: false),
            () => AccessTools.DeleteToken(f.Client, "old@pve", "t1", dryRun: false),
            () => AccessTools.DeleteUser(f.Client, "old@pve", dryRun: false),
        };
        foreach (var call in gate == "no-destroy" ? destroys : creates.Concat(destroys))
            await Assert.ThrowsAsync<InvalidOperationException>(call);
        Assert.Empty(f.Requests);
    }

    [Theory]
    [InlineData("monitor", "monitor", "PVEAuditor", "/")]
    [InlineData("mon itor@pve", "monitor", "PVEAuditor", "/")]
    [InlineData("monitor@p", "monitor", "PVEAuditor", "/")]
    [InlineData("monitor@pve!x", "monitor", "PVEAuditor", "/")]
    [InlineData("monitor@pve", "1abc", "PVEAuditor", "/")]
    [InlineData("monitor@pve", "a", "PVEAuditor", "/")]
    [InlineData("monitor@pve", "monitor", "Bad Role", "/")]
    [InlineData("monitor@pve", "monitor", "", "/")]
    [InlineData("monitor@pve", "monitor", "PVEAuditor", "")]
    [InlineData("monitor@pve", "monitor", "PVEAuditor", "nodes")]
    [InlineData("monitor@pve", "monitor", "PVEAuditor", "/nodes/../access")]
    [InlineData("monitor@pve", "monitor", "PVEAuditor", "/etc/pve")]
    [InlineData("monitor@pve", "monitor", "PVEAuditor", "/vms/100/")]
    public async Task InvalidIdentifiersAndPathsAreRejectedBeforeAnyRequest(string user, string token, string role, string path)
    {
        await using var f = new AccessFixture();
        f.EnableWrites();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => AccessTools.Provision(f.Client, user, token, role, path, dryRun: false));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => AccessTools.Provision(f.Client, user, token, role, path));
        Assert.Empty(f.Requests);
    }

    [Theory]
    [InlineData("Administrator", "access-administration")]
    [InlineData("NoSuchRole", "Role does not exist")]
    public async Task EscalatingOrUnknownRolesAreRefused(string role, string blocker)
    {
        await using var f = new AccessFixture();
        f.EnableWrites();
        foreach (var preview in new[] { await Provision(f, dryRun: true, role: role), await AccessTools.SetAcl(f.Client, "/", role, "fixture@pve!reader") })
        {
            var report = Fixture.Parse(preview);
            Assert.False(report.GetProperty("canSubmit").GetBoolean());
            Assert.Contains(report.GetProperty("blockers").EnumerateArray(), b => b.GetString()!.Contains(blocker));
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => Provision(f, role: role));
        Assert.Empty(f.Mutations);
    }

    [Theory]
    [InlineData("fixture@pve!reader")]
    [InlineData("fixture@pve")]
    public async Task ServerOwnIdentityCannotBeChanged(string self)
    {
        await using var f = new AccessFixture();
        f.EnableWrites(destroy: true);
        f.Acl.Add(("/", self.Contains('!') ? "token" : "user", self, "PVEAuditor", true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => AccessTools.SetAcl(f.Client, "/vms/100", "PVEVMUser", self, dryRun: false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => AccessTools.RemoveAcl(f.Client, "/", "PVEAuditor", self, dryRun: false));
        if (self.Contains('!')) await Assert.ThrowsAsync<InvalidOperationException>(() => AccessTools.DeleteToken(f.Client, "fixture@pve", "reader", dryRun: false));
        else await Assert.ThrowsAsync<InvalidOperationException>(() => AccessTools.DeleteUser(f.Client, self, dryRun: false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => AccessTools.DeleteUser(f.Client, "root@pam", dryRun: false));
        Assert.Empty(f.Mutations);
    }

    [Fact]
    public async Task ProvisioningRequiresPveRealmAndExistingRealm()
    {
        await using var f = new AccessFixture();
        f.EnableWrites();
        Assert.Contains(Fixture.Parse(await Provision(f, true, user: "monitor@pam")).GetProperty("blockers").EnumerateArray(), b => b.GetString()!.Contains("'pve' realm"));
        Assert.Contains(Fixture.Parse(await Provision(f, true, user: "monitor@corp")).GetProperty("blockers").EnumerateArray(), b => b.GetString()!.Contains("Realm does not exist"));
        Assert.Empty(f.Mutations);
    }

    [Fact]
    public async Task InsufficientUpstreamPermissionIsReportedWithoutFurtherSteps()
    {
        await using var f = new AccessFixture();
        f.EnableWrites();
        f.Override = r => r.Method == HttpMethod.Post ? AccessFixture.Error(HttpStatusCode.Forbidden) : null;
        var report = Fixture.Parse(await Provision(f));
        Assert.Equal("rejected", report.GetProperty("outcome").GetString());
        var steps = report.GetProperty("steps").EnumerateArray().ToArray();
        Assert.Equal(("rejected", "permission-denied"), (steps[0].GetProperty("state").GetString(), steps[0].GetProperty("error").GetString()));
        Assert.Equal("not-attempted", steps[1].GetProperty("state").GetString());
        Assert.Equal("not-attempted", steps[2].GetProperty("state").GetString());
        Assert.Single(f.Mutations);
        Assert.False(report.GetProperty("readback").GetProperty("userPresent").GetBoolean());
        Assert.DoesNotContain(AccessFixture.Secret, report.GetRawText());
    }

    [Fact]
    public async Task PartialFailureReportsStateAndKeepsTheOnlyCopyOfTheSecret()
    {
        await using var f = new AccessFixture();
        f.EnableWrites();
        f.Override = r => r.Method == HttpMethod.Put ? AccessFixture.Error(HttpStatusCode.Forbidden) : null;
        var report = Fixture.Parse(await Provision(f));
        Assert.Equal("partial", report.GetProperty("outcome").GetString());
        Assert.Equal(AccessFixture.Secret, report.GetProperty("secret").GetString());
        var readback = report.GetProperty("readback");
        Assert.True(readback.GetProperty("userPresent").GetBoolean());
        Assert.True(readback.GetProperty("token").GetProperty("tokenPresent").GetBoolean());
        Assert.Empty(readback.GetProperty("aclEntries").EnumerateArray());
        Assert.Empty(readback.GetProperty("tokenEffectivePermissions").GetProperty("privileges").EnumerateArray());
        Assert.Contains(report.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("No step was retried"));
        Assert.Equal(3, f.Mutations.Count());
    }

    [Fact]
    public async Task UncertainTokenResponseIsNotRetriedAndNeverLeaksTheSecret()
    {
        await using var f = new AccessFixture();
        f.EnableWrites();
        var logged = new List<string>();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new Capture(logged)).CreateLogger();
        try
        {
            f.LoseResponse = r => r.RequestUri!.AbsolutePath.Contains("/token/");
            var raw = await AccessTools.CreateToken(f.Client, "fixture@pve", "lost", dryRun: false);
            var report = Fixture.Parse(raw);
            Assert.Equal("unknown", report.GetProperty("outcome").GetString());
            Assert.Equal("mutation-indeterminate", report.GetProperty("steps")[0].GetProperty("error").GetString());
            Assert.True(report.GetProperty("readback").GetProperty("tokenPresent").GetBoolean());
            Assert.Contains(report.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("revoke it"));
            Assert.DoesNotContain(AccessFixture.Secret, raw);
            Assert.DoesNotContain("synthetic transport", raw);
            Assert.Single(f.Mutations);

            f.LoseResponse = null;
            var issued = await AccessTools.CreateToken(f.Client, "fixture@pve", "fresh", dryRun: false);
            Assert.Contains(AccessFixture.Secret, issued);
            Assert.NotEmpty(logged);
            Assert.All(logged, line => Assert.DoesNotContain(AccessFixture.Secret, line));
        }
        finally { Log.Logger = previous; }
    }

    [Fact]
    public async Task UpstreamErrorBodiesNeverReachTheReport()
    {
        await using var f = new AccessFixture();
        f.EnableWrites();
        f.Override = r => r.Method == HttpMethod.Post ? AccessFixture.Error(HttpStatusCode.InternalServerError) : null;
        var raw = await AccessTools.CreateToken(f.Client, "fixture@pve", "other", dryRun: false);
        Assert.Equal("unknown", Fixture.Parse(raw).GetProperty("outcome").GetString());
        Assert.DoesNotContain(AccessFixture.Secret, raw);
        Assert.DoesNotContain("synthetic upstream", raw);
    }

    [Fact]
    public async Task UserDeletionRequiresTokensAndAclsToBeRemovedFirst()
    {
        await using var f = new AccessFixture();
        f.EnableWrites(destroy: true);
        await Provision(f);
        var blocked = Fixture.Parse(await AccessTools.DeleteUser(f.Client, "monitor@pve"));
        Assert.Equal(2, blocked.GetProperty("blockers").GetArrayLength());
        await Assert.ThrowsAsync<InvalidOperationException>(() => AccessTools.DeleteUser(f.Client, "monitor@pve", dryRun: false));

        var revoke = Fixture.Parse(await AccessTools.DeleteToken(f.Client, "monitor@pve", "monitor", dryRun: false));
        Assert.Equal("completed", revoke.GetProperty("outcome").GetString());
        Assert.Single(revoke.GetProperty("before").GetProperty("aclEntries").EnumerateArray());
        Assert.False(revoke.GetProperty("readback").GetProperty("tokenPresent").GetBoolean());
        Assert.Equal("completed", Fixture.Parse(await AccessTools.RemoveAcl(f.Client, "/", "PVEAuditor", "monitor@pve", dryRun: false)).GetProperty("outcome").GetString());
        await Assert.ThrowsAsync<InvalidOperationException>(() => AccessTools.RemoveAcl(f.Client, "/", "PVEAuditor", "monitor@pve", dryRun: false));
        var deleted = Fixture.Parse(await AccessTools.DeleteUser(f.Client, "monitor@pve", dryRun: false));
        Assert.Equal("completed", deleted.GetProperty("outcome").GetString());
        Assert.False(deleted.GetProperty("readback").GetProperty("userPresent").GetBoolean());
        Assert.Single(f.Mutations, m => m.Method == HttpMethod.Delete && m.Path == "access/users/monitor@pve");
    }

    [Fact]
    public async Task TokenAclWarnsWhenItsUserLacksThePrivileges()
    {
        await using var f = new AccessFixture();
        f.EnableWrites();
        f.Users["svc@pve"] = null;
        f.Tokens["svc@pve"] = new() { ["tk"] = true };
        var report = Fixture.Parse(await AccessTools.SetAcl(f.Client, "/vms/100", "PVEVMUser", "svc@pve!tk", dryRun: false));
        Assert.Equal("completed", report.GetProperty("outcome").GetString());
        Assert.Contains(report.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("intersection") || w.GetString()!.Contains("only receive"));
        Assert.Contains(report.GetProperty("requested").GetProperty("role").GetProperty("nonAuditPrivileges").EnumerateArray(), p => p.GetString() == "VM.PowerMgmt");
        Assert.Single(report.GetProperty("readback").GetProperty("entries").EnumerateArray());

        f.Acl.Add(("/", "user", "svc@pve", "PVEVMUser", true));
        var covered = Fixture.Parse(await AccessTools.SetAcl(f.Client, "/storage/local", "PVEAuditor", "svc@pve!tk"));
        Assert.Empty(covered.GetProperty("warnings").EnumerateArray());
    }

    [Fact]
    public async Task AccessReadsHonourTheirToggle()
    {
        await using var f = new AccessFixture();
        f.Options.EnableAccess = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => AccessTools.ListUsers(f.Client));
        await Assert.ThrowsAsync<InvalidOperationException>(() => AccessTools.GetPermissions(f.Client, "fixture@pve!reader"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Provision(f, dryRun: true));
        Assert.Empty(f.Requests);
    }

    private sealed class Capture(List<string> lines) : ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            lock (lines) lines.Add(logEvent.RenderMessage() + logEvent.Exception);
        }
    }
}
