using System.ComponentModel;
using ProxmoxMCPSharp.Services;
using ModelContextProtocol.Server;

namespace ProxmoxMCPSharp.Tools;

[McpServerToolType]
public static class TaskTools
{
    [McpServerTool(Name = "list_node_tasks"),
     Description("List recent tasks (UPIDs) on a node.")]
    public static async Task<string> ListTasks(
        ProxmoxClient pve,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        [Description("Only running tasks if true.")] bool? running,
        [Description("Number of tasks to fetch (default 50).")] int? limit,
        [Description("Filter by source: active, all.")] string? source,
        CancellationToken ct = default)
    {
        if (!pve.Options.EnableTasks) throw new InvalidOperationException("Task tools are disabled.");
        var n = pve.ResolveNode(node);
        var qs = new List<string>();
        if (running == true) qs.Add("running=1");
        if (limit is not null) qs.Add("limit=" + limit.Value);
        if (!string.IsNullOrWhiteSpace(source)) qs.Add("source=" + Uri.EscapeDataString(source));
        var path = $"nodes/{n}/tasks" + (qs.Count > 0 ? "?" + string.Join('&', qs) : string.Empty);
        return JsonOpts.Pass(await pve.GetAsync(path, ct));
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

    [McpServerTool(Name = "get_task_log"),
     Description("Get the log of a task by UPID.")]
    public static async Task<string> GetTaskLog(
        ProxmoxClient pve,
        [Description("UPID of the task.")] string upid,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        [Description("Number of lines from the end (default 1000).")] int? limit,
        CancellationToken ct = default)
    {
        if (!pve.Options.EnableTasks) throw new InvalidOperationException("Task tools are disabled.");
        var n = pve.ResolveNode(node);
        var path = $"nodes/{n}/tasks/{Uri.EscapeDataString(upid)}/log";
        if (limit is not null) path += "?limit=" + limit.Value;
        return JsonOpts.Pass(await pve.GetAsync(path, ct));
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
