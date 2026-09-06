using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using ProxmoxMCPSharp.Services;

namespace ProxmoxMCPSharp.Tools;

[McpServerToolType]
public static class DiskTools
{
    [McpServerTool(Name = "get_node_disk_smart", ReadOnly = true, Idempotent = true, OpenWorld = true),
     Description("Read SMART attributes, vendor text or unknown health for a discovered block device. Does not initiate a SMART test. Requires root-path Sys.Audit.")]
    public static async Task<string> GetSmart(ProxmoxClient pve, string disk, string? node = null, bool? healthOnly = null, CancellationToken ct = default)
    {
        if (!pve.Options.EnableNodes) throw new InvalidOperationException("Node tools are disabled.");
        if (!disk.StartsWith("/dev/", StringComparison.Ordinal) || disk.Split('/').Any(s => s is "." or "..") || disk.Any(char.IsControl))
            throw new ArgumentException("Use the /dev/ path returned by list_node_disks.");
        var n = DiagnosticReads.Segment(pve.ResolveNode(node));
        return JsonOpts.Pass(await pve.ReadAsync($"nodes/{n}/disks/smart", new Dictionary<string, object?> {
            ["disk"] = disk, ["healthonly"] = healthOnly.HasValue ? (healthOnly.Value ? 1 : 0) : null }, ct));
    }

    [McpServerTool(Name = "list_node_directory_mounts", ReadOnly = true, Idempotent = true, OpenWorld = true),
     Description("Read PVE-managed mount definitions, not the full live mount table. Mount options are omitted because they can contain credentials.")]
    public static async Task<string> ListDirectories(ProxmoxClient pve, string? node = null, CancellationToken ct = default)
    {
        if (!pve.Options.EnableNodes) throw new InvalidOperationException("Node tools are disabled.");
        var n = DiagnosticReads.Segment(pve.ResolveNode(node));
        var result = await pve.ReadAsync($"nodes/{n}/disks/directory", ct: ct);
        return JsonSerializer.Serialize(new { data = DiagnosticReads.Data(result).EnumerateArray().Select(d => DiagnosticReads.Project(d, "path", "device", "type", "unitfile")) });
    }
}
