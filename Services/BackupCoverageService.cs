using System.Globalization;
using System.Text.Json;

namespace ProxmoxMCPSharp.Services;

public sealed class BackupCoverageService(ProxmoxClient pve)
{
    public async Task<string> GetAsync(string node, int[] vmids, string[] storages, int lookbackDays = 30,
        double? maxAgeHours = null, int taskPageBudget = 20, CancellationToken ct = default)
    {
        var options = pve.Options;
        if (!(options.EnableBackups && options.EnableStorage && options.EnableTasks && options.EnableNodes && options.EnableQemu && options.EnableLxc))
            throw new InvalidOperationException("Backup coverage requires EnableBackups, EnableStorage, EnableTasks, EnableNodes, EnableQemu and EnableLxc.");
        ArgumentNullException.ThrowIfNull(vmids);
        ArgumentNullException.ThrowIfNull(storages);
        DiagnosticReads.Bound(vmids.Length, 1, 100, nameof(vmids));
        DiagnosticReads.Bound(storages.Length, 1, 20, nameof(storages));
        DiagnosticReads.Bound(lookbackDays, 30, 365, nameof(lookbackDays));
        DiagnosticReads.Bound(taskPageBudget, 20, 20, nameof(taskPageBudget));
        if (maxAgeHours is { } age && (!double.IsFinite(age) || age <= 0)) throw new ArgumentOutOfRangeException(nameof(maxAgeHours));
        if (string.IsNullOrWhiteSpace(node)) throw new ArgumentException("Backup coverage requires an explicit node.");
        node = pve.ResolveNode(node);
        var n = DiagnosticReads.Segment(node);
        foreach (var id in vmids) { DiagnosticReads.Bound(id, 100, int.MaxValue, nameof(vmids)); pve.EnsureVmAllowed(id); }
        foreach (var storage in storages) DiagnosticReads.Segment(storage);
        vmids = vmids.Distinct().ToArray(); storages = storages.Distinct(StringComparer.Ordinal).ToArray();
        var now = DateTimeOffset.UtcNow;
        var since = now.AddDays(-lookbackDays).ToUnixTimeSeconds();
        var errors = new List<object>();
        var queriedTargets = new List<object>();
        var reads = 0;
        async Task<JsonElement?> Read(string component, string path, Dictionary<string, object?>? query = null, JsonValueKind kind = JsonValueKind.Array)
        {
            try
            {
                ++reads;
                // Sequential reads bound concurrency to one; every response has a 1 MiB decoded-byte cap.
                var data = DiagnosticReads.Data(await pve.ReadAsync(path, query, ct, 1024 * 1024));
                if (data.ValueKind != kind) throw new JsonException();
                return data;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            { errors.Add(new { component, reason = DiagnosticReads.Error(ex) }); return null; }
        }

        var qemu = await Read("qemuInventory", $"nodes/{n}/qemu");
        var lxc = await Read("lxcInventory", $"nodes/{n}/lxc");
        var jobs = await Read("backupJobs", "cluster/backup");
        var inventory = new HashSet<int>();
        foreach (var list in new[] { qemu, lxc })
            if (list is { } entries)
                foreach (var entry in entries.EnumerateArray())
                    if (DiagnosticReads.Number(entry, "vmid") is { } id && id <= int.MaxValue) inventory.Add((int)id);

        var archives = new Dictionary<int, List<(string Storage, long? Time)>>();
        foreach (var id in vmids) archives[id] = [];
        var targetStates = new Dictionary<string, string>();
        var archivesComplete = true;
        foreach (var storage in storages)
        {
            var s = DiagnosticReads.Segment(storage);
            var status = await Read("status:" + storage, $"nodes/{n}/storage/{s}/status", kind: JsonValueKind.Object);
            var availability = status is { } st ? DiagnosticReads.Flag(st, "active") switch { true => "active", false => "inactive", _ => "unknown" } : "unknown";
            if (status is { } enabled && DiagnosticReads.Flag(enabled, "enabled") == false) availability = "disabled";
            targetStates[storage] = availability;
            JsonElement? content = null;
            if (availability == "active") content = await Read("archives:" + storage, $"nodes/{n}/storage/{s}/content", new() { ["content"] = "backup" });
            if (content is null) archivesComplete = false;
            var unidentified = 0;
            if (content is { } entries)
                foreach (var entry in entries.EnumerateArray())
                {
                    if (DiagnosticReads.Number(entry, "vmid") is not { } id) { unidentified++; continue; }
                    if (id <= int.MaxValue && archives.TryGetValue((int)id, out var guestArchives))
                        guestArchives.Add((storage, DiagnosticReads.Number(entry, "ctime")));
                }
            if (unidentified > 0) archivesComplete = false;
            queriedTargets.Add(new { node, storage, availability, contentRead = content is not null, unidentifiedArchiveCount = unidentified,
                byteLimit = 1024 * 1024 });
        }

        var tasks = new List<JsonElement>();
        var pages = 0;
        var historyExhausted = false;
        var taskReadFailed = false;
        for (var page = 0; page < taskPageBudget; page++)
        {
            pages++;
            var entries = await Read("backupTaskHistory", $"nodes/{n}/tasks", new() {
                ["start"] = page * 500, ["limit"] = 500, ["since"] = since, ["until"] = now.ToUnixTimeSeconds(),
                ["typefilter"] = "vzdump", ["source"] = "all" });
            if (entries is null) { taskReadFailed = true; break; }
            tasks.AddRange(entries.Value.EnumerateArray().Where(t => DiagnosticReads.Text(t, "type") == "vzdump"));
            if (entries.Value.GetArrayLength() < 500) { historyExhausted = true; break; }
        }

        object TaskSummary(JsonElement task) => new { startTime = DiagnosticReads.Number(task, "starttime"), endTime = DiagnosticReads.Number(task, "endtime"),
            outcome = DiagnosticReads.Number(task, "endtime") is null ? "running-or-unknown" : DiagnosticReads.Text(task, "status") == "OK" ? "success" : DiagnosticReads.Text(task, "status") is null ? "unknown" : "failure-or-warning" };
        var guests = new List<object>();
        var incompleteGuestEvidence = false;
        foreach (var id in vmids)
        {
            ct.ThrowIfCancellationRequested();
            var applicableJobs = new List<object>();
            var unknownJobs = new List<object>();
            var reasons = new List<string>();
            if (!inventory.Contains(id)) reasons.Add("guest-not-visible-on-requested-node");
            if (jobs is { } jobEntries)
                foreach (var job in jobEntries.EnumerateArray())
                {
                    var applicability = Applies(job, node, id);
                    var summary = new { id = DiagnosticReads.Text(job, "id"), schedule = DiagnosticReads.Text(job, "schedule"),
                        target = DiagnosticReads.Text(job, "storage"), enabled = DiagnosticReads.Flag(job, "enabled"), applicability,
                        targetAvailability = DiagnosticReads.Text(job, "storage") is { } target && targetStates.TryGetValue(target, out var state) ? state : "not-queried" };
                    if (applicability == "applicable") applicableJobs.Add(summary);
                    else if (applicability == "unknown") unknownJobs.Add(summary);
                }
            if (unknownJobs.Count > 0) reasons.Add("unresolved-job-selection");
            if (jobs is null) reasons.Add("jobs-unavailable");
            if (!archivesComplete) reasons.Add("archive-evidence-incomplete");
            if (!historyExhausted || taskReadFailed) reasons.Add("task-history-incomplete");
            reasons.Add("history-and-inventory-visibility-depend-on-API-privileges");
            var visible = archives[id];
            var latest = visible.Where(a => a.Time is >= 0 && a.Time <= now.ToUnixTimeSeconds()).Select(a => a.Time).Max();
            double? ageHours = latest is { } timestamp ? (now.ToUnixTimeSeconds() - timestamp) / 3600.0 : null;
            var invalidArchiveTime = visible.Any(a => a.Time is null or < 0 || a.Time > now.ToUnixTimeSeconds());
            if (invalidArchiveTime) reasons.Add("archive-timestamp-missing-or-invalid");
            incompleteGuestEvidence |= !inventory.Contains(id) || jobs is null || unknownJobs.Count > 0 || invalidArchiveTime;
            var coverage = !inventory.Contains(id) ? "unknown" : visible.Count == 0 ? archivesComplete ? "no-visible-backup" : "unknown"
                : ageHours is null ? "unknown" : maxAgeHours is null ? "visible-backup"
                : ageHours <= maxAgeHours ? "recent-visible-backup" : archivesComplete && !invalidArchiveTime ? "stale-visible-backup" : "unknown";
            var guestTasks = tasks.Where(t => DiagnosticReads.Text(t, "id") == id.ToString(CultureInfo.InvariantCulture)).ToArray();
            object? LatestTask(bool success)
            {
                var matches = guestTasks.Where(t => DiagnosticReads.Number(t, "endtime") is not null && DiagnosticReads.Text(t, "status") is { } status && (status == "OK") == success)
                    .OrderByDescending(t => DiagnosticReads.Number(t, "endtime")).ToArray();
                return matches.Length == 0 ? null : TaskSummary(matches[0]);
            }
            guests.Add(new { vmid = id, state = coverage, applicableJobs, unresolvedJobs = unknownJobs,
                visibleArchiveCount = visible.Count, latestArchiveEpoch = latest, ageHours, latestObservedSuccess = LatestTask(true),
                latestObservedFailureOrWarning = LatestTask(false), targetAvailability = targetStates, evaluationReasons = reasons,
                evidenceCompleteness = new { inventory = inventory.Contains(id), jobs = jobs is not null && unknownJobs.Count == 0,
                    archives = archivesComplete && !invalidArchiveTime, taskWindow = historyExhausted && !taskReadFailed, visibility = "permission-limited" } });
        }
        return JsonSerializer.Serialize(new { schemaVersion = 1, observedAtUtc = now, node, scannedTimeWindow = new { since, until = now.ToUnixTimeSeconds() },
            maxAgeHours, guests, scannedPages = pages, queriedTargets, upstreamReads = reads, errors,
            truncated = !historyExhausted || !archivesComplete, partial = errors.Count > 0 || !historyExhausted || !archivesComplete || incompleteGuestEvidence,
            nodeWideTaskEvidence = tasks.Where(t => string.IsNullOrEmpty(DiagnosticReads.Text(t, "id"))).Select(TaskSummary),
            limitations = new[] { "Archive visibility is scoped to requested targets; it does not prove restore success.",
                "Node-wide tasks are not attributed to individual guests or jobs without correlation evidence.",
                "Pool-based job selection remains unknown; schedules are not interpreted as overdue rules.",
                "Offset history is a live view and may change between pages; permissions and retention can hide history." } });
    }

    private static string Applies(JsonElement job, string node, int vmid)
    {
        static string[] Tokens(string? value) => value?.Split([',', ';', ' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        var id = vmid.ToString(CultureInfo.InvariantCulture);
        var nodes = Tokens(DiagnosticReads.Text(job, "node"));
        if (nodes.Length > 0 && !nodes.Contains(node, StringComparer.OrdinalIgnoreCase)) return "not-applicable";
        if (Tokens(DiagnosticReads.Text(job, "exclude")).Contains(id)) return "not-applicable";
        if (!string.IsNullOrEmpty(DiagnosticReads.Text(job, "pool"))) return "unknown";
        if (DiagnosticReads.Flag(job, "all") == true) return "applicable";
        var ids = Tokens(DiagnosticReads.Text(job, "vmid"));
        return ids.Length == 0 ? "unknown" : ids.Contains(id) ? "applicable" : "not-applicable";
    }
}
