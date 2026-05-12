using System.ComponentModel;
using ProxmoxMCPSharp.Services;
using ModelContextProtocol.Server;

namespace ProxmoxMCPSharp.Tools;

[McpServerToolType]
public static class AptTools
{
    [McpServerTool(Name = "list_apt_updates"),
     Description("List APT updates available for a node (packages with newer versions in the repos).")]
    public static async Task<string> ListUpdates(
        ProxmoxClient pve,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        EnsureEnabled(pve);
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.GetAsync($"nodes/{n}/apt/update", ct));
    }

    [McpServerTool(Name = "refresh_apt_database"),
     Description("Run an 'apt-get update' on a node to refresh the package database. Requires ReadOnly=false.")]
    public static async Task<string> RefreshDatabase(
        ProxmoxClient pve,
        [Description("If true, send an email summary to the node's notification target.")] bool? notify,
        [Description("If true, run in quiet mode.")] bool? quiet,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        EnsureEnabled(pve);
        pve.EnsureWriteAllowed(nameof(RefreshDatabase));
        var n = pve.ResolveNode(node);
        var parameters = new Dictionary<string, object?>();
        if (notify is not null) parameters["notify"] = notify;
        if (quiet is not null) parameters["quiet"] = quiet;
        return JsonOpts.Pass(await pve.PostAsync($"nodes/{n}/apt/update", parameters, ct));
    }

    [McpServerTool(Name = "get_apt_versions"),
     Description("Get the installed versions of the Proxmox VE stack packages on a node.")]
    public static async Task<string> GetVersions(
        ProxmoxClient pve,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        EnsureEnabled(pve);
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.GetAsync($"nodes/{n}/apt/versions", ct));
    }

    [McpServerTool(Name = "get_apt_changelog"),
     Description("Get the changelog for an APT package on a node.")]
    public static async Task<string> GetChangelog(
        ProxmoxClient pve,
        [Description("Package name (e.g. 'pve-manager').")] string name,
        [Description("Optional specific version to fetch the changelog for.")] string? version,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        EnsureEnabled(pve);
        var n = pve.ResolveNode(node);
        var qs = new List<string> { "name=" + Uri.EscapeDataString(name) };
        if (!string.IsNullOrWhiteSpace(version)) qs.Add("version=" + Uri.EscapeDataString(version));
        return JsonOpts.Pass(await pve.GetAsync($"nodes/{n}/apt/changelog?" + string.Join('&', qs), ct));
    }

    [McpServerTool(Name = "list_apt_repositories"),
     Description("List the APT repositories configured on a node and their handling status.")]
    public static async Task<string> ListRepositories(
        ProxmoxClient pve,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        EnsureEnabled(pve);
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.GetAsync($"nodes/{n}/apt/repositories", ct));
    }

    private static void EnsureEnabled(ProxmoxClient pve)
    {
        if (!pve.Options.EnableApt)
            throw new InvalidOperationException("APT tools are disabled.");
    }
}
