using System.Net;
using System.Text.Json;
using ProxmoxMCPSharp.Services;
using ProxmoxMCPSharp.Tools;
using Xunit;

namespace ProxmoxMCPSharp.Tests;

public sealed class AggregateTests
{
    [Theory]
    [InlineData("\"disable\":1,", "disabled")]
    [InlineData("\"nodes\":\"node-b\",", "node-restricted")]
    [InlineData("", "inactive")]
    public async Task StorageDistinguishesStatesAndProjectsConfiguration(string fields, string state)
    {
        await using var f = new Fixture();
        f.Respond = (r, _) => Task.FromResult(r.RequestUri!.AbsolutePath.EndsWith("/status") ? Fixture.Json("{\"active\":0}")
            : Fixture.Json("{" + fields + "\"type\":\"nfs\",\"password\":\"synthetic-secret\",\"username\":\"private-account\"}"));
        var json = await StorageTools.GetDiagnostics(f.Client, "backup-a");
        Assert.Equal(state, Fixture.Parse(json).GetProperty("availability").GetString());
        Assert.DoesNotContain("synthetic-secret", json);
        Assert.DoesNotContain("private-account", json);
        Assert.Contains("unsupported-backend", json);
        Assert.Equal(2, f.Requests.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DirectoryMatchingUsesPathAndOmitsOptions(bool matching)
    {
        await using var f = new Fixture();
        f.Respond = (r, _) => Task.FromResult(r.RequestUri!.AbsolutePath.EndsWith("/directory")
            ? Fixture.Json("[{\"path\":\"" + (matching ? "/mnt/backup" : "/mnt/other") + "\",\"options\":\"password=synthetic-secret\",\"device\":\"/dev/sdb1\"}]")
            : r.RequestUri.AbsolutePath.EndsWith("/status") ? Fixture.Json("{\"active\":0}") : Fixture.Json("{\"type\":\"dir\",\"path\":\"/mnt/backup\"}"));
        var report = await StorageTools.GetDiagnostics(f.Client, "unrelated-storage-name");
        var data = Fixture.Parse(report);
        Assert.Equal(matching ? 1 : 0, data.GetProperty("backingEvidence").GetArrayLength());
        Assert.Contains("live-mount-state-unverified", report);
        Assert.DoesNotContain("synthetic-secret", report);
        Assert.DoesNotContain("synthetic-secret", await DiskTools.ListDirectories(f.Client));
        Assert.True(f.Requests.Count <= 8);
    }

    [Fact]
    public async Task StorageGateAndDeniedEvidenceRemainDistinct()
    {
        await using var f = new Fixture();
        f.Options.EnableNodes = false;
        f.Respond = (r, _) => Task.FromResult(r.RequestUri!.AbsolutePath.EndsWith("/status")
            ? new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("synthetic-secret") }
            : Fixture.Json("{\"type\":\"dir\",\"path\":\"/mnt/backup\"}"));
        var report = await StorageTools.GetDiagnostics(f.Client, "backup-a");
        Assert.Contains("permission-denied", report);
        Assert.Contains("node-components-disabled", report);
        Assert.DoesNotContain("synthetic-secret", report);
        Assert.Equal(2, f.Requests.Count);
        f.Options.EnableStorage = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => StorageTools.GetDiagnostics(f.Client, "backup-a"));
        Assert.Equal(2, f.Requests.Count);
    }

    [Fact]
    public async Task ZfsUsesConfiguredPoolAndPreservesDegradedHierarchy()
    {
        await using var f = new Fixture();
        f.Respond = (r, _) => Task.FromResult(r.RequestUri!.AbsolutePath.Contains("/disks/zfs/")
            ? Fixture.Json("{\"state\":\"DEGRADED\",\"scan\":\"scrub in progress\",\"children\":[{\"state\":\"FAULTED\"}]}")
            : r.RequestUri.AbsolutePath.EndsWith("/status") ? Fixture.Json("{\"active\":1}") : Fixture.Json("{\"type\":\"zfspool\",\"pool\":\"pool-a/guests\"}"));
        var result = await StorageTools.GetDiagnostics(f.Client, "different-storage-name");
        Assert.EndsWith("/disks/zfs/pool-a", f.Requests[^1].AbsolutePath);
        Assert.Contains("DEGRADED", result); Assert.Contains("FAULTED", result); Assert.Contains("scrub in progress", result);
    }

    private static void CoverageFixture(Fixture f, string content, string jobs = "[]", bool fullTaskPage = false)
    {
        f.Respond = (r, _) =>
        {
            var path = r.RequestUri!.AbsolutePath;
            return Task.FromResult(path.EndsWith("/qemu") ? Fixture.Json("[{\"vmid\":100},{\"vmid\":101},{\"vmid\":102}]")
                : path.EndsWith("/lxc") ? Fixture.Json("[]")
                : path.EndsWith("/backup") ? Fixture.Json(jobs)
                : path.EndsWith("/status") ? Fixture.Json("{\"active\":1,\"enabled\":1}")
                : path.EndsWith("/content") ? Fixture.Json(content)
                : path.EndsWith("/tasks") ? Fixture.Json(fullTaskPage ? JsonSerializer.Serialize(Enumerable.Range(0, 500).Select(_ => new { type = "vzdump", id = "", status = "OK", endtime = 1700000000 }))
                    : "[{\"type\":\"vzdump\",\"id\":\"100\",\"status\":\"OK\",\"endtime\":1700000000},{\"type\":\"vzdump\",\"id\":\"\",\"status\":\"OK\",\"endtime\":1700000000}]")
                : throw new InvalidOperationException("Unexpected fixture endpoint"));
        };
    }

    [Fact]
    public async Task CoverageJoinsArchivesJobsAndOnlyCorrelatedTasks()
    {
        await using var f = new Fixture();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        CoverageFixture(f, $"[{{\"vmid\":100,\"ctime\":{now - 3600}}},{{\"vmid\":101,\"ctime\":{now - 360000}}}]",
            "[{\"id\":\"all-except-101\",\"all\":1,\"exclude\":\"101\",\"storage\":\"backup-a\",\"schedule\":\"daily\",\"enabled\":1},{\"id\":\"other-node\",\"node\":\"node-b\",\"all\":1}]");
        var report = Fixture.Parse(await BackupTools.GetCoverage(f.Client, "node-a", [100, 101, 102], ["backup-a"], maxAgeHours: 24));
        var guests = report.GetProperty("guests");
        Assert.Equal("recent-visible-backup", guests[0].GetProperty("state").GetString());
        Assert.Equal("stale-visible-backup", guests[1].GetProperty("state").GetString());
        Assert.Equal("no-visible-backup", guests[2].GetProperty("state").GetString());
        Assert.Single(guests[0].GetProperty("applicableJobs").EnumerateArray());
        Assert.Empty(guests[1].GetProperty("applicableJobs").EnumerateArray());
        Assert.Equal(JsonValueKind.Object, guests[0].GetProperty("latestObservedSuccess").ValueKind);
        Assert.Equal(JsonValueKind.Null, guests[1].GetProperty("latestObservedSuccess").ValueKind);
        Assert.Single(report.GetProperty("nodeWideTaskEvidence").EnumerateArray());
        Assert.Equal(6, f.Requests.Count);
    }

    [Fact]
    public async Task CoverageWithoutThresholdDoesNotInventOverdueRuleAndPoolSelectionStaysUnknown()
    {
        await using var f = new Fixture();
        CoverageFixture(f, "[{\"vmid\":100,\"ctime\":1000}]", "[{\"id\":\"pool-job\",\"pool\":\"pool-a\"}]");
        var report = Fixture.Parse(await BackupTools.GetCoverage(f.Client, "node-a", [100], ["backup-a"]));
        Assert.Equal("visible-backup", report.GetProperty("guests")[0].GetProperty("state").GetString());
        Assert.True(report.GetProperty("partial").GetBoolean());
        Assert.Single(report.GetProperty("guests")[0].GetProperty("unresolvedJobs").EnumerateArray());
    }

    [Fact]
    public async Task CoveragePageAndByteBudgetsProduceIncompleteEvidence()
    {
        await using var f = new Fixture();
        CoverageFixture(f, "[]", fullTaskPage: true);
        var response = f.Respond;
        f.Respond = (r, ct) => r.RequestUri!.AbsolutePath.EndsWith("/content") ? Task.FromResult(Fixture.Json("\"" + new string('x', 1024 * 1024) + "\"")) : response(r, ct);
        var report = Fixture.Parse(await BackupTools.GetCoverage(f.Client, "node-a", [100], ["backup-a"], taskPageBudget: 2));
        Assert.Equal(2, report.GetProperty("scannedPages").GetInt32());
        Assert.True(report.GetProperty("truncated").GetBoolean());
        Assert.Contains("response-byte-budget-exceeded", report.GetRawText());
        Assert.Equal("unknown", report.GetProperty("guests")[0].GetProperty("state").GetString());
        Assert.Equal(7, f.Requests.Count);
    }

    [Fact]
    public async Task CoverageRejectsDeniedGuestsScopeAndCategoriesBeforeSending()
    {
        await using var f = new Fixture();
        f.Options.BlockedVmIds.Add(100);
        await Assert.ThrowsAsync<InvalidOperationException>(() => BackupTools.GetCoverage(f.Client, "node-a", [100], ["backup-a"]));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => BackupTools.GetCoverage(f.Client, "node-a", [], ["backup-a"]));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => BackupTools.GetCoverage(f.Client, "", [101], ["backup-a"]));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => BackupTools.GetCoverage(f.Client, "node-a", [101], ["backup-a"], taskPageBudget: 21));
        f.Options.EnableQemu = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => BackupTools.GetCoverage(f.Client, "node-a", [101], ["backup-a"]));
        Assert.Empty(f.Requests);
    }

    [Fact]
    public async Task MaximumScopeStaysWithinReadBudgetAndDoesNotEmitUnrequestedGuests()
    {
        await using var f = new Fixture();
        CoverageFixture(f, "[]", fullTaskPage: true);
        var original = f.Respond;
        f.Respond = (r, ct) => r.RequestUri!.AbsolutePath.EndsWith("/qemu")
            ? Task.FromResult(Fixture.Json(JsonSerializer.Serialize(Enumerable.Range(100, 1000).Select(id => new { vmid = id })))) : original(r, ct);
        var report = Fixture.Parse(await BackupTools.GetCoverage(f.Client, "node-a", Enumerable.Range(100, 100).ToArray(), Enumerable.Range(1, 20).Select(i => "backup-" + i).ToArray()));
        Assert.Equal(100, report.GetProperty("guests").GetArrayLength());
        Assert.Equal(20, report.GetProperty("queriedTargets").GetArrayLength());
        Assert.Equal(20, report.GetProperty("scannedPages").GetInt32());
        Assert.Equal(63, f.Requests.Count);
        Assert.True(report.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task InactiveTargetAndDeniedHistoryDoNotBecomeNoBackupConclusion()
    {
        await using var f = new Fixture();
        CoverageFixture(f, "[]");
        var original = f.Respond;
        f.Respond = (r, ct) => r.RequestUri!.AbsolutePath.EndsWith("/status") ? Task.FromResult(Fixture.Json("{\"active\":0}"))
            : r.RequestUri.AbsolutePath.EndsWith("/tasks") ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("synthetic-secret") })
            : original(r, ct);
        var report = await BackupTools.GetCoverage(f.Client, "node-a", [100], ["backup-a"]);
        Assert.Equal("unknown", Fixture.Parse(report).GetProperty("guests")[0].GetProperty("state").GetString());
        Assert.Contains("inactive", report);
        Assert.Contains("permission-denied", report);
        Assert.DoesNotContain("synthetic-secret", report);
        Assert.DoesNotContain(f.Requests, r => r.AbsolutePath.EndsWith("/content"));
    }
}
