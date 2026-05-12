using System.ComponentModel;
using ProxmoxMCPSharp.Services;
using ModelContextProtocol.Server;

namespace ProxmoxMCPSharp.Tools;

[McpServerToolType]
public static class NodeTools
{
    [McpServerTool(Name = "list_nodes"),
     Description("List all nodes in the Proxmox cluster.")]
    public static async Task<string> ListNodes(ProxmoxClient pve, CancellationToken ct)
    {
        if (!pve.Options.EnableNodes) throw new InvalidOperationException("Node tools are disabled.");
        return JsonOpts.Pass(await pve.GetAsync("nodes", ct));
    }

    [McpServerTool(Name = "get_node_status"),
     Description("Get hardware, kernel, load and memory status for a node.")]
    public static async Task<string> GetNodeStatus(
        ProxmoxClient pve,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct)
    {
        if (!pve.Options.EnableNodes) throw new InvalidOperationException("Node tools are disabled.");
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.GetAsync($"nodes/{n}/status", ct));
    }

    [McpServerTool(Name = "get_node_version"),
     Description("Get Proxmox VE version installed on a node.")]
    public static async Task<string> GetNodeVersion(
        ProxmoxClient pve,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct)
    {
        if (!pve.Options.EnableNodes) throw new InvalidOperationException("Node tools are disabled.");
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.GetAsync($"nodes/{n}/version", ct));
    }

    [McpServerTool(Name = "get_node_subscription"),
     Description("Get a node's subscription/support status.")]
    public static async Task<string> GetNodeSubscription(
        ProxmoxClient pve,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct)
    {
        if (!pve.Options.EnableNodes) throw new InvalidOperationException("Node tools are disabled.");
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.GetAsync($"nodes/{n}/subscription", ct));
    }

    [McpServerTool(Name = "get_node_dns"),
     Description("Get DNS configuration for a node.")]
    public static async Task<string> GetNodeDns(
        ProxmoxClient pve,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct)
    {
        if (!pve.Options.EnableNodes) throw new InvalidOperationException("Node tools are disabled.");
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.GetAsync($"nodes/{n}/dns", ct));
    }

    [McpServerTool(Name = "get_node_time"),
     Description("Get a node's local time and timezone.")]
    public static async Task<string> GetNodeTime(
        ProxmoxClient pve,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct)
    {
        if (!pve.Options.EnableNodes) throw new InvalidOperationException("Node tools are disabled.");
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.GetAsync($"nodes/{n}/time", ct));
    }

    [McpServerTool(Name = "get_node_services"),
     Description("List system services on a node and their state.")]
    public static async Task<string> GetNodeServices(
        ProxmoxClient pve,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct)
    {
        if (!pve.Options.EnableNodes) throw new InvalidOperationException("Node tools are disabled.");
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.GetAsync($"nodes/{n}/services", ct));
    }

    [McpServerTool(Name = "get_node_rrd"),
     Description("Get RRD performance graph data for a node (CPU, memory, disk, net).")]
    public static async Task<string> GetNodeRrd(
        ProxmoxClient pve,
        [Description("Timeframe: hour, day, week, month or year.")] string timeframe,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        [Description("Aggregation: AVERAGE or MAX. Defaults to AVERAGE.")] string? cf,
        CancellationToken ct = default)
    {
        if (!pve.Options.EnableNodes) throw new InvalidOperationException("Node tools are disabled.");
        var n = pve.ResolveNode(node);
        var path = $"nodes/{n}/rrddata?timeframe={Uri.EscapeDataString(timeframe)}";
        if (!string.IsNullOrWhiteSpace(cf)) path += "&cf=" + Uri.EscapeDataString(cf);
        return JsonOpts.Pass(await pve.GetAsync(path, ct));
    }

    [McpServerTool(Name = "get_node_syslog"),
     Description("Get recent system log entries for a node.")]
    public static async Task<string> GetNodeSyslog(
        ProxmoxClient pve,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        [Description("Optional service to filter by.")] string? service,
        [Description("Number of lines to fetch (default 200).")] int? limit,
        CancellationToken ct = default)
    {
        if (!pve.Options.EnableNodes) throw new InvalidOperationException("Node tools are disabled.");
        var n = pve.ResolveNode(node);
        var qs = new List<string>();
        if (!string.IsNullOrWhiteSpace(service)) qs.Add("service=" + Uri.EscapeDataString(service));
        if (limit is not null) qs.Add("limit=" + limit.Value);
        var path = $"nodes/{n}/syslog" + (qs.Count > 0 ? "?" + string.Join('&', qs) : string.Empty);
        return JsonOpts.Pass(await pve.GetAsync(path, ct));
    }

    [McpServerTool(Name = "list_node_disks"),
     Description("List physical disks attached to a node.")]
    public static async Task<string> ListNodeDisks(
        ProxmoxClient pve,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        if (!pve.Options.EnableNodes) throw new InvalidOperationException("Node tools are disabled.");
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.GetAsync($"nodes/{n}/disks/list", ct));
    }
}
