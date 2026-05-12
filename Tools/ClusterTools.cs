using System.ComponentModel;
using ProxmoxMCPSharp.Services;
using ModelContextProtocol.Server;

namespace ProxmoxMCPSharp.Tools;

[McpServerToolType]
public static class ClusterTools
{
    [McpServerTool(Name = "get_version"),
     Description("Get the Proxmox VE API version of the target cluster/node.")]
    public static async Task<string> GetVersion(ProxmoxClient pve, CancellationToken ct)
        => JsonOpts.Pass(await pve.GetAsync("version", ct));

    [McpServerTool(Name = "get_cluster_status"),
     Description("Get cluster-wide status (nodes, quorum, version).")]
    public static async Task<string> GetClusterStatus(ProxmoxClient pve, CancellationToken ct)
    {
        if (!pve.Options.EnableCluster) throw new InvalidOperationException("Cluster tools are disabled.");
        return JsonOpts.Pass(await pve.GetAsync("cluster/status", ct));
    }

    [McpServerTool(Name = "list_cluster_resources"),
     Description("Cluster-wide resource list. Optional type: vm, storage, node, sdn, pool.")]
    public static async Task<string> ListClusterResources(
        ProxmoxClient pve,
        [Description("Optional type filter: vm, storage, node, sdn, pool.")] string? type,
        CancellationToken ct)
    {
        if (!pve.Options.EnableCluster) throw new InvalidOperationException("Cluster tools are disabled.");
        var path = string.IsNullOrWhiteSpace(type) ? "cluster/resources" : $"cluster/resources?type={Uri.EscapeDataString(type)}";
        return JsonOpts.Pass(await pve.GetAsync(path, ct));
    }

    [McpServerTool(Name = "get_cluster_log"),
     Description("Recent cluster log entries.")]
    public static async Task<string> GetClusterLog(
        ProxmoxClient pve,
        [Description("Max number of entries to return.")] int? max,
        CancellationToken ct)
    {
        if (!pve.Options.EnableCluster) throw new InvalidOperationException("Cluster tools are disabled.");
        var path = max is null ? "cluster/log" : $"cluster/log?max={max.Value}";
        return JsonOpts.Pass(await pve.GetAsync(path, ct));
    }

    [McpServerTool(Name = "get_next_vmid"),
     Description("Get the next free VMID the cluster will hand out.")]
    public static async Task<string> GetNextVmId(ProxmoxClient pve, CancellationToken ct)
        => JsonOpts.Pass(await pve.GetAsync("cluster/nextid", ct));

    [McpServerTool(Name = "list_cluster_ha_resources"),
     Description("List HA-managed resources in the cluster.")]
    public static async Task<string> ListHaResources(ProxmoxClient pve, CancellationToken ct)
    {
        if (!pve.Options.EnableCluster) throw new InvalidOperationException("Cluster tools are disabled.");
        return JsonOpts.Pass(await pve.GetAsync("cluster/ha/resources", ct));
    }

    [McpServerTool(Name = "get_cluster_ha_status"),
     Description("Get HA manager status across the cluster.")]
    public static async Task<string> GetHaStatus(ProxmoxClient pve, CancellationToken ct)
    {
        if (!pve.Options.EnableCluster) throw new InvalidOperationException("Cluster tools are disabled.");
        return JsonOpts.Pass(await pve.GetAsync("cluster/ha/status/current", ct));
    }
}
