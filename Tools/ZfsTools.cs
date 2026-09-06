using System.ComponentModel;
using ModelContextProtocol.Server;
using ProxmoxMCPSharp.Services;

namespace ProxmoxMCPSharp.Tools;

[McpServerToolType]
public static class ZfsTools
{
    [McpServerTool(Name = "list_node_zfs_pools", ReadOnly = true, Idempotent = true, OpenWorld = true),
     Description("Read physical ZFS pool capacity and health independently of datastore allocation. Requires root-path Sys.Audit.")]
    public static async Task<string> ListPools(ProxmoxClient pve, string? node = null, CancellationToken ct = default)
    {
        if (!pve.Options.EnableNodes) throw new InvalidOperationException("Node tools are disabled.");
        return JsonOpts.Pass(await pve.ReadAsync($"nodes/{DiagnosticReads.Segment(pve.ResolveNode(node))}/disks/zfs", ct: ct));
    }

    [McpServerTool(Name = "get_node_zfs_pool_status", ReadOnly = true, Idempotent = true, OpenWorld = true),
     Description("Read pool state, errors, vdev hierarchy and current/latest scan text. This is not historical scrub evidence. Requires root-path Sys.Audit.")]
    public static async Task<string> GetPoolStatus(ProxmoxClient pve, string pool, string? node = null, CancellationToken ct = default)
    {
        if (!pve.Options.EnableNodes) throw new InvalidOperationException("Node tools are disabled.");
        return JsonOpts.Pass(await pve.ReadAsync($"nodes/{DiagnosticReads.Segment(pve.ResolveNode(node))}/disks/zfs/{DiagnosticReads.Segment(pool)}", ct: ct));
    }
}
