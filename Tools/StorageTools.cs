using System.ComponentModel;
using ProxmoxMCPSharp.Services;
using ModelContextProtocol.Server;

namespace ProxmoxMCPSharp.Tools;

[McpServerToolType]
public static class StorageTools
{
    [McpServerTool(Name = "list_storages"),
     Description("List storages defined cluster-wide (with their type/content).")]
    public static async Task<string> ListStorages(ProxmoxClient pve, CancellationToken ct)
    {
        if (!pve.Options.EnableStorage) throw new InvalidOperationException("Storage tools are disabled.");
        return JsonOpts.Pass(await pve.GetAsync("storage", ct));
    }

    [McpServerTool(Name = "list_node_storage"),
     Description("List storages available on a node, with usage statistics.")]
    public static async Task<string> ListNodeStorage(
        ProxmoxClient pve,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        if (!pve.Options.EnableStorage) throw new InvalidOperationException("Storage tools are disabled.");
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.GetAsync($"nodes/{n}/storage", ct));
    }

    [McpServerTool(Name = "get_storage_status"),
     Description("Get usage status (used/total/avail) for a storage on a node.")]
    public static async Task<string> GetStorageStatus(
        ProxmoxClient pve,
        [Description("Storage identifier (e.g. 'local', 'local-lvm').")] string storage,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        if (!pve.Options.EnableStorage) throw new InvalidOperationException("Storage tools are disabled.");
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.GetAsync($"nodes/{n}/storage/{storage}/status", ct));
    }

    [McpServerTool(Name = "list_storage_content"),
     Description("List content (images, ISOs, backups, templates) in a storage.")]
    public static async Task<string> ListStorageContent(
        ProxmoxClient pve,
        [Description("Storage identifier.")] string storage,
        [Description("Optional content filter: iso, vztmpl, backup, images, rootdir, snippets.")] string? content,
        [Description("Filter content owned by a specific VMID.")] int? vmid,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        if (!pve.Options.EnableStorage) throw new InvalidOperationException("Storage tools are disabled.");
        var n = pve.ResolveNode(node);
        var qs = new List<string>();
        if (!string.IsNullOrWhiteSpace(content)) qs.Add("content=" + Uri.EscapeDataString(content));
        if (vmid is not null) qs.Add("vmid=" + vmid.Value);
        var path = $"nodes/{n}/storage/{storage}/content" + (qs.Count > 0 ? "?" + string.Join('&', qs) : string.Empty);
        return JsonOpts.Pass(await pve.GetAsync(path, ct));
    }

    [McpServerTool(Name = "delete_storage_content"),
     Description("Delete a specific volume from a storage (e.g. a backup file). Requires ReadOnly=false and AllowDestroy=true.")]
    public static async Task<string> DeleteStorageContent(
        ProxmoxClient pve,
        [Description("Storage identifier.")] string storage,
        [Description("Volume id, e.g. 'local:backup/vzdump-qemu-100-...'.")] string volume,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        if (!pve.Options.EnableStorage) throw new InvalidOperationException("Storage tools are disabled.");
        pve.EnsureDestroyAllowed(nameof(DeleteStorageContent));
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.DeleteAsync($"nodes/{n}/storage/{storage}/content/{Uri.EscapeDataString(volume)}", null, ct));
    }
}
