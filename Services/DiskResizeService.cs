using System.Globalization;
using System.Text.Json;

namespace ProxmoxMCPSharp.Services;

public sealed class DiskResizeService(ProxmoxClient pve)
{
    public async Task<string> ResizeAsync(string kind, int vmid, string disk, string size, string expectedDigest,
        string? node = null, bool dryRun = true, int waitSeconds = 10, CancellationToken ct = default)
    {
        node = GuestManagement.Authorize(pve, kind, node, vmid);
        GuestManagement.WriteGate(pve, "Disk resize", pve.Options.AllowDiskResize, dryRun);
        GuestManagement.Digest(expectedDigest);
        GuestManagement.Slot(kind, disk);
        DiagnosticReads.Bound(waitSeconds, 10, 60, nameof(waitSeconds), 0);
        var path = GuestManagement.Path(node, kind, vmid);
        var guest = await GuestManagement.State(pve, node, kind, vmid, expectedDigest, ct);
        if (GuestManagement.HasPending(guest.Pending, disk)) throw new InvalidOperationException("The disk has pending changes; resolve them before resizing.");
        var volume = await GuestManagement.Volume(pve, node, kind, vmid, disk, guest.Config, ct);
        var configuredBytes = volume.Properties.TryGetValue("size", out var configuredSize) ? GuestManagement.Size(configuredSize) : (long?)null;
        if (configuredBytes != volume.Size) throw new InvalidOperationException("Configured and actual volume sizes differ or are unavailable; reconcile the previous operation first.");
        var target = GuestManagement.Size(size, volume.Size);
        if (target <= volume.Size) throw new ArgumentException("Resize must grow the disk; zero growth and shrinking are rejected.");
        var storageConfig = DiagnosticReads.Data(await pve.ReadAsync($"storage/{DiagnosticReads.Segment(volume.Storage)}", ct: ct));
        var backend = DiagnosticReads.Text(storageConfig, "type");
        var supported = backend switch
        {
            "dir" or "nfs" or "cifs" => kind == "qemu" ? volume.Format is "raw" or "qcow2" : volume.Format == "raw",
            "lvm" or "lvmthin" or "rbd" => volume.Format == "raw",
            "zfspool" => kind == "qemu" ? volume.Format == "raw" : volume.Format is "raw" or "subvol",
            _ => false
        };
        if (!supported) throw new InvalidOperationException("This backend/volume format does not have a supported resize contract.");
        var alignment = backend == "lvm" ? 4 * GuestManagement.MiB : GuestManagement.MiB;
        if (target % alignment != 0) throw new ArgumentException($"Target must align to {alignment} bytes for this backend.");
        var capacity = await GuestManagement.Storage(pve, node, volume.Storage, ct);
        var blockers = new List<string>();
        if (DiagnosticReads.Flag(capacity, "active") != true || DiagnosticReads.Flag(capacity, "enabled") == false || DiagnosticReads.Flag(storageConfig, "disable") == true)
            blockers.Add("Storage is inactive, disabled or availability is unknown.");
        var allowedNodes = DiagnosticReads.Text(storageConfig, "nodes");
        if (!string.IsNullOrWhiteSpace(allowedNodes) && !allowedNodes.Split(',', StringSplitOptions.TrimEntries).Contains(node, StringComparer.OrdinalIgnoreCase))
            blockers.Add("Storage is restricted to other nodes.");
        var available = DiagnosticReads.Number(capacity, "avail");
        if (available is null || available < target - volume.Size) blockers.Add("Insufficient or unknown available storage; submission is blocked.");
        var warnings = new List<string> { "Capacity evidence is not a reservation; backend allocation and alignment remain subject to PVE validation.",
            "Virtual capacity and guest filesystem capacity are separate. No reboot or guest commands will be issued." };
        object? backupEvidence = null;
        if (pve.Options.EnableBackups)
        {
            try
            {
                var backups = DiagnosticReads.Data(await pve.ReadAsync($"nodes/{DiagnosticReads.Segment(node)}/storage/{DiagnosticReads.Segment(volume.Storage)}/content",
                    new Dictionary<string, object?> { ["content"] = "backup", ["vmid"] = vmid }, ct));
                var matching = backups.EnumerateArray().Where(b => DiagnosticReads.Number(b, "vmid") == vmid).ToArray();
                backupEvidence = new
                {
                    queriedStorage = volume.Storage,
                    visibleArchiveCount = matching.Length,
                    latestEpoch = matching.Select(b => DiagnosticReads.Number(b, "ctime")).Max(),
                    scope = "selected-storage-only; restore capability unverified"
                };
            }
            catch (Exception ex) when (!ct.IsCancellationRequested) { warnings.Add("Backup evidence unavailable: " + DiagnosticReads.Error(ex)); }
        }
        else warnings.Add("Backup evidence unavailable: backup category disabled.");

        OperationOutcome? operation = null;
        long? observed = null;
        long? observedConfigured = null;
        object? pendingAfter = null;
        if (!dryRun)
        {
            if (blockers.Count > 0) throw new InvalidOperationException(string.Join(" ", blockers));
            // Recheck after evidence gathering. The same digest is also checked by PVE under its guest lock.
            var recheck = await GuestManagement.State(pve, node, kind, vmid, expectedDigest, ct);
            if (DiagnosticReads.Text(recheck.Config, disk) != DiagnosticReads.Text(guest.Config, disk) || GuestManagement.HasPending(recheck.Pending, disk) || recheck.Status != guest.Status)
                throw new InvalidOperationException("Disk identity, pending changes or run state changed before submission.");
            var actual = await GuestManagement.Volume(pve, node, kind, vmid, disk, recheck.Config, ct);
            if (actual.Id != volume.Id || actual.Size != volume.Size) throw new InvalidOperationException("Actual volume changed before submission; reconcile first.");
            operation = await ManagementOperation.Submit(pve, node, "resize-" + kind, HttpMethod.Put, path + "/resize",
                new() { ["disk"] = disk, ["size"] = target.ToString(CultureInfo.InvariantCulture), ["digest"] = expectedDigest }, waitSeconds, ct);
            warnings.AddRange(operation.Warnings);
            if (!ct.IsCancellationRequested)
            {
                try
                {
                    var readback = DiagnosticReads.Data(await pve.ReadAsync(path + "/config", new Dictionary<string, object?> { ["current"] = 1 }, ct));
                    var after = await GuestManagement.Volume(pve, node, kind, vmid, disk, readback, ct);
                    if (after.Id != volume.Id) warnings.Add("Readback volume identity differs; no size success is inferred.");
                    else
                    {
                        observed = after.Size;
                        observedConfigured = after.Properties.TryGetValue("size", out var value) ? GuestManagement.Size(value) : null;
                    }
                    var pending = DiagnosticReads.Data(await pve.ReadAsync(path + "/pending", ct: ct));
                    pendingAfter = pending.EnumerateArray().Where(p => DiagnosticReads.Text(p, "key") == disk).Select(p => DiagnosticReads.Project(p, "key", "value", "pending", "delete")).ToArray();
                }
                catch (Exception ex) { warnings.Add("Readback unavailable: " + DiagnosticReads.Error(ex)); }
            }
        }
        object? filesystemEvidence = null;
        if (kind == "qemu" && guest.Status == "running" && pve.Options.EnableQemuAgent &&
            DiagnosticReads.Text(guest.Config, "agent") is { } agent && (agent == "1" || agent.StartsWith("enabled=1", StringComparison.Ordinal)) && !ct.IsCancellationRequested)
        {
            try
            {
                var data = DiagnosticReads.Data(await pve.ReadAsync(path + "/agent/get-fsinfo", ct: ct));
                var entries = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("result", out var result) ? result : data;
                filesystemEvidence = entries.EnumerateArray().Select(f => DiagnosticReads.Project(f, "name", "mountpoint", "type", "total-bytes", "used-bytes")).ToArray();
                warnings.Add("Guest filesystem inspection is observational; no disk-to-filesystem growth or partition layout is inferred.");
            }
            catch (Exception ex) { warnings.Add("Guest filesystem inspection unavailable: " + DiagnosticReads.Error(ex)); }
        }
        var capacityVerified = observed == target && observedConfigured == target;
        var outcome = dryRun ? "preview" : capacityVerified ? "target-capacity-observed" : "incomplete-or-unknown";
        Serilog.Log.Information("Disk resize {Kind} {Node} {Vmid} {Disk}: {Outcome}", kind, node, vmid, disk, outcome);
        return JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            observedAtUtc = DateTimeOffset.UtcNow,
            node,
            kind,
            vmid,
            disk,
            dryRun,
            outcome,
            canSubmit = blockers.Count == 0,
            blockers,
            volume = volume.Id,
            beforeBytes = volume.Size,
            targetBytes = target,
            resolvedAbsoluteSize = target.ToString(CultureInfo.InvariantCulture),
            expectedDigest,
            observedBytes = observed,
            observedConfiguredBytes = observedConfigured,
            capacityVerified,
            capacityEvidence = new { storage = volume.Storage, backend, availableBytes = available, alignmentBytes = alignment },
            backupEvidence,
            operation,
            pendingChanges = pendingAfter,
            guestRunState = guest.Status,
            guestFilesystem = "unverified",
            filesystemEvidence,
            nextAction = kind == "qemu" ? "Inspect guest filesystem capacity with qemu_agent_get_fsinfo when available; separately authorize any guest partition/filesystem growth."
                : "Inspect task log and container filesystem capacity; LXC volume growth can succeed while filesystem growth emits warnings.",
            warnings
        });
    }
}
