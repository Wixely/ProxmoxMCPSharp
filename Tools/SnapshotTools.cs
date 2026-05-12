using System.ComponentModel;
using ProxmoxMCPSharp.Services;
using ModelContextProtocol.Server;

namespace ProxmoxMCPSharp.Tools;

[McpServerToolType]
public static class SnapshotTools
{
    [McpServerTool(Name = "list_qemu_snapshots"),
     Description("List snapshots of a QEMU VM.")]
    public static async Task<string> ListQemuSnapshots(
        ProxmoxClient pve,
        [Description("VMID of the QEMU VM.")] int vmid,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        if (!pve.Options.EnableSnapshots) throw new InvalidOperationException("Snapshot tools are disabled.");
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.GetAsync($"nodes/{n}/qemu/{vmid}/snapshot", ct));
    }

    [McpServerTool(Name = "create_qemu_snapshot"),
     Description("Take a snapshot of a QEMU VM. Requires ReadOnly=false.")]
    public static async Task<string> CreateQemuSnapshot(
        ProxmoxClient pve,
        [Description("VMID of the QEMU VM.")] int vmid,
        [Description("Snapshot name (must match ^[A-Za-z][A-Za-z0-9_-]{1,39}$).")] string snapname,
        [Description("Optional description.")] string? description,
        [Description("If true, also save the VM's RAM state. VM must be running.")] bool? includeRam,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        if (!pve.Options.EnableSnapshots) throw new InvalidOperationException("Snapshot tools are disabled.");
        pve.EnsureWriteAllowed(nameof(CreateQemuSnapshot));
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);
        var parameters = new Dictionary<string, object?> { ["snapname"] = snapname };
        if (!string.IsNullOrWhiteSpace(description)) parameters["description"] = description;
        if (includeRam is not null) parameters["vmstate"] = includeRam;
        return JsonOpts.Pass(await pve.PostAsync($"nodes/{n}/qemu/{vmid}/snapshot", parameters, ct));
    }

    [McpServerTool(Name = "delete_qemu_snapshot"),
     Description("Delete a snapshot from a QEMU VM. Requires ReadOnly=false and AllowDestroy=true.")]
    public static async Task<string> DeleteQemuSnapshot(
        ProxmoxClient pve,
        [Description("VMID of the QEMU VM.")] int vmid,
        [Description("Snapshot name.")] string snapname,
        [Description("If true, force deletion even if children depend on it.")] bool? force,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        if (!pve.Options.EnableSnapshots) throw new InvalidOperationException("Snapshot tools are disabled.");
        pve.EnsureDestroyAllowed(nameof(DeleteQemuSnapshot));
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);
        var parameters = new Dictionary<string, object?>();
        if (force is not null) parameters["force"] = force;
        return JsonOpts.Pass(await pve.DeleteAsync($"nodes/{n}/qemu/{vmid}/snapshot/{snapname}", parameters, ct));
    }

    [McpServerTool(Name = "rollback_qemu_snapshot"),
     Description("Roll a QEMU VM back to a snapshot. Requires ReadOnly=false and AllowSnapshotRollback=true.")]
    public static async Task<string> RollbackQemuSnapshot(
        ProxmoxClient pve,
        [Description("VMID of the QEMU VM.")] int vmid,
        [Description("Snapshot name to roll back to.")] string snapname,
        [Description("If true, start the VM after rollback.")] bool? start,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        if (!pve.Options.EnableSnapshots) throw new InvalidOperationException("Snapshot tools are disabled.");
        pve.EnsureWriteAllowed(nameof(RollbackQemuSnapshot));
        if (!pve.Options.AllowSnapshotRollback)
            throw new InvalidOperationException("Snapshot rollback is disabled. Set Proxmox:AllowSnapshotRollback=true.");
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);
        var parameters = new Dictionary<string, object?>();
        if (start is not null) parameters["start"] = start;
        return JsonOpts.Pass(await pve.PostAsync($"nodes/{n}/qemu/{vmid}/snapshot/{snapname}/rollback", parameters, ct));
    }

    [McpServerTool(Name = "list_lxc_snapshots"),
     Description("List snapshots of an LXC container.")]
    public static async Task<string> ListLxcSnapshots(
        ProxmoxClient pve,
        [Description("CTID of the container.")] int vmid,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        if (!pve.Options.EnableSnapshots) throw new InvalidOperationException("Snapshot tools are disabled.");
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.GetAsync($"nodes/{n}/lxc/{vmid}/snapshot", ct));
    }

    [McpServerTool(Name = "create_lxc_snapshot"),
     Description("Take a snapshot of an LXC container. Requires ReadOnly=false.")]
    public static async Task<string> CreateLxcSnapshot(
        ProxmoxClient pve,
        [Description("CTID of the container.")] int vmid,
        [Description("Snapshot name.")] string snapname,
        [Description("Optional description.")] string? description,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        if (!pve.Options.EnableSnapshots) throw new InvalidOperationException("Snapshot tools are disabled.");
        pve.EnsureWriteAllowed(nameof(CreateLxcSnapshot));
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);
        var parameters = new Dictionary<string, object?> { ["snapname"] = snapname };
        if (!string.IsNullOrWhiteSpace(description)) parameters["description"] = description;
        return JsonOpts.Pass(await pve.PostAsync($"nodes/{n}/lxc/{vmid}/snapshot", parameters, ct));
    }

    [McpServerTool(Name = "delete_lxc_snapshot"),
     Description("Delete a snapshot from an LXC container. Requires ReadOnly=false and AllowDestroy=true.")]
    public static async Task<string> DeleteLxcSnapshot(
        ProxmoxClient pve,
        [Description("CTID of the container.")] int vmid,
        [Description("Snapshot name.")] string snapname,
        [Description("If true, force deletion.")] bool? force,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        if (!pve.Options.EnableSnapshots) throw new InvalidOperationException("Snapshot tools are disabled.");
        pve.EnsureDestroyAllowed(nameof(DeleteLxcSnapshot));
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);
        var parameters = new Dictionary<string, object?>();
        if (force is not null) parameters["force"] = force;
        return JsonOpts.Pass(await pve.DeleteAsync($"nodes/{n}/lxc/{vmid}/snapshot/{snapname}", parameters, ct));
    }

    [McpServerTool(Name = "rollback_lxc_snapshot"),
     Description("Roll an LXC container back to a snapshot. Requires ReadOnly=false and AllowSnapshotRollback=true.")]
    public static async Task<string> RollbackLxcSnapshot(
        ProxmoxClient pve,
        [Description("CTID of the container.")] int vmid,
        [Description("Snapshot name to roll back to.")] string snapname,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        if (!pve.Options.EnableSnapshots) throw new InvalidOperationException("Snapshot tools are disabled.");
        pve.EnsureWriteAllowed(nameof(RollbackLxcSnapshot));
        if (!pve.Options.AllowSnapshotRollback)
            throw new InvalidOperationException("Snapshot rollback is disabled. Set Proxmox:AllowSnapshotRollback=true.");
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.PostAsync($"nodes/{n}/lxc/{vmid}/snapshot/{snapname}/rollback", null, ct));
    }
}
