using System.ComponentModel;
using ModelContextProtocol.Server;
using ProxmoxMCPSharp.Services;

namespace ProxmoxMCPSharp.Tools;

[McpServerToolType]
public static class GuestDiskManagementTools
{
    [McpServerTool(Name = "add_qemu_disk", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true),
     Description("Create a managed QEMU disk in an explicit empty slot. Positive sizeGiB and target storage are required. Defaults to preview; writes require ReadOnly=false and AllowDiskManagement. Retains current/pending evidence without formatting the guest filesystem.")]
    public static Task<string> AddQemu(ProxmoxClient pve, int vmid, string disk, string storage, decimal sizeGiB, string expectedDigest,
        string? node = null, string? format = null, string? cache = null, bool? discard = null, bool? ssd = null, bool? ioThread = null,
        bool dryRun = true, int waitSeconds = 10, CancellationToken ct = default) =>
        new DiskManagementService(pve).Execute(new("qemu", vmid, "add", disk, expectedDigest, node, storage, sizeGiB,
            Format: format, Cache: cache, Discard: discard, Ssd: ssd, IoThread: ioThread, DryRun: dryRun, WaitSeconds: waitSeconds), ct);

    [McpServerTool(Name = "attach_qemu_disk", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true),
     Description("Attach a discovered unusedN volume owned by this guest to an empty QEMU disk slot. No cross-guest reassignment or implicit allocation. Defaults to preview; submission requires AllowDiskManagement and ReadOnly=false.")]
    public static Task<string> AttachQemu(ProxmoxClient pve, int vmid, string disk, string unused, string expectedDigest,
        string? node = null, string? cache = null, bool? discard = null, bool? ssd = null, bool? ioThread = null,
        bool dryRun = true, int waitSeconds = 10, CancellationToken ct = default) =>
        new DiskManagementService(pve).Execute(new("qemu", vmid, "attach", disk, expectedDigest, node, Unused: unused,
            Cache: cache, Discard: discard, Ssd: ssd, IoThread: ioThread, DryRun: dryRun, WaitSeconds: waitSeconds), ct);

    [McpServerTool(Name = "detach_qemu_disk", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true),
     Description("Detach an explicit non-boot QEMU disk while retaining its volume as unusedN. Rejects unresolved boot identity, optical/EFI/TPM disks and pending conflicts. Defaults to preview; no physical deletion.")]
    public static Task<string> DetachQemu(ProxmoxClient pve, int vmid, string disk, string expectedDigest,
        string? node = null, bool dryRun = true, int waitSeconds = 10, CancellationToken ct = default) =>
        new DiskManagementService(pve).Execute(new("qemu", vmid, "detach", disk, expectedDigest, node, DryRun: dryRun, WaitSeconds: waitSeconds), ct);

    [McpServerTool(Name = "move_qemu_disk", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true),
     Description("Move a managed QEMU disk to target storage within the same guest. Retains source as unused by default. deleteSource=true additionally requires AllowDestroy for submission. Defaults to preview; reports task, target and source evidence separately.")]
    public static Task<string> MoveQemu(ProxmoxClient pve, int vmid, string disk, string storage, string expectedDigest,
        string? node = null, string? format = null, int? bandwidthKiB = null, bool deleteSource = false,
        bool dryRun = true, int waitSeconds = 10, CancellationToken ct = default) =>
        new DiskManagementService(pve).Execute(new("qemu", vmid, "move", disk, expectedDigest, node, storage,
            Format: format, DeleteSource: deleteSource, BandwidthKiB: bandwidthKiB, DryRun: dryRun, WaitSeconds: waitSeconds), ct);

    [McpServerTool(Name = "move_lxc_volume", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true),
     Description("Move rootfs or a managed LXC mpN to storage within the same container. Requires a stopped container; never stops it automatically. Retains source unless deleteSource is explicit and AllowDestroy permits it. Defaults to preview.")]
    public static Task<string> MoveLxc(ProxmoxClient pve, int vmid, string volume, string storage, string expectedDigest,
        string? node = null, int? bandwidthKiB = null, bool deleteSource = false, bool dryRun = true, int waitSeconds = 10, CancellationToken ct = default) =>
        new DiskManagementService(pve).Execute(new("lxc", vmid, "move", volume, expectedDigest, node, storage,
            DeleteSource: deleteSource, BandwidthKiB: bandwidthKiB, DryRun: dryRun, WaitSeconds: waitSeconds), ct);

    [McpServerTool(Name = "add_lxc_mount_point", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true),
     Description("Add a storage-backed managed LXC mount at an explicit empty mpN and non-system guest path. No host bind/device mounts or privilege changes. Defaults to preview; writes require AllowDiskManagement and ReadOnly=false.")]
    public static Task<string> AddLxcMount(ProxmoxClient pve, int vmid, string disk, string storage, decimal sizeGiB, string mountPath, string expectedDigest,
        string? node = null, bool? backup = null, bool? readOnly = null, bool dryRun = true, int waitSeconds = 10, CancellationToken ct = default) =>
        new DiskManagementService(pve).Execute(new("lxc", vmid, "add", disk, expectedDigest, node, storage, sizeGiB,
            MountPath: mountPath, Backup: backup, ReadOnly: readOnly, DryRun: dryRun, WaitSeconds: waitSeconds), ct);

    [McpServerTool(Name = "detach_lxc_mount_point", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true),
     Description("Detach an explicit managed LXC mpN, retaining its volume as unused. Excludes rootfs and system/bind/device mounts. Reports pending state without restarting. Defaults to preview.")]
    public static Task<string> DetachLxcMount(ProxmoxClient pve, int vmid, string disk, string expectedDigest,
        string? node = null, bool dryRun = true, int waitSeconds = 10, CancellationToken ct = default) =>
        new DiskManagementService(pve).Execute(new("lxc", vmid, "detach", disk, expectedDigest, node, DryRun: dryRun, WaitSeconds: waitSeconds), ct);
}
