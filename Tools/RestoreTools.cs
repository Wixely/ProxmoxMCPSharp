using System.ComponentModel;
using ProxmoxMCPSharp.Services;
using ModelContextProtocol.Server;

namespace ProxmoxMCPSharp.Tools;

[McpServerToolType]
public static class RestoreTools
{
    [McpServerTool(Name = "restore_qemu_backup"),
     Description("Restore a QEMU VM from a vzdump backup. Same surface as create, but seeded from an archive volume id. Requires ReadOnly=false.")]
    public static async Task<string> RestoreQemu(
        ProxmoxClient pve,
        [Description("New VMID to restore into. Use get_next_vmid for a free id.")] int vmid,
        [Description("Backup archive volume id, e.g. 'local:backup/vzdump-qemu-100-2026_05_12-03_00_00.vma.zst'.")] string archive,
        [Description("If true, overwrite an existing VM with this VMID.")] bool? force,
        [Description("If true, restore VMID/MAC settings from the backup (no random regeneration).")] bool? unique,
        [Description("Target storage to redirect disks into (optional).")] string? storage,
        [Description("Restore bandwidth limit in KiB/s (0 = no limit).")] int? bwlimit,
        [Description("If true, start the VM immediately after restore.")] bool? start,
        [Description("Optional pool to place the restored VM into.")] string? pool,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        EnsureEnabled(pve);
        pve.EnsureWriteAllowed(nameof(RestoreQemu));
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);
        var parameters = new Dictionary<string, object?>
        {
            ["vmid"] = vmid,
            ["archive"] = archive,
        };
        if (force is not null) parameters["force"] = force;
        if (unique is not null) parameters["unique"] = unique;
        if (!string.IsNullOrWhiteSpace(storage)) parameters["storage"] = storage;
        if (bwlimit is not null) parameters["bwlimit"] = bwlimit;
        if (start is not null) parameters["start"] = start;
        if (!string.IsNullOrWhiteSpace(pool)) parameters["pool"] = pool;
        return JsonOpts.Pass(await pve.PostAsync($"nodes/{n}/qemu", parameters, ct));
    }

    [McpServerTool(Name = "restore_lxc_backup"),
     Description("Restore an LXC container from a vzdump backup. Uses POST /nodes/{node}/lxc with restore=1. Requires ReadOnly=false.")]
    public static async Task<string> RestoreLxc(
        ProxmoxClient pve,
        [Description("New CTID to restore into.")] int vmid,
        [Description("Backup archive volume id, e.g. 'local:backup/vzdump-lxc-101-2026_05_12-03_00_00.tar.zst'.")] string archive,
        [Description("Hostname for the restored container. Required when not preserving from the archive.")] string? hostname,
        [Description("Root filesystem spec (e.g. 'storage:8'). Defaults to DefaultStorage:8 if neither archive contents nor this is set.")] string? rootfs,
        [Description("If true, overwrite an existing container with this CTID.")] bool? force,
        [Description("If true, restore CTID/MAC settings from the backup.")] bool? unique,
        [Description("Restore bandwidth limit in KiB/s (0 = no limit).")] int? bwlimit,
        [Description("If true, start the container immediately after restore.")] bool? start,
        [Description("Optional pool to place the restored container into.")] string? pool,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        EnsureEnabled(pve);
        pve.EnsureWriteAllowed(nameof(RestoreLxc));
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);

        var rfs = rootfs;
        if (string.IsNullOrWhiteSpace(rfs) && !string.IsNullOrWhiteSpace(pve.Options.DefaultStorage))
            rfs = $"{pve.Options.DefaultStorage}:8";

        var parameters = new Dictionary<string, object?>
        {
            ["vmid"] = vmid,
            ["ostemplate"] = archive,
            ["restore"] = true,
        };
        if (!string.IsNullOrWhiteSpace(hostname)) parameters["hostname"] = hostname;
        if (!string.IsNullOrWhiteSpace(rfs)) parameters["rootfs"] = rfs;
        if (force is not null) parameters["force"] = force;
        if (unique is not null) parameters["unique"] = unique;
        if (bwlimit is not null) parameters["bwlimit"] = bwlimit;
        if (start is not null) parameters["start"] = start;
        if (!string.IsNullOrWhiteSpace(pool)) parameters["pool"] = pool;

        return JsonOpts.Pass(await pve.PostAsync($"nodes/{n}/lxc", parameters, ct));
    }

    private static void EnsureEnabled(ProxmoxClient pve)
    {
        if (!pve.Options.EnableRestore)
            throw new InvalidOperationException("Restore tools are disabled.");
    }
}
