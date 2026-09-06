using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProxmoxMCPSharp.Services;

internal sealed record DiskChange(string Kind, int Vmid, string Action, string Disk, string ExpectedDigest,
    string? Node = null, string? Storage = null, decimal? SizeGiB = null, string? Unused = null,
    string? Format = null, string? Cache = null, bool? Discard = null, bool? Ssd = null, bool? IoThread = null,
    bool DeleteSource = false, int? BandwidthKiB = null, string? MountPath = null, bool? Backup = null,
    bool? ReadOnly = null, bool DryRun = true, int WaitSeconds = 10);

internal sealed record PreparedDiskChange(GuestState Guest, ManagedVolume? Source, string? Format,
    long? RequestedBytes, Dictionary<string, object?> Parameters, string Endpoint, HttpMethod Method,
    object? Capacity, List<string> Blockers);

internal sealed class DiskManagementService(ProxmoxClient pve)
{
    public async Task<string> Execute(DiskChange change, CancellationToken ct)
    {
        var node = GuestManagement.Authorize(pve, change.Kind, change.Node, change.Vmid);
        GuestManagement.WriteGate(pve, "Disk management", pve.Options.AllowDiskManagement, change.DryRun);
        if (change.DeleteSource && !change.DryRun) pve.EnsureDestroyAllowed("Delete source after disk move");
        GuestManagement.Digest(change.ExpectedDigest);
        GuestManagement.Slot(change.Kind, change.Disk);
        DiagnosticReads.Bound(change.WaitSeconds, 10, 60, nameof(change.WaitSeconds), 0);
        if (change.Action is not ("add" or "attach" or "detach" or "move")) throw new ArgumentException("Invalid disk action.");
        if (change.Kind == "lxc" && change.Action == "attach") throw new ArgumentException("LXC attachment is not part of this tool.");
        if (change.Kind == "lxc" && change.Action is "add" or "detach" && change.Disk == "rootfs") throw new ArgumentException("Root filesystem creation/detachment is excluded.");
        if (change.DeleteSource && change.Action != "move") throw new ArgumentException("Source deletion is only an explicit move option.");
        if (change.BandwidthKiB is { } bandwidth) DiagnosticReads.Bound(bandwidth, 1, 1048576, nameof(change.BandwidthKiB));
        var prepared = await Prepare(change, node, ct);
        OperationOutcome? operation = null;
        var warnings = new List<string> { "No guest restart or filesystem initialization is performed. Review task and pending changes before assuming usability." };
        object? readback = null;
        if (!change.DryRun)
        {
            if (prepared.Blockers.Count > 0) throw new InvalidOperationException(string.Join(" ", prepared.Blockers));
            // Repeat preparation immediately before writing, including capacity, ownership, boot and pending checks.
            var recheck = await Prepare(change, node, ct);
            if (recheck.Blockers.Count > 0) throw new InvalidOperationException(string.Join(" ", recheck.Blockers));
            if (prepared.Guest.Status != recheck.Guest.Status || prepared.Source?.Id != recheck.Source?.Id || prepared.Source?.Size != recheck.Source?.Size ||
                JsonSerializer.Serialize(prepared.Parameters) != JsonSerializer.Serialize(recheck.Parameters))
                throw new InvalidOperationException("Disk identity, capacity, options or run state changed before submission.");
            operation = await ManagementOperation.Submit(pve, node, change.Action + "-" + change.Kind + "-disk", prepared.Method,
                GuestManagement.Path(node, change.Kind, change.Vmid) + prepared.Endpoint, prepared.Parameters, change.WaitSeconds, ct);
            warnings.AddRange(operation.Warnings);
            if (!ct.IsCancellationRequested)
            {
                try { readback = await Readback(change, node, prepared, ct); }
                catch (Exception ex) { warnings.Add("Disk readback unavailable: " + DiagnosticReads.Error(ex)); }
            }
        }
        Serilog.Log.Information("Disk management {Action} {Kind} {Node} {Vmid} {Slot}: preview={Preview}, readback={Readback}",
            change.Action, change.Kind, node, change.Vmid, change.Disk, change.DryRun, readback is not null);
        return JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            observedAtUtc = DateTimeOffset.UtcNow,
            node,
            kind = change.Kind,
            vmid = change.Vmid,
            action = change.Action,
            disk = change.Disk,
            dryRun = change.DryRun,
            canSubmit = prepared.Blockers.Count == 0,
            blockers = prepared.Blockers,
            expectedDigest = change.ExpectedDigest,
            sourceVolume = prepared.Source?.Id,
            beforeBytes = prepared.Source?.Size,
            requestedBytes = prepared.RequestedBytes,
            targetStorage = change.Storage,
            targetFormat = prepared.Format,
            sourceDeletionRequested = change.DeleteSource,
            capacityEvidence = prepared.Capacity,
            operation,
            readback,
            warnings,
            nextAction = "Inspect current/pending configuration and task outcome. Reconcile unknown results before retrying; guest filesystem work requires separate authorization."
        });
    }

    private async Task<PreparedDiskChange> Prepare(DiskChange change, string node, CancellationToken ct)
    {
        var guest = await GuestManagement.State(pve, node, change.Kind, change.Vmid, change.ExpectedDigest, ct);
        if (change.IoThread == true && change.Disk.StartsWith("scsi", StringComparison.Ordinal) &&
            (DiagnosticReads.Text(guest.Config, "scsihw") != "virtio-scsi-single" || GuestManagement.HasPending(guest.Pending, "scsihw")))
            throw new InvalidOperationException("SCSI IO threads require an active virtio-scsi-single controller without pending controller changes.");
        if (GuestManagement.HasPending(guest.Pending, change.Disk) || GuestManagement.HasPending(guest.Pending, "boot") || GuestManagement.HasPending(guest.Pending, "bootdisk"))
            throw new InvalidOperationException("Selected disk or boot configuration has unresolved pending changes.");
        if (change.Action is "add" or "attach" && DiagnosticReads.Text(guest.Config, change.Disk) is not null)
            throw new InvalidOperationException("The requested slot is occupied; disks are never implicitly replaced.");
        var parameters = new Dictionary<string, object?> { ["digest"] = change.ExpectedDigest };
        var blockers = new List<string>();
        ManagedVolume? source = null;
        string? format = null;
        long? requestedBytes = null;
        object? capacity = null;
        var endpoint = "/config";
        var method = HttpMethod.Put;
        if (change.Action is "attach" or "detach" or "move")
        {
            var sourceSlot = change.Action == "attach" ? change.Unused : change.Disk;
            if (sourceSlot is null || (change.Action == "attach" && !Regex.IsMatch(sourceSlot, "\\Aunused[0-9]{1,3}\\z")))
                throw new ArgumentException("Attachment requires a discovered unusedN reference.");
            if (GuestManagement.HasPending(guest.Pending, sourceSlot)) throw new InvalidOperationException("The source has pending changes.");
            source = await GuestManagement.Volume(pve, node, change.Kind, change.Vmid, sourceSlot, guest.Config, ct);
            var references = guest.Config.EnumerateObject().Where(p => IsVolumeKey(change.Kind, p.Name) &&
                VolumeId(p.Value.ToString()) == source.Id).ToArray();
            if (references.Length != 1 || guest.Pending.EnumerateArray().Any(p => DiagnosticReads.Text(p, "pending") is { } value && VolumeId(value) == source.Id))
                throw new InvalidOperationException("Volume references are duplicated or pending; reconcile before management.");
            if (source.Properties.TryGetValue("shared", out var shared) && shared == "1") throw new InvalidOperationException("Explicitly shared guest volumes are excluded from disk reassignment.");
            if (change.Action == "detach")
            {
                EnsureNotSystemDisk(change.Kind, change.Disk, guest.Config, source);
                parameters["delete"] = change.Disk; // No force; never remove unusedN, which would delete the volume.
            }
            else if (change.Action == "attach")
            {
                format = source.Format;
                parameters[change.Disk] = source.Id + QemuOptions(change);
            }
        }
        if (change.Action is "add" or "move")
        {
            if (string.IsNullOrWhiteSpace(change.Storage)) throw new ArgumentException("An explicit target storage is required.");
            DiagnosticReads.Segment(change.Storage);
            if (change.Storage == source?.Storage) throw new ArgumentException("Target storage must differ from the source storage.");
            requestedBytes = change.Action == "add" ? AllocationBytes(change.SizeGiB) : source!.Size;
            var target = DiagnosticReads.Data(await pve.ReadAsync("storage/" + DiagnosticReads.Segment(change.Storage), ct: ct));
            var status = await GuestManagement.Storage(pve, node, change.Storage, ct);
            var backend = DiagnosticReads.Text(target, "type");
            format = SelectFormat(change.Kind, backend, change.Format);
            var content = DiagnosticReads.Text(target, "content")?.Split(',', StringSplitOptions.TrimEntries);
            if (content is null || !content.Contains(change.Kind == "qemu" ? "images" : "rootdir")) blockers.Add("Target content support is missing or incompatible.");
            if (DiagnosticReads.Flag(target, "disable") == true || DiagnosticReads.Flag(status, "enabled") == false || DiagnosticReads.Flag(status, "active") != true)
                blockers.Add("Target storage is disabled, inactive or unknown.");
            var nodes = DiagnosticReads.Text(target, "nodes");
            if (!string.IsNullOrWhiteSpace(nodes) && !nodes.Split(',', StringSplitOptions.TrimEntries).Contains(node, StringComparer.OrdinalIgnoreCase))
                blockers.Add("Target storage does not allow this node.");
            var available = DiagnosticReads.Number(status, "avail");
            if (available is null || requestedBytes > available) blockers.Add("Target capacity is unknown or insufficient; no overcommit is implicit.");
            capacity = new { storage = change.Storage, backend, availableBytes = available, requiredBytes = requestedBytes, reservation = false };
            if (change.Action == "add")
            {
                var allocation = change.Storage + ":" + change.SizeGiB!.Value.ToString(CultureInfo.InvariantCulture);
                if (change.Kind == "qemu") allocation += ",format=" + format + QemuOptions(change);
                else
                {
                    MountPath(change.MountPath);
                    if (guest.Pending.EnumerateArray().Any(p =>
                        Regex.IsMatch(DiagnosticReads.Text(p, "key") ?? "", "\\Amp[0-9]+\\z") &&
                        DiagnosticReads.Text(p, "pending") is { } value &&
                        GuestManagement.Properties(value).GetValueOrDefault("mp") == change.MountPath))
                        throw new InvalidOperationException("Guest mount path has an unresolved pending assignment.");
                    if (guest.Config.EnumerateObject().Where(p => Regex.IsMatch(p.Name, "\\Amp[0-9]+\\z")).Any(p =>
                        GuestManagement.Properties(p.Value.ToString()).GetValueOrDefault("mp") == change.MountPath))
                        throw new InvalidOperationException("Guest mount path is already in use.");
                    allocation += ",mp=" + change.MountPath;
                    if (change.Backup.HasValue) allocation += ",backup=" + (change.Backup.Value ? "1" : "0");
                    if (change.ReadOnly.HasValue) allocation += ",ro=" + (change.ReadOnly.Value ? "1" : "0");
                }
                parameters[change.Disk] = allocation;
            }
            else
            {
                endpoint = change.Kind == "qemu" ? "/move_disk" : "/move_volume";
                method = HttpMethod.Post;
                parameters[change.Kind == "qemu" ? "disk" : "volume"] = change.Disk;
                parameters["storage"] = change.Storage;
                parameters["delete"] = change.DeleteSource;
                if (change.Kind == "qemu") parameters["format"] = format;
                if (change.BandwidthKiB.HasValue) parameters["bwlimit"] = change.BandwidthKiB;
                if (change.Kind == "lxc" && guest.Status == "running") blockers.Add("PVE requires a stopped container for volume moves; no stop will be initiated.");
            }
        }
        return new(guest, source, format, requestedBytes, parameters, endpoint, method, capacity, blockers);
    }

    private async Task<object> Readback(DiskChange change, string node, PreparedDiskChange prepared, CancellationToken ct)
    {
        var path = GuestManagement.Path(node, change.Kind, change.Vmid);
        var current = DiagnosticReads.Data(await pve.ReadAsync(path + "/config", new Dictionary<string, object?> { ["current"] = 1 }, ct));
        var pending = DiagnosticReads.Data(await pve.ReadAsync(path + "/pending", ct: ct));
        var currentVolume = VolumeId(DiagnosticReads.Text(current, change.Disk));
        var unused = current.EnumerateObject().Where(p => Regex.IsMatch(p.Name, "\\Aunused[0-9]+\\z") && VolumeId(p.Value.ToString()) == prepared.Source?.Id)
            .Select(p => p.Name).ToArray();
        var targetStorage = change.Storage ?? prepared.Source?.Storage;
        var targetInventory = targetStorage is null ? null : await Inventory(node, change.Kind, change.Vmid, targetStorage, ct);
        var targetEntry = targetInventory?.Where(v => DiagnosticReads.Text(v, "volid") == currentVolume && DiagnosticReads.Number(v, "vmid") == change.Vmid).ToArray();
        var targetBytes = targetEntry?.Length == 1 ? DiagnosticReads.Number(targetEntry[0], "size") : null;
        var targetExpected = change.Action switch
        {
            "detach" => currentVolume is null,
            "attach" => currentVolume == prepared.Source?.Id,
            _ => currentVolume is not null && currentVolume.StartsWith(change.Storage + ":", StringComparison.Ordinal) && currentVolume != prepared.Source?.Id
        };
        bool? sourceVisible = null;
        if (prepared.Source is { } source)
        {
            var sourceInventory = source.Storage == targetStorage ? targetInventory! : await Inventory(node, change.Kind, change.Vmid, source.Storage, ct);
            sourceVisible = sourceInventory.Any(v => DiagnosticReads.Text(v, "volid") == source.Id && DiagnosticReads.Number(v, "vmid") == change.Vmid);
        }
        var selectedPending = pending.EnumerateArray().Where(p => DiagnosticReads.Text(p, "key") == change.Disk ||
            (prepared.Source is not null && DiagnosticReads.Text(p, "key")?.StartsWith("unused", StringComparison.Ordinal) == true &&
             new[] { DiagnosticReads.Text(p, "value"), DiagnosticReads.Text(p, "pending") }.Any(v => VolumeId(v) == prepared.Source?.Id)))
            .Select(p => DiagnosticReads.Project(p, "key", "value", "pending", "delete")).ToArray();
        var pendingChange = GuestManagement.HasPending(pending, change.Disk);
        return new
        {
            currentVolume,
            targetBytes,
            targetReferenceObserved = targetExpected,
            targetCapacityVerified = change.Action is "add" or "move" ? targetExpected && targetBytes >= prepared.RequestedBytes : (bool?)null,
            retainedUnusedReferences = unused,
            sourceVisible,
            sourceRetentionVerified = change.Action is "detach" or "move" && !change.DeleteSource ? sourceVisible == true && unused.Length > 0 : (bool?)null,
            sourceDeletion = change.DeleteSource ? sourceVisible == false ? "not-visible-in-scoped-inventory" : "not-established" : "not-requested",
            pendingChanges = selectedPending,
            requestedConfigurationActive = targetExpected && !pendingChange,
            guestFilesystem = "unverified",
            restart = pendingChange ? "pending-change-may-require-restart" : "no-automatic-restart"
        };
    }

    private async Task<JsonElement[]> Inventory(string node, string kind, int vmid, string storage, CancellationToken ct) =>
        DiagnosticReads.Data(await pve.ReadAsync($"nodes/{DiagnosticReads.Segment(node)}/storage/{DiagnosticReads.Segment(storage)}/content",
            new Dictionary<string, object?> { ["vmid"] = vmid, ["content"] = kind == "qemu" ? "images" : "rootdir" }, ct, 1024 * 1024)).EnumerateArray().ToArray();

    private static long AllocationBytes(decimal? sizeGiB)
    {
        if (sizeGiB is null or <= 0) throw new ArgumentException("Positive sizeGiB is required.");
        var bytes = GuestManagement.Size(sizeGiB.Value.ToString(CultureInfo.InvariantCulture) + "G");
        if (bytes % GuestManagement.MiB != 0) throw new ArgumentException("Allocation size must align to a MiB.");
        return bytes;
    }

    private static string SelectFormat(string kind, string? backend, string? requested)
    {
        var fileBackend = backend is "dir" or "nfs" or "cifs";
        if (!fileBackend && backend is not ("lvm" or "lvmthin" or "zfspool" or "rbd")) throw new InvalidOperationException("Unsupported target storage backend.");
        var format = requested ?? (kind == "lxc" && backend == "zfspool" ? "subvol" : "raw");
        var valid = kind == "qemu" ? format == "raw" || (fileBackend && format == "qcow2") : format == (backend == "zfspool" ? "subvol" : "raw");
        if (!valid) throw new ArgumentException("Requested volume format is incompatible with the backend or guest.");
        return format;
    }

    private static string QemuOptions(DiskChange change)
    {
        var options = "";
        if (change.Cache is not null)
        {
            if (change.Cache is not ("none" or "directsync" or "writethrough" or "writeback")) throw new ArgumentException("Unsupported cache mode.");
            options += ",cache=" + change.Cache;
        }
        if (change.IoThread == true && !(change.Disk.StartsWith("scsi", StringComparison.Ordinal) || change.Disk.StartsWith("virtio", StringComparison.Ordinal)))
            throw new ArgumentException("IO threads require SCSI or VirtIO disks.");
        if (change.Discard.HasValue) options += ",discard=" + (change.Discard.Value ? "on" : "ignore");
        if (change.Ssd.HasValue) options += ",ssd=" + (change.Ssd.Value ? "1" : "0");
        if (change.IoThread.HasValue) options += ",iothread=" + (change.IoThread.Value ? "1" : "0");
        return options;
    }

    private static void EnsureNotSystemDisk(string kind, string disk, JsonElement config, ManagedVolume volume)
    {
        if (kind == "lxc")
        {
            MountPath(volume.Properties.GetValueOrDefault("mp"));
            return;
        }
        var boot = DiagnosticReads.Text(config, "boot");
        var bootDisk = DiagnosticReads.Text(config, "bootdisk");
        if (bootDisk == disk || (boot?.StartsWith("order=", StringComparison.Ordinal) == true && boot[6..].Split(';').Contains(disk)))
            throw new InvalidOperationException("Boot/system disk detachment is excluded.");
        if (boot?.StartsWith("order=", StringComparison.Ordinal) != true && bootDisk is null)
            throw new InvalidOperationException("Boot disk identity is unresolved; detachment is blocked.");
    }

    private static void MountPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/') || path.EndsWith('/') || path.Length > 255 || path.Any(c => char.IsControl(c) || c is ',' or '\\') ||
            path.Split('/').Skip(1).Any(p => p is "" or "." or "..") ||
            new[] { "/", "/proc", "/sys", "/dev", "/etc", "/bin", "/sbin", "/lib", "/lib64", "/usr" }.Any(p => path == p || (p is "/proc" or "/sys" or "/dev" && path.StartsWith(p + "/", StringComparison.Ordinal))))
            throw new ArgumentException("An explicit non-system absolute guest mount path is required.");
    }

    private static bool IsVolumeKey(string kind, string key) => Regex.IsMatch(key, kind == "qemu" ? "\\A(?:(?:ide|sata|scsi|virtio|unused)[0-9]+)\\z" : "\\A(?:rootfs|(?:mp|unused)[0-9]+)\\z");
    private static string? VolumeId(string? value) => value is null ? null : GuestManagement.Properties(value).GetValueOrDefault("volume");
}
