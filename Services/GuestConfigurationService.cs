using System.Text.Json;

namespace ProxmoxMCPSharp.Services;

public sealed class GuestConfigurationService(ProxmoxClient pve)
{
    internal async Task<string> UpdateResources(string kind, int vmid, string expectedDigest, string? node,
        int? cores, int? sockets, int? vcpus, int? memoryMiB, int? balloonMiB, int? swapMiB,
        double? cpuLimit, int? cpuUnits, bool dryRun, int waitSeconds, CancellationToken ct)
    {
        node = GuestManagement.Authorize(pve, kind, node, vmid, storage: false);
        GuestManagement.WriteGate(pve, "Resource changes", pve.Options.AllowResourceChanges, dryRun);
        if (!pve.Options.EnableNodes) throw new InvalidOperationException("Node reads are required for resource capacity evidence.");
        GuestManagement.Digest(expectedDigest);
        var fields = new Dictionary<string, object?>();
        void Add(string name, int? value, int minimum, int maximum)
        {
            if (value is null) return;
            if (value < minimum || value > maximum) throw new ArgumentOutOfRangeException(name, $"Expected {minimum} through {maximum}.");
            fields[name] = value;
        }
        Add("cores", cores, 1, 1024);
        Add("memory", memoryMiB, 16, 1048576);
        Add("cpuunits", cpuUnits, 1, kind == "lxc" ? 10000 : 262144);
        if (cpuLimit.HasValue)
        {
            if (!double.IsFinite(cpuLimit.Value) || cpuLimit < 0 || cpuLimit > 1024) throw new ArgumentOutOfRangeException(nameof(cpuLimit));
            fields["cpulimit"] = cpuLimit;
        }
        if (kind == "qemu")
        {
            if (swapMiB is not null) throw new ArgumentException("Swap is an LXC resource setting.");
            Add("sockets", sockets, 1, 1024);
            Add("vcpus", vcpus, 1, 1024);
            Add("balloon", balloonMiB, 0, 1048576);
        }
        else
        {
            if (sockets is not null || vcpus is not null || balloonMiB is not null) throw new ArgumentException("Sockets, vcpus and balloon are QEMU resource settings.");
            Add("swap", swapMiB, 0, 1048576);
        }
        if (fields.Count == 0) throw new ArgumentException("At least one resource change is required.");
        DiagnosticReads.Bound(waitSeconds, 10, 60, nameof(waitSeconds), 0);
        var guest = await GuestManagement.State(pve, node, kind, vmid, expectedDigest, ct);
        string[] resourceKeys = ["cores", "sockets", "vcpus", "memory", "balloon", "swap", "cpulimit", "cpuunits"];
        if (resourceKeys.Any(k => GuestManagement.HasPending(guest.Pending, k))) throw new InvalidOperationException("Resources have unresolved pending changes.");
        long Value(string key, long fallback) => fields.TryGetValue(key, out var value) ? Convert.ToInt64(value) :
            (key == "memory" ? MemoryValue(guest.Config) : DiagnosticReads.Number(guest.Config, key)) ?? fallback;
        var virtualCpuCount = checked(Value("cores", 1) * (kind == "qemu" ? Value("sockets", 1) : 1));
        if (virtualCpuCount > 1024 || (kind == "qemu" && Value("vcpus", virtualCpuCount) > virtualCpuCount)) throw new ArgumentException("vcpus must not exceed cores times sockets; total virtual CPUs must not exceed 1024.");
        var requestedMemory = Value("memory", 512);
        if (kind == "qemu" && Value("balloon", 0) > requestedMemory) throw new ArgumentException("Balloon memory must not exceed configured memory.");
        var host = DiagnosticReads.Data(await pve.ReadAsync($"nodes/{DiagnosticReads.Segment(node)}/status", ct: ct));
        var hostCpus = host.TryGetProperty("cpuinfo", out var cpu) ? DiagnosticReads.Number(cpu, "cpus") : null;
        var freeMemory = host.TryGetProperty("memory", out var memory) ? DiagnosticReads.Number(memory, "free") : null;
        var oldMemory = MemoryValue(guest.Config);
        var blockers = new List<string>();
        if (fields.Keys.Any(k => k is "cores" or "sockets" or "vcpus") && (hostCpus is null || virtualCpuCount > hostCpus))
            blockers.Add("Requested virtual CPUs exceed observed host capacity or host CPU capacity is unknown.");
        if (cpuLimit is > 0 && (hostCpus is null || cpuLimit > hostCpus))
            blockers.Add("Requested CPU limit exceeds observed host capacity or host CPU capacity is unknown.");
        var growth = (requestedMemory - (oldMemory ?? 0)) * GuestManagement.MiB;
        if (fields.ContainsKey("memory") && (oldMemory is null || (growth > 0 && (freeMemory is null || growth > freeMemory))))
            blockers.Add("Memory increase exceeds observed free host memory or capacity evidence is unknown.");
        if (!dryRun && blockers.Count > 0) throw new InvalidOperationException(string.Join(" ", blockers));
        return await Apply(kind, node, vmid, expectedDigest, guest, fields, dryRun, waitSeconds, new { hostCpus, freeMemoryBytes = freeMemory }, blockers, ct);
    }

    internal async Task<string> Apply(string kind, string node, int vmid, string expectedDigest, GuestState guest,
        Dictionary<string, object?> fields, bool dryRun, int waitSeconds, object? evidence, List<string> blockers, CancellationToken ct)
    {
        var path = GuestManagement.Path(node, kind, vmid);
        var keys = fields.Keys.ToArray();
        OperationOutcome? operation = null;
        JsonElement? current = null;
        JsonElement? pending = null;
        var warnings = new List<string>();
        if (!dryRun)
        {
            var latest = await GuestManagement.State(pve, node, kind, vmid, expectedDigest, ct);
            if (latest.Status != guest.Status || keys.Any(k => DiagnosticReads.Text(latest.Config, k) != DiagnosticReads.Text(guest.Config, k) || GuestManagement.HasPending(latest.Pending, k)))
                throw new InvalidOperationException("Configuration or run state changed before submission.");
            fields["digest"] = expectedDigest;
            operation = await ManagementOperation.Submit(pve, node, "configure-" + kind, HttpMethod.Put, path + "/config", fields, waitSeconds, ct);
            fields.Remove("digest");
            warnings.AddRange(operation.Warnings);
            if (!ct.IsCancellationRequested)
            {
                try
                {
                    current = DiagnosticReads.Data(await pve.ReadAsync(path + "/config", new Dictionary<string, object?> { ["current"] = 1 }, ct));
                    pending = DiagnosticReads.Data(await pve.ReadAsync(path + "/pending", ct: ct));
                }
                catch (Exception ex) { warnings.Add("Configuration readback unavailable: " + DiagnosticReads.Error(ex)); }
            }
        }
        var pendingEntries = pending?.EnumerateArray().Where(p => keys.Contains(DiagnosticReads.Text(p, "key"))).ToArray();
        var hasPending = pendingEntries?.Any(p => p.TryGetProperty("pending", out _) || DiagnosticReads.Number(p, "delete") is > 0);
        var matches = current is { } config && fields.All(kv => kv.Key == "memory"
            ? MemoryValue(config) == Convert.ToInt64(kv.Value)
            : DiagnosticReads.Text(config, kv.Key) == Convert.ToString(kv.Value, System.Globalization.CultureInfo.InvariantCulture));
        Serilog.Log.Information("Guest configuration {Kind} {Node} {Vmid}: preview={Preview}, currentMatches={Matches}", kind, node, vmid, dryRun, matches);
        return JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            observedAtUtc = DateTimeOffset.UtcNow,
            kind,
            node,
            vmid,
            dryRun,
            canSubmit = blockers.Count == 0,
            blockers,
            expectedDigest,
            before = DiagnosticReads.Project(guest.Config, keys),
            requested = fields,
            current = current is { } c ? DiagnosticReads.Project(c, keys) : null,
            pending = pendingEntries?.Select(p => DiagnosticReads.Project(p, "key", "value", "pending", "delete")),
            currentMatchesRequested = matches,
            restart = hasPending == true ? "pending-changes-may-require-restart" : matches ? "no-pending-requested-values-observed" : "unverified",
            operation,
            capacityEvidence = evidence,
            warnings,
            nextAction = "Review current and pending values. No restart was initiated; authorize one separately if required."
        });
    }

    private static long? MemoryValue(JsonElement config)
    {
        var value = DiagnosticReads.Text(config, "memory");
        if (value is null) return 512; // PVE 8.2/8.3 documented QEMU and LXC default.
        if (value.StartsWith("current=", StringComparison.Ordinal)) value = value[8..];
        return long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var memory) ? memory : null;
    }
}
