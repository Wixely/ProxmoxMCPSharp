using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProxmoxMCPSharp.Services;

internal sealed record GuestTransfer(string Kind, int Vmid, bool IsClone, string? Node = null, string? TargetNode = null,
    int? NewVmid = null, string? Name = null, string? TargetStorage = null, bool? Full = null, bool Online = false,
    bool RestartMigration = false, int ShutdownTimeout = 180, bool WithLocalDisks = false,
    int? BandwidthKiB = null, string? ExpectedDigest = null, bool DryRun = true, int WaitSeconds = 10);

internal sealed class GuestTransferService(ProxmoxClient pve)
{
    public async Task<string> Execute(GuestTransfer request, CancellationToken ct)
    {
        var source = GuestManagement.Authorize(pve, request.Kind, request.Node, request.Vmid);
        if (request.IsClone && request.NewVmid is null) throw new ArgumentException("An explicit destination VMID is required.");
        if (!request.IsClone && string.IsNullOrWhiteSpace(request.TargetNode)) throw new ArgumentException("An explicit destination node is required.");
        var destinationId = request.IsClone ? request.NewVmid!.Value : request.Vmid;
        var destination = GuestManagement.Authorize(pve, request.Kind, request.TargetNode ?? source, destinationId);
        GuestManagement.WriteGate(pve, "Clone/migration", pve.Options.AllowDiskManagement, request.DryRun);
        if (!pve.Options.EnableCluster || !pve.Options.EnableNodes) throw new InvalidOperationException("Cluster and node reads are required for clone/migration preflight.");
        if (request.IsClone && destinationId == request.Vmid) throw new ArgumentException("Clone destination must have a different VMID.");
        if (!request.IsClone && source.Equals(destination, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Migration requires a different destination node.");
        if (request.Kind == "lxc" || request.ExpectedDigest is not null) GuestManagement.Digest(request.ExpectedDigest!);
        if (request.TargetStorage is not null) DiagnosticReads.Segment(request.TargetStorage);
        DiagnosticReads.Bound(request.WaitSeconds, 10, 60, nameof(request.WaitSeconds), 0);
        DiagnosticReads.Bound(request.ShutdownTimeout, 180, 600, nameof(request.ShutdownTimeout));
        if (request.BandwidthKiB is { } bandwidth) DiagnosticReads.Bound(bandwidth, 1, 1048576, nameof(request.BandwidthKiB));
        if (request.Name is { } name && (name.Length > 253 || name.Split('.').Any(label => label.Length > 63 || !Regex.IsMatch(label, @"\A[a-zA-Z0-9](?:[a-zA-Z0-9-]*[a-zA-Z0-9])?\z"))))
            throw new ArgumentException("Invalid clone name.");
        if (request.Kind == "lxc" && (request.Online || request.WithLocalDisks)) throw new ArgumentException("LXC live migration is unsupported; use explicit restart migration or stop the container separately.");
        if (request.Kind == "qemu" && request.RestartMigration) throw new ArgumentException("Restart migration is an LXC option.");
        var guest = await GuestManagement.State(pve, source, request.Kind, request.Vmid, request.ExpectedDigest, ct, allowTemplate: true);
        var digest = DiagnosticReads.Text(guest.Config, "digest") ?? throw new InvalidOperationException("Source digest unavailable.");
        GuestManagement.Digest(digest);
        var blockers = await Preflight(request, source, destination, destinationId, guest, ct);
        var parameters = new Dictionary<string, object?> { ["target"] = destination };
        if (request.IsClone)
        {
            parameters["newid"] = destinationId;
            if (request.Name is not null) parameters[request.Kind == "qemu" ? "name" : "hostname"] = request.Name;
            if (request.Full.HasValue) parameters["full"] = request.Full.Value;
            if (request.TargetStorage is not null) parameters["storage"] = request.TargetStorage;
        }
        else
        {
            parameters["online"] = request.Online;
            if (request.Kind == "lxc")
            {
                parameters["restart"] = request.RestartMigration;
                if (request.RestartMigration) parameters["timeout"] = request.ShutdownTimeout;
            }
            else parameters["with-local-disks"] = request.WithLocalDisks;
            if (request.TargetStorage is not null) parameters[request.Kind == "qemu" ? "targetstorage" : "target-storage"] = request.TargetStorage;
        }
        if (request.BandwidthKiB.HasValue) parameters["bwlimit"] = request.BandwidthKiB.Value;
        OperationOutcome? operation = null;
        object? readback = null;
        var warnings = new List<string>
        {
            "Clone/migration APIs do not accept a configuration digest. Preflight rechecks observed state but cannot eliminate races with other administrators.",
            "Storage allocation, bridge availability and migration transport compatibility remain upstream checks; preflight is not a capacity reservation or a guarantee of completion."
        };
        if (!request.DryRun)
        {
            if (blockers.Count > 0) throw new InvalidOperationException(string.Join(" ", blockers));
            var latest = await GuestManagement.State(pve, source, request.Kind, request.Vmid, digest, ct, allowTemplate: true);
            if (guest.Status != latest.Status) throw new InvalidOperationException("Guest run state changed before submission.");
            var recheck = await Preflight(request, source, destination, destinationId, latest, ct);
            if (recheck.Count > 0) throw new InvalidOperationException(string.Join(" ", recheck));
            operation = await ManagementOperation.Submit(pve, source, (request.IsClone ? "clone-" : "migrate-") + request.Kind,
                HttpMethod.Post, GuestManagement.Path(source, request.Kind, request.Vmid) + (request.IsClone ? "/clone" : "/migrate"), parameters, request.WaitSeconds, ct);
            warnings.AddRange(operation.Warnings);
            if (!ct.IsCancellationRequested)
            {
                try
                {
                    var inventory = await Inventory(ct);
                    var target = inventory.Where(v => DiagnosticReads.Number(v, "vmid") == destinationId && DiagnosticReads.Text(v, "type") == request.Kind).ToArray();
                    var located = target.Length == 1 && DiagnosticReads.Text(target[0], "node") == destination;
                    readback = new
                    {
                        destinationObserved = located,
                        destinationStatus = located ? DiagnosticReads.Text(target[0], "status") : null,
                        sourceObserved = inventory.Any(v => DiagnosticReads.Number(v, "vmid") == request.Vmid && DiagnosticReads.Text(v, "node") == source),
                        identityAttribution = request.IsClone ? "inventory-presence-does-not-prove-clone-provenance" : "observed-guest-location",
                        guestUsability = "unverified"
                    };
                }
                catch (Exception ex) { warnings.Add("Destination readback unavailable: " + DiagnosticReads.Error(ex)); }
            }
        }
        Serilog.Log.Information("Guest transfer {Kind} clone={Clone} {Source}/{SourceId} to {Target}/{TargetId}; preview={Preview}, task={HasTask}",
            request.Kind, request.IsClone, source, request.Vmid, destination, destinationId, request.DryRun, operation?.TaskId is not null);
        return JsonSerializer.Serialize(new
        {
            data = operation?.TaskId,
            schemaVersion = 1,
            kind = request.Kind,
            action = request.IsClone ? "clone" : "migrate",
            sourceNode = source,
            sourceVmid = request.Vmid,
            targetNode = destination,
            targetVmid = destinationId,
            targetStorage = request.TargetStorage,
            dryRun = request.DryRun,
            canSubmit = blockers.Count == 0,
            blockers,
            observedDigest = digest,
            sourceStatus = guest.Status,
            downtime = request.IsClone ? "source-not-stopped" : guest.Status == "stopped" ? "guest-already-stopped" : request.RestartMigration ? "explicit-shutdown-and-restart-requested" : request.Online ? "online-migration-requested-with-possible-pause" : "blocked-running-guest-without-migration-mode",
            operation,
            readback,
            warnings,
            nextAction = "Inspect the task and destination guest. Reconcile uncertain results before retrying; verify guest networking, disks and application health separately."
        });
    }

    private async Task<List<string>> Preflight(GuestTransfer request, string source, string destination, int destinationId, GuestState guest, CancellationToken ct)
    {
        var blockers = new List<string>();
        if (guest.Pending.EnumerateArray().Any(p => p.TryGetProperty("pending", out _) || DiagnosticReads.Number(p, "delete") is > 0)) blockers.Add("Source configuration has unresolved pending changes.");
        var inventory = await Inventory(ct);
        var sourceEntries = inventory.Where(v => DiagnosticReads.Number(v, "vmid") == request.Vmid).ToArray();
        if (sourceEntries.Length != 1 || DiagnosticReads.Text(sourceEntries[0], "node") != source || DiagnosticReads.Text(sourceEntries[0], "type") != request.Kind)
            blockers.Add("Source location/type cannot be established from cluster inventory.");
        if (request.IsClone && inventory.Any(v => DiagnosticReads.Number(v, "vmid") == destinationId)) blockers.Add("Destination VMID already exists; no overwrite is permitted.");
        await pve.ReadAsync($"nodes/{DiagnosticReads.Segment(destination)}/status", ct: ct);
        if (!request.IsClone)
        {
            var ha = DiagnosticReads.Data(await pve.ReadAsync("cluster/ha/resources", ct: ct, maxBytes: 1024 * 1024));
            if (ha.EnumerateArray().Any(v => DiagnosticReads.Text(v, "sid") == (request.Kind == "qemu" ? "vm:" : "ct:") + request.Vmid))
                blockers.Add("HA-managed migration may override request options; use an explicitly authorized HA workflow.");
            if (guest.Status == "running" && !(request.Kind == "qemu" ? request.Online : request.RestartMigration))
                blockers.Add("Running guest requires explicit online QEMU migration or explicit LXC restart migration; no implicit stop is permitted.");
        }
        var full = request.Full ?? DiagnosticReads.Flag(guest.Config, "template") != true;
        if (request.IsClone && !full && request.TargetStorage is not null) blockers.Add("Target storage requires a full clone.");
        if (request.TargetStorage is { } storage)
        {
            var config = DiagnosticReads.Data(await pve.ReadAsync("storage/" + DiagnosticReads.Segment(storage), ct: ct));
            var status = await GuestManagement.Storage(pve, destination, storage, ct);
            if (DiagnosticReads.Flag(config, "disable") == true || DiagnosticReads.Flag(status, "enabled") == false || DiagnosticReads.Flag(status, "active") != true)
                blockers.Add("Destination storage is disabled, inactive or unknown.");
            if (!(DiagnosticReads.Text(config, "content") ?? "").Split(',', StringSplitOptions.TrimEntries).Contains(request.Kind == "qemu" ? "images" : "rootdir"))
                blockers.Add("Destination storage content support is incompatible or unknown.");
            var nodes = DiagnosticReads.Text(config, "nodes");
            if (nodes is not null && !nodes.Split(',', StringSplitOptions.TrimEntries).Contains(destination)) blockers.Add("Destination storage excludes the target node.");
            if (request.IsClone && source != destination && DiagnosticReads.Flag(config, "shared") != true) blockers.Add("Cross-node clone requires shared target storage.");
        }
        return blockers;
    }

    private async Task<JsonElement[]> Inventory(CancellationToken ct) => DiagnosticReads.Data(await pve.ReadAsync("cluster/resources",
        new Dictionary<string, object?> { ["type"] = "vm" }, ct, 1024 * 1024)).EnumerateArray().ToArray();
}
