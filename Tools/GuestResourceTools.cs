using System.ComponentModel;
using ModelContextProtocol.Server;
using ProxmoxMCPSharp.Services;

namespace ProxmoxMCPSharp.Tools;

[McpServerToolType]
public static class GuestResourceTools
{
    [McpServerTool(Name = "update_qemu_resources", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true),
     Description("Preview or update typed QEMU CPU/memory resources with a current digest and host capacity evidence. Memory/balloon use MiB. Defaults to dryRun=true; submission needs AllowResourceChanges and ReadOnly=false. Reports current/pending values without restarting.")]
    public static Task<string> UpdateQemu(ProxmoxClient pve, int vmid, string expectedDigest, string? node = null,
        int? cores = null, int? sockets = null, int? vcpus = null, int? memoryMiB = null, int? balloonMiB = null,
        double? cpuLimit = null, int? cpuUnits = null, bool dryRun = true, int waitSeconds = 10, CancellationToken ct = default) =>
        new GuestConfigurationService(pve).UpdateResources("qemu", vmid, expectedDigest, node, cores, sockets, vcpus, memoryMiB, balloonMiB, null, cpuLimit, cpuUnits, dryRun, waitSeconds, ct);

    [McpServerTool(Name = "update_lxc_resources", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true),
     Description("Preview or update typed LXC CPU/memory/swap with a current digest and host capacity evidence. Memory/swap use MiB. Defaults to dryRun=true; submission needs AllowResourceChanges and ReadOnly=false. No restart is initiated.")]
    public static Task<string> UpdateLxc(ProxmoxClient pve, int vmid, string expectedDigest, string? node = null,
        int? cores = null, int? memoryMiB = null, int? swapMiB = null, double? cpuLimit = null, int? cpuUnits = null,
        bool dryRun = true, int waitSeconds = 10, CancellationToken ct = default) =>
        new GuestConfigurationService(pve).UpdateResources("lxc", vmid, expectedDigest, node, cores, null, null, memoryMiB, null, swapMiB, cpuLimit, cpuUnits, dryRun, waitSeconds, ct);
}
