using System.ComponentModel;
using ProxmoxMCPSharp.Services;
using ModelContextProtocol.Server;

namespace ProxmoxMCPSharp.Tools;

[McpServerToolType]
public static class BackupTools
{
    [McpServerTool(Name = "list_backup_jobs"),
     Description("List configured cluster backup (vzdump) jobs.")]
    public static async Task<string> ListBackupJobs(ProxmoxClient pve, CancellationToken ct)
    {
        if (!pve.Options.EnableBackups) throw new InvalidOperationException("Backup tools are disabled.");
        return JsonOpts.Pass(await pve.GetAsync("cluster/backup", ct));
    }

    [McpServerTool(Name = "get_backup_job"),
     Description("Get details of a single configured backup job.")]
    public static async Task<string> GetBackupJob(
        ProxmoxClient pve,
        [Description("Job id (as returned by list_backup_jobs).")] string id,
        CancellationToken ct)
    {
        if (!pve.Options.EnableBackups) throw new InvalidOperationException("Backup tools are disabled.");
        return JsonOpts.Pass(await pve.GetAsync($"cluster/backup/{Uri.EscapeDataString(id)}", ct));
    }

    [McpServerTool(Name = "create_backup_job"),
     Description("Create a new scheduled backup job. Requires ReadOnly=false.")]
    public static async Task<string> CreateBackupJob(
        ProxmoxClient pve,
        [Description("Storage to write backups to.")] string storage,
        [Description("Schedule in systemd OnCalendar format, e.g. 'mon..fri 03:00'.")] string schedule,
        [Description("Comma-separated VMIDs to include, or 'all'.")] string vmid,
        [Description("Compression: 0, 1 (lzo), gzip, zstd.")] string? compress,
        [Description("Mode: snapshot, suspend, stop.")] string? mode,
        [Description("Job comment.")] string? comment,
        [Description("If true, enable the job immediately.")] bool? enabled,
        [Description("Optional email address for notifications.")] string? mailto,
        [Description("Email notification trigger: always, failure.")] string? mailnotification,
        CancellationToken ct = default)
    {
        if (!pve.Options.EnableBackups) throw new InvalidOperationException("Backup tools are disabled.");
        pve.EnsureWriteAllowed(nameof(CreateBackupJob));
        var parameters = new Dictionary<string, object?>
        {
            ["storage"] = storage,
            ["schedule"] = schedule,
            ["vmid"] = vmid,
        };
        if (!string.IsNullOrWhiteSpace(compress)) parameters["compress"] = compress;
        if (!string.IsNullOrWhiteSpace(mode)) parameters["mode"] = mode;
        if (!string.IsNullOrWhiteSpace(comment)) parameters["comment"] = comment;
        if (enabled is not null) parameters["enabled"] = enabled;
        if (!string.IsNullOrWhiteSpace(mailto)) parameters["mailto"] = mailto;
        if (!string.IsNullOrWhiteSpace(mailnotification)) parameters["mailnotification"] = mailnotification;
        return JsonOpts.Pass(await pve.PostAsync("cluster/backup", parameters, ct));
    }

    [McpServerTool(Name = "update_backup_job"),
     Description("Update an existing scheduled backup job. Requires ReadOnly=false.")]
    public static async Task<string> UpdateBackupJob(
        ProxmoxClient pve,
        [Description("Job id.")] string id,
        [Description("Comma-separated key=value pairs to set on the job.")] string keyValuePairs,
        CancellationToken ct)
    {
        if (!pve.Options.EnableBackups) throw new InvalidOperationException("Backup tools are disabled.");
        pve.EnsureWriteAllowed(nameof(UpdateBackupJob));
        var parameters = new Dictionary<string, object?>();
        foreach (var pair in keyValuePairs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var idx = pair.IndexOf('=');
            if (idx <= 0) continue;
            parameters[pair[..idx].Trim()] = pair[(idx + 1)..].Trim();
        }
        return JsonOpts.Pass(await pve.PutAsync($"cluster/backup/{Uri.EscapeDataString(id)}", parameters, ct));
    }

    [McpServerTool(Name = "delete_backup_job"),
     Description("Delete a scheduled backup job. Requires ReadOnly=false and AllowDestroy=true.")]
    public static async Task<string> DeleteBackupJob(
        ProxmoxClient pve,
        [Description("Job id.")] string id,
        CancellationToken ct)
    {
        if (!pve.Options.EnableBackups) throw new InvalidOperationException("Backup tools are disabled.");
        pve.EnsureDestroyAllowed(nameof(DeleteBackupJob));
        return JsonOpts.Pass(await pve.DeleteAsync($"cluster/backup/{Uri.EscapeDataString(id)}", null, ct));
    }

    [McpServerTool(Name = "start_vzdump_backup"),
     Description("Trigger an ad-hoc vzdump backup on a node. Requires ReadOnly=false and AllowManualBackup=true.")]
    public static async Task<string> StartVzdumpBackup(
        ProxmoxClient pve,
        [Description("Storage to write the backup to.")] string storage,
        [Description("Comma-separated VMIDs to back up. Use 'all' for the whole node.")] string vmid,
        [Description("Compression: 0, 1 (lzo), gzip, zstd.")] string? compress,
        [Description("Mode: snapshot, suspend, stop.")] string? mode,
        [Description("Notes template, e.g. 'manual via ProxmoxMCPSharp'.")] string? notesTemplate,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        if (!pve.Options.EnableBackups) throw new InvalidOperationException("Backup tools are disabled.");
        pve.EnsureWriteAllowed(nameof(StartVzdumpBackup));
        if (!pve.Options.AllowManualBackup)
            throw new InvalidOperationException("Manual backups are disabled. Set Proxmox:AllowManualBackup=true.");
        var n = pve.ResolveNode(node);
        var parameters = new Dictionary<string, object?>
        {
            ["storage"] = storage,
            ["vmid"] = vmid,
        };
        if (!string.IsNullOrWhiteSpace(compress)) parameters["compress"] = compress;
        if (!string.IsNullOrWhiteSpace(mode)) parameters["mode"] = mode;
        if (!string.IsNullOrWhiteSpace(notesTemplate)) parameters["notes-template"] = notesTemplate;
        return JsonOpts.Pass(await pve.PostAsync($"nodes/{n}/vzdump", parameters, ct));
    }
}
