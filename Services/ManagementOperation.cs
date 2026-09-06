using System.Diagnostics;
using System.Text.Json;

namespace ProxmoxMCPSharp.Services;

internal sealed record OperationOutcome(string State, string? TaskId, string? TaskOutcome, string[] Warnings);

internal static class ManagementOperation
{
    public static async Task<OperationOutcome> Submit(ProxmoxClient pve, string node, string operation, HttpMethod method,
        string path, Dictionary<string, object?> parameters, int waitSeconds, CancellationToken ct)
    {
        DiagnosticReads.Bound(waitSeconds, 10, 60, nameof(waitSeconds), 0);
        ct.ThrowIfCancellationRequested();
        string? taskId = null;
        try
        {
            var data = DiagnosticReads.Data(await pve.MutateAsync(method, path, parameters, ct));
            if (data.ValueKind == JsonValueKind.Null)
                return new("synchronous-response", null, null, []);
            if (data.ValueKind != JsonValueKind.String || !(taskId = data.GetString())!.StartsWith("UPID:", StringComparison.Ordinal))
                return new("unknown", null, null, ["Unexpected mutation response; reconcile state before any further write."]);
            // A task identifier from another node must not bypass node restrictions.
            var parts = taskId.Split(':');
            if (parts.Length < 3 || !string.Equals(parts[1], node, StringComparison.OrdinalIgnoreCase))
                return new("unknown", taskId, null, ["Task node differs from the authorized node; polling was not attempted."]);
            if (!pve.Options.EnableTasks) return new("submitted", taskId, null, ["Task reads disabled; completion is unverified."]);
            var watch = Stopwatch.StartNew();
            for (var poll = 0; poll <= waitSeconds; poll++)
            {
                var status = DiagnosticReads.Data(await pve.ReadAsync($"nodes/{DiagnosticReads.Segment(node)}/tasks/{DiagnosticReads.Segment(taskId)}/status", ct: ct));
                if (DiagnosticReads.Text(status, "status") == "stopped")
                {
                    var exit = DiagnosticReads.Text(status, "exitstatus");
                    var outcome = exit == "OK" ? "success" : exit is null ? "unknown" : "failure-or-warning";
                    var warnings = new List<string>();
                    // Logs can contain filesystem warnings even when task exitstatus is OK. Never copy raw lines into reports.
                    var log = await pve.ReadAsync($"nodes/{DiagnosticReads.Segment(node)}/tasks/{DiagnosticReads.Segment(taskId)}/log",
                        new Dictionary<string, object?> { ["start"] = 0, ["limit"] = 1000 }, ct);
                    if (DiagnosticReads.Data(log).EnumerateArray().Any(line =>
                        (DiagnosticReads.Text(line, "t") ?? "").Contains("warn", StringComparison.OrdinalIgnoreCase) ||
                        (DiagnosticReads.Text(line, "t") ?? "").Contains("fail", StringComparison.OrdinalIgnoreCase)))
                        warnings.Add("Task log contains warning/failure evidence; inspect get_task_log before assuming filesystem success.");
                    if (DiagnosticReads.Number(log, "total") is > 1000) warnings.Add("Task log inspection was truncated at 1000 lines.");
                    return new("task-stopped", taskId, outcome, warnings.ToArray());
                }
                if (watch.Elapsed.TotalSeconds >= waitSeconds || poll == waitSeconds) break;
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
            return new("submitted", taskId, null, ["Polling budget exhausted; the task may still be running."]);
        }
        catch (Exception ex) when (ex is HttpRequestException or ProxmoxApiException or OperationCanceledException or JsonException or ResponseBudgetException or InvalidOperationException or KeyNotFoundException)
        {
            return new("unknown", taskId, null, [DiagnosticReads.Error(ex), "Submission or completion is uncertain. No automatic retry was made; reconcile task and disk state."]);
        }
        finally
        {
            Serilog.Log.Information("Management request {Operation} on {Node}; task reference available: {HasTask}", operation, node, taskId is not null);
        }
    }
}
