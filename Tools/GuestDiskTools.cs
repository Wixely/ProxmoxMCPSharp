using System.ComponentModel;
using ModelContextProtocol.Server;
using ProxmoxMCPSharp.Services;

namespace ProxmoxMCPSharp.Tools;

[McpServerToolType]
public static class GuestDiskTools
{
    [McpServerTool(Name = "resize_qemu_disk", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true),
     Description("Grow an explicit managed QEMU disk. Defaults to read-only preview; dryRun=false requires ReadOnly=false and AllowDiskResize=true. Size uses binary K/M/G/T or bytes (+10G or 74G). Requires current digest. Resolves increments once and never retries a write. Reports task/readback separately from guest filesystem space.")]
    public static Task<string> ResizeQemu(ProxmoxClient pve, int vmid, string disk, string size, string expectedDigest,
        string? node = null, bool dryRun = true, int waitSeconds = 10, CancellationToken ct = default) =>
        new DiskResizeService(pve).ResizeAsync("qemu", vmid, disk, size, expectedDigest, node, dryRun, waitSeconds, ct);

    [McpServerTool(Name = "resize_lxc_disk", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true),
     Description("Grow LXC rootfs or a managed mpN volume owned by the guest; no bind/device mounts. Defaults to read-only preview. Submission requires ReadOnly=false and AllowDiskResize=true. PVE may attempt filesystem growth; task warnings and usable space remain separate from volume growth. Never retries relative growth.")]
    public static Task<string> ResizeLxc(ProxmoxClient pve, int vmid, string disk, string size, string expectedDigest,
        string? node = null, bool dryRun = true, int waitSeconds = 10, CancellationToken ct = default) =>
        new DiskResizeService(pve).ResizeAsync("lxc", vmid, disk, size, expectedDigest, node, dryRun, waitSeconds, ct);

    [McpServerTool(Name = "get_qemu_pending_config", ReadOnly = true, Idempotent = true, OpenWorld = true),
     Description("Read current/pending/deleted QEMU configuration values and retain upstream envelope.")]
    public static Task<string> QemuPending(ProxmoxClient pve, int vmid, string? node = null, CancellationToken ct = default) => Pending(pve, "qemu", vmid, node, ct);

    [McpServerTool(Name = "get_lxc_pending_config", ReadOnly = true, Idempotent = true, OpenWorld = true),
     Description("Read current/pending/deleted LXC configuration values and retain upstream envelope.")]
    public static Task<string> LxcPending(ProxmoxClient pve, int vmid, string? node = null, CancellationToken ct = default) => Pending(pve, "lxc", vmid, node, ct);

    private static async Task<string> Pending(ProxmoxClient pve, string kind, int vmid, string? node, CancellationToken ct)
    {
        var n = GuestManagement.Authorize(pve, kind, node, vmid, storage: false);
        return JsonOpts.Pass(await pve.ReadAsync(GuestManagement.Path(n, kind, vmid) + "/pending", ct: ct));
    }
}
