using System.ComponentModel;
using ProxmoxMCPSharp.Services;
using ModelContextProtocol.Server;

namespace ProxmoxMCPSharp.Tools;

[McpServerToolType]
public static class TaskTools
{
    [McpServerTool(Name = "list_node_tasks", ReadOnly = true, Idempotent = true, OpenWorld = true),
     Description("Page node task history. Offsets and total describe a live, permission-limited view. With running=true, filters one active-source page; advance by limit even if filtered data is empty. Total remains the upstream unfiltered total.")]
    public static async Task<string> ListTasks(
        ProxmoxClient pve,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node = null,
        [Description("Only running tasks in the selected active page if true; false/null adds no running filter.")] bool? running = null,
        [Description("Number of upstream tasks per page (default 50, maximum 500).")] int? limit = null,
        [Description("Source: archive (default), active, all. running=true rejects archive and selects active.")] string? source = null,
        CancellationToken ct = default,
        int? start = null,
        [Description("Inclusive UTC Unix epoch seconds.")] long? since = null,
        [Description("Inclusive UTC Unix epoch seconds.")] long? until = null,
        string? typeFilter = null, int? vmid = null,
        [Description("Comma-separated ok, warning, error, unknown.")] string? statusFilter = null)
    {
        if (!pve.Options.EnableTasks) throw new InvalidOperationException("Task tools are disabled.");
        var n = DiagnosticReads.Segment(pve.ResolveNode(node));
        if (source is not null and not ("archive" or "active" or "all")) throw new ArgumentException("Invalid task source.");
        if (running == true && source == "archive") throw new ArgumentException("running=true conflicts with source=archive.");
        DiagnosticReads.Window(since, until);
        if (vmid is not null) { DiagnosticReads.Bound(vmid, 100, int.MaxValue, nameof(vmid)); pve.EnsureVmAllowed(vmid.Value); }
        if (statusFilter is not null && statusFilter.Split(',').Any(s => s is not ("ok" or "warning" or "error" or "unknown")))
            throw new ArgumentException("Invalid statusFilter; use comma-separated ok, warning, error, unknown.");
        var query = new Dictionary<string, object?> { ["start"] = DiagnosticReads.Bound(start, 0, int.MaxValue, nameof(start), 0),
            ["limit"] = DiagnosticReads.Bound(limit, 50, 500, nameof(limit)), ["source"] = running == true ? "active" : source ?? "archive",
            ["since"] = since, ["until"] = until, ["typefilter"] = typeFilter, ["vmid"] = vmid, ["statusfilter"] = statusFilter };
        var result = await pve.ReadAsync($"nodes/{n}/tasks", query, ct);
        if (running != true) return JsonOpts.Pass(result);
        var filtered = System.Text.Json.Nodes.JsonNode.Parse(result.GetRawText())!.AsObject();
        filtered["data"] = System.Text.Json.JsonSerializer.SerializeToNode(DiagnosticReads.Data(result).EnumerateArray().Where(t =>
            DiagnosticReads.Text(t, "status") == "RUNNING" || (DiagnosticReads.Text(t, "status") is null && DiagnosticReads.Number(t, "endtime") is null)));
        return filtered.ToJsonString();
    }

    [McpServerTool(Name = "get_task_status"),
     Description("Get current status of a task by UPID.")]
    public static async Task<string> GetTaskStatus(
        ProxmoxClient pve,
        [Description("UPID of the task.")] string upid,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        if (!pve.Options.EnableTasks) throw new InvalidOperationException("Task tools are disabled.");
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.GetAsync($"nodes/{n}/tasks/{Uri.EscapeDataString(upid)}/status", ct));
    }

    [McpServerTool(Name = "get_task_log", ReadOnly = true, Idempotent = true, OpenWorld = true),
     Description("Read task log lines forward from a zero-based offset. Preserves upstream line numbers and total; this is not tailing or a snapshot.")]
    public static async Task<string> GetTaskLog(
        ProxmoxClient pve,
        [Description("UPID of the task.")] string upid,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node = null,
        [Description("Lines per page: default 50, maximum 1000.")] int? limit = null,
        CancellationToken ct = default, int? start = null)
    {
        if (!pve.Options.EnableTasks) throw new InvalidOperationException("Task tools are disabled.");
        var n = DiagnosticReads.Segment(pve.ResolveNode(node));
        return JsonOpts.Pass(await pve.ReadAsync($"nodes/{n}/tasks/{DiagnosticReads.Segment(upid)}/log",
            new Dictionary<string, object?> { ["start"] = DiagnosticReads.Bound(start, 0, int.MaxValue, nameof(start), 0),
                ["limit"] = DiagnosticReads.Bound(limit, 50, 1000, nameof(limit)) }, ct));
    }

    [McpServerTool(Name = "stop_task"),
     Description("Stop a running task by UPID. Requires ReadOnly=false.")]
    public static async Task<string> StopTask(
        ProxmoxClient pve,
        [Description("UPID of the task.")] string upid,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        if (!pve.Options.EnableTasks) throw new InvalidOperationException("Task tools are disabled.");
        pve.EnsureWriteAllowed(nameof(StopTask));
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.DeleteAsync($"nodes/{n}/tasks/{Uri.EscapeDataString(upid)}", null, ct));
    }
}
