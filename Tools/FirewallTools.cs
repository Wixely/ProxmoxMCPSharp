using System.ComponentModel;
using ProxmoxMCPSharp.Services;
using ModelContextProtocol.Server;

namespace ProxmoxMCPSharp.Tools;

[McpServerToolType]
public static class FirewallTools
{
    // ----- Cluster level --------------------------------------------------

    [McpServerTool(Name = "get_cluster_firewall_options"),
     Description("Get cluster-wide firewall options (enable, policy_in/out, log_ratelimit).")]
    public static async Task<string> GetClusterOptions(ProxmoxClient pve, CancellationToken ct)
    {
        EnsureEnabled(pve);
        return JsonOpts.Pass(await pve.GetAsync("cluster/firewall/options", ct));
    }

    [McpServerTool(Name = "update_cluster_firewall_options"),
     Description("Update cluster-wide firewall options. Pass a comma-separated key=value list (e.g. 'enable=1,policy_in=DROP'). Requires ReadOnly=false.")]
    public static async Task<string> UpdateClusterOptions(
        ProxmoxClient pve,
        [Description("Comma-separated key=value pairs.")] string keyValuePairs,
        CancellationToken ct)
    {
        EnsureEnabled(pve);
        pve.EnsureWriteAllowed(nameof(UpdateClusterOptions));
        return JsonOpts.Pass(await pve.PutAsync("cluster/firewall/options", ParseKvList(keyValuePairs), ct));
    }

    [McpServerTool(Name = "list_cluster_firewall_rules"),
     Description("List cluster-level firewall rules (datacenter-wide policy).")]
    public static async Task<string> ListClusterRules(ProxmoxClient pve, CancellationToken ct)
    {
        EnsureEnabled(pve);
        return JsonOpts.Pass(await pve.GetAsync("cluster/firewall/rules", ct));
    }

    [McpServerTool(Name = "create_cluster_firewall_rule"),
     Description("Add a cluster-level firewall rule. Requires ReadOnly=false.")]
    public static async Task<string> CreateClusterRule(
        ProxmoxClient pve,
        [Description("Action: ACCEPT, DROP, REJECT, or group name.")] string action,
        [Description("Type: in, out, group.")] string type,
        [Description("Optional comma-separated key=value pairs (source, dest, dport, sport, proto, iface, comment, enable=1, etc).")] string? extra,
        CancellationToken ct)
    {
        EnsureEnabled(pve);
        pve.EnsureWriteAllowed(nameof(CreateClusterRule));
        var parameters = new Dictionary<string, object?> { ["action"] = action, ["type"] = type };
        Merge(parameters, ParseKvList(extra));
        return JsonOpts.Pass(await pve.PostAsync("cluster/firewall/rules", parameters, ct));
    }

    [McpServerTool(Name = "delete_cluster_firewall_rule"),
     Description("Delete a cluster-level firewall rule by position index. Requires ReadOnly=false and AllowDestroy=true.")]
    public static async Task<string> DeleteClusterRule(
        ProxmoxClient pve,
        [Description("Rule position index (as returned by list_cluster_firewall_rules).")] int pos,
        CancellationToken ct)
    {
        EnsureEnabled(pve);
        pve.EnsureDestroyAllowed(nameof(DeleteClusterRule));
        return JsonOpts.Pass(await pve.DeleteAsync($"cluster/firewall/rules/{pos}", null, ct));
    }

    [McpServerTool(Name = "list_cluster_firewall_groups"),
     Description("List security groups defined at the cluster firewall level.")]
    public static async Task<string> ListClusterGroups(ProxmoxClient pve, CancellationToken ct)
    {
        EnsureEnabled(pve);
        return JsonOpts.Pass(await pve.GetAsync("cluster/firewall/groups", ct));
    }

    [McpServerTool(Name = "list_cluster_firewall_ipsets"),
     Description("List ipsets defined at the cluster firewall level.")]
    public static async Task<string> ListClusterIpSets(ProxmoxClient pve, CancellationToken ct)
    {
        EnsureEnabled(pve);
        return JsonOpts.Pass(await pve.GetAsync("cluster/firewall/ipset", ct));
    }

    [McpServerTool(Name = "list_cluster_firewall_aliases"),
     Description("List aliases (named IPs/networks) defined at the cluster firewall level.")]
    public static async Task<string> ListClusterAliases(ProxmoxClient pve, CancellationToken ct)
    {
        EnsureEnabled(pve);
        return JsonOpts.Pass(await pve.GetAsync("cluster/firewall/aliases", ct));
    }

    // ----- Node level -----------------------------------------------------

    [McpServerTool(Name = "get_node_firewall_options"),
     Description("Get a node's host firewall options (enable, log levels, nftables/iptables backend).")]
    public static async Task<string> GetNodeOptions(
        ProxmoxClient pve,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        EnsureEnabled(pve);
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.GetAsync($"nodes/{n}/firewall/options", ct));
    }

    [McpServerTool(Name = "update_node_firewall_options"),
     Description("Update a node's host firewall options. Requires ReadOnly=false.")]
    public static async Task<string> UpdateNodeOptions(
        ProxmoxClient pve,
        [Description("Comma-separated key=value pairs.")] string keyValuePairs,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        EnsureEnabled(pve);
        pve.EnsureWriteAllowed(nameof(UpdateNodeOptions));
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.PutAsync($"nodes/{n}/firewall/options", ParseKvList(keyValuePairs), ct));
    }

    [McpServerTool(Name = "list_node_firewall_rules"),
     Description("List a node's host firewall rules.")]
    public static async Task<string> ListNodeRules(
        ProxmoxClient pve,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        EnsureEnabled(pve);
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.GetAsync($"nodes/{n}/firewall/rules", ct));
    }

    [McpServerTool(Name = "create_node_firewall_rule"),
     Description("Add a host firewall rule to a node. Requires ReadOnly=false.")]
    public static async Task<string> CreateNodeRule(
        ProxmoxClient pve,
        [Description("Action: ACCEPT, DROP, REJECT, or group name.")] string action,
        [Description("Type: in, out, group.")] string type,
        [Description("Optional comma-separated key=value pairs.")] string? extra,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        EnsureEnabled(pve);
        pve.EnsureWriteAllowed(nameof(CreateNodeRule));
        var n = pve.ResolveNode(node);
        var parameters = new Dictionary<string, object?> { ["action"] = action, ["type"] = type };
        Merge(parameters, ParseKvList(extra));
        return JsonOpts.Pass(await pve.PostAsync($"nodes/{n}/firewall/rules", parameters, ct));
    }

    [McpServerTool(Name = "delete_node_firewall_rule"),
     Description("Delete a host firewall rule from a node. Requires ReadOnly=false and AllowDestroy=true.")]
    public static async Task<string> DeleteNodeRule(
        ProxmoxClient pve,
        [Description("Rule position index.")] int pos,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        EnsureEnabled(pve);
        pve.EnsureDestroyAllowed(nameof(DeleteNodeRule));
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.DeleteAsync($"nodes/{n}/firewall/rules/{pos}", null, ct));
    }

    // ----- VM / CT level --------------------------------------------------

    [McpServerTool(Name = "get_qemu_firewall_options"),
     Description("Get a QEMU VM's firewall options.")]
    public static async Task<string> GetQemuOptions(
        ProxmoxClient pve,
        [Description("VMID of the QEMU VM.")] int vmid,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        EnsureEnabled(pve);
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.GetAsync($"nodes/{n}/qemu/{vmid}/firewall/options", ct));
    }

    [McpServerTool(Name = "update_qemu_firewall_options"),
     Description("Update a QEMU VM's firewall options. Requires ReadOnly=false.")]
    public static async Task<string> UpdateQemuOptions(
        ProxmoxClient pve,
        [Description("VMID of the QEMU VM.")] int vmid,
        [Description("Comma-separated key=value pairs.")] string keyValuePairs,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        EnsureEnabled(pve);
        pve.EnsureWriteAllowed(nameof(UpdateQemuOptions));
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.PutAsync($"nodes/{n}/qemu/{vmid}/firewall/options", ParseKvList(keyValuePairs), ct));
    }

    [McpServerTool(Name = "list_qemu_firewall_rules"),
     Description("List firewall rules attached to a QEMU VM.")]
    public static async Task<string> ListQemuRules(
        ProxmoxClient pve,
        [Description("VMID of the QEMU VM.")] int vmid,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        EnsureEnabled(pve);
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.GetAsync($"nodes/{n}/qemu/{vmid}/firewall/rules", ct));
    }

    [McpServerTool(Name = "create_qemu_firewall_rule"),
     Description("Add a firewall rule to a QEMU VM. Requires ReadOnly=false.")]
    public static async Task<string> CreateQemuRule(
        ProxmoxClient pve,
        [Description("VMID of the QEMU VM.")] int vmid,
        [Description("Action: ACCEPT, DROP, REJECT, or group name.")] string action,
        [Description("Type: in, out, group.")] string type,
        [Description("Optional comma-separated key=value pairs.")] string? extra,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        EnsureEnabled(pve);
        pve.EnsureWriteAllowed(nameof(CreateQemuRule));
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);
        var parameters = new Dictionary<string, object?> { ["action"] = action, ["type"] = type };
        Merge(parameters, ParseKvList(extra));
        return JsonOpts.Pass(await pve.PostAsync($"nodes/{n}/qemu/{vmid}/firewall/rules", parameters, ct));
    }

    [McpServerTool(Name = "delete_qemu_firewall_rule"),
     Description("Delete a firewall rule from a QEMU VM. Requires ReadOnly=false and AllowDestroy=true.")]
    public static async Task<string> DeleteQemuRule(
        ProxmoxClient pve,
        [Description("VMID of the QEMU VM.")] int vmid,
        [Description("Rule position index.")] int pos,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        EnsureEnabled(pve);
        pve.EnsureDestroyAllowed(nameof(DeleteQemuRule));
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.DeleteAsync($"nodes/{n}/qemu/{vmid}/firewall/rules/{pos}", null, ct));
    }

    [McpServerTool(Name = "list_lxc_firewall_rules"),
     Description("List firewall rules attached to an LXC container.")]
    public static async Task<string> ListLxcRules(
        ProxmoxClient pve,
        [Description("CTID of the container.")] int vmid,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        EnsureEnabled(pve);
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.GetAsync($"nodes/{n}/lxc/{vmid}/firewall/rules", ct));
    }

    [McpServerTool(Name = "create_lxc_firewall_rule"),
     Description("Add a firewall rule to an LXC container. Requires ReadOnly=false.")]
    public static async Task<string> CreateLxcRule(
        ProxmoxClient pve,
        [Description("CTID of the container.")] int vmid,
        [Description("Action: ACCEPT, DROP, REJECT, or group name.")] string action,
        [Description("Type: in, out, group.")] string type,
        [Description("Optional comma-separated key=value pairs.")] string? extra,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        EnsureEnabled(pve);
        pve.EnsureWriteAllowed(nameof(CreateLxcRule));
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);
        var parameters = new Dictionary<string, object?> { ["action"] = action, ["type"] = type };
        Merge(parameters, ParseKvList(extra));
        return JsonOpts.Pass(await pve.PostAsync($"nodes/{n}/lxc/{vmid}/firewall/rules", parameters, ct));
    }

    [McpServerTool(Name = "delete_lxc_firewall_rule"),
     Description("Delete a firewall rule from an LXC container. Requires ReadOnly=false and AllowDestroy=true.")]
    public static async Task<string> DeleteLxcRule(
        ProxmoxClient pve,
        [Description("CTID of the container.")] int vmid,
        [Description("Rule position index.")] int pos,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        EnsureEnabled(pve);
        pve.EnsureDestroyAllowed(nameof(DeleteLxcRule));
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.DeleteAsync($"nodes/{n}/lxc/{vmid}/firewall/rules/{pos}", null, ct));
    }

    // ----- helpers --------------------------------------------------------

    private static void EnsureEnabled(ProxmoxClient pve)
    {
        if (!pve.Options.EnableFirewall)
            throw new InvalidOperationException("Firewall tools are disabled.");
    }

    private static Dictionary<string, object?> ParseKvList(string? raw)
    {
        var dict = new Dictionary<string, object?>();
        if (string.IsNullOrWhiteSpace(raw)) return dict;
        foreach (var pair in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var idx = pair.IndexOf('=');
            if (idx <= 0) continue;
            dict[pair[..idx].Trim()] = pair[(idx + 1)..].Trim();
        }
        return dict;
    }

    private static void Merge(Dictionary<string, object?> target, Dictionary<string, object?> source)
    {
        foreach (var kv in source) target[kv.Key] = kv.Value;
    }
}
