using System.ComponentModel;
using ProxmoxMCPSharp.Services;
using ModelContextProtocol.Server;

namespace ProxmoxMCPSharp.Tools;

[McpServerToolType]
public static class NetworkTools
{
    [McpServerTool(Name = "list_node_network"),
     Description("List network devices configured on a node (bridges, bonds, eths, vlans).")]
    public static async Task<string> ListNodeNetwork(
        ProxmoxClient pve,
        [Description("Optional type filter: bridge, bond, eth, vlan, OVSBridge, alias.")] string? type,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        if (!pve.Options.EnableNetwork) throw new InvalidOperationException("Network tools are disabled.");
        var n = pve.ResolveNode(node);
        var path = $"nodes/{n}/network";
        if (!string.IsNullOrWhiteSpace(type)) path += "?type=" + Uri.EscapeDataString(type);
        return JsonOpts.Pass(await pve.GetAsync(path, ct));
    }

    [McpServerTool(Name = "get_node_network_interface"),
     Description("Get the configuration of a single network interface on a node.")]
    public static async Task<string> GetInterface(
        ProxmoxClient pve,
        [Description("Interface name (e.g. vmbr0).")] string iface,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        if (!pve.Options.EnableNetwork) throw new InvalidOperationException("Network tools are disabled.");
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.GetAsync($"nodes/{n}/network/{iface}", ct));
    }

    [McpServerTool(Name = "create_node_bridge"),
     Description("Create a new bridge on a node. Configuration is pending until reload_node_network. Requires ReadOnly=false.")]
    public static async Task<string> CreateBridge(
        ProxmoxClient pve,
        [Description("Bridge name, e.g. 'vmbr1'.")] string iface,
        [Description("Optional bridge_ports value, e.g. 'eno2'.")] string? bridgePorts,
        [Description("Optional CIDR address, e.g. '10.0.0.1/24'.")] string? cidr,
        [Description("Optional comment.")] string? comments,
        [Description("If true, bring up automatically at boot.")] bool? autostart,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        if (!pve.Options.EnableNetwork) throw new InvalidOperationException("Network tools are disabled.");
        pve.EnsureWriteAllowed(nameof(CreateBridge));
        var n = pve.ResolveNode(node);
        var parameters = new Dictionary<string, object?>
        {
            ["type"] = "bridge",
            ["iface"] = iface,
        };
        if (!string.IsNullOrWhiteSpace(bridgePorts)) parameters["bridge_ports"] = bridgePorts;
        if (!string.IsNullOrWhiteSpace(cidr)) parameters["cidr"] = cidr;
        if (!string.IsNullOrWhiteSpace(comments)) parameters["comments"] = comments;
        if (autostart is not null) parameters["autostart"] = autostart;
        return JsonOpts.Pass(await pve.PostAsync($"nodes/{n}/network", parameters, ct));
    }

    [McpServerTool(Name = "reload_node_network"),
     Description("Apply pending network configuration changes on a node. Requires ReadOnly=false.")]
    public static async Task<string> ReloadNetwork(
        ProxmoxClient pve,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        if (!pve.Options.EnableNetwork) throw new InvalidOperationException("Network tools are disabled.");
        pve.EnsureWriteAllowed(nameof(ReloadNetwork));
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.PutAsync($"nodes/{n}/network", null, ct));
    }

    [McpServerTool(Name = "revert_node_network"),
     Description("Discard pending network configuration changes on a node. Requires ReadOnly=false.")]
    public static async Task<string> RevertNetwork(
        ProxmoxClient pve,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        if (!pve.Options.EnableNetwork) throw new InvalidOperationException("Network tools are disabled.");
        pve.EnsureWriteAllowed(nameof(RevertNetwork));
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.DeleteAsync($"nodes/{n}/network", null, ct));
    }
}
