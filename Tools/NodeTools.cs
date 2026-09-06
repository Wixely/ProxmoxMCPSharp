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

    [McpServerTool(Name = "get_node_syslog", ReadOnly = true, Idempotent = true, OpenWorld = true),
     Description("Get recent system log entries for a node.")]
    public static async Task<string> GetNodeSyslog(
        ProxmoxClient pve,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node = null,
        [Description("Optional service to filter by.")] string? service = null,
        [Description("Number of lines to fetch (default 200, maximum 1000).")] int? limit = null,
        CancellationToken ct = default, int? start = null,
        [Description("Node-local date/time: yyyy-MM-dd or yyyy-MM-dd HH:mm[:ss].")] string? since = null,
        [Description("Node-local date/time: yyyy-MM-dd or yyyy-MM-dd HH:mm[:ss].")] string? until = null)
    {
        if (!pve.Options.EnableNodes) throw new InvalidOperationException("Node tools are disabled.");
        var n = DiagnosticReads.Segment(pve.ResolveNode(node));
        DateTime? Parse(string? value) => value is null ? null : DateTime.TryParseExact(value,
            ["yyyy-MM-dd", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss"], System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var date) ? date : throw new ArgumentException("Invalid syslog date/time.");
        var from = Parse(since); var to = Parse(until);
        if (from > to || service?.Length > 128) throw new ArgumentException("Invalid syslog range or service length.");
        return JsonOpts.Pass(await pve.ReadAsync($"nodes/{n}/syslog", new Dictionary<string, object?> {
            ["start"] = DiagnosticReads.Bound(start, 0, int.MaxValue, nameof(start), 0),
            ["limit"] = DiagnosticReads.Bound(limit, 200, 1000, nameof(limit)), ["service"] = service, ["since"] = since, ["until"] = until }, ct));
    }

    [McpServerTool(Name = "list_node_disks", ReadOnly = true, Idempotent = true, OpenWorld = true),
     Description("List physical disks attached to a node.")]
    public static async Task<string> ListNodeDisks(
        ProxmoxClient pve,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node = null,
        CancellationToken ct = default, bool? skipSmart = null, bool? includePartitions = null)
    {
        if (!pve.Options.EnableNodes) throw new InvalidOperationException("Node tools are disabled.");
        var n = DiagnosticReads.Segment(pve.ResolveNode(node));
        return JsonOpts.Pass(await pve.ReadAsync($"nodes/{n}/disks/list", new Dictionary<string, object?> {
            ["skipsmart"] = skipSmart.HasValue ? (skipSmart.Value ? 1 : 0) : null,
            ["include-partitions"] = includePartitions.HasValue ? (includePartitions.Value ? 1 : 0) : null }, ct));
    }
}
