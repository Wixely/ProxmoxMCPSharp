using System.Text.Json;

namespace ProxmoxMCPSharp.Services;

public sealed class StorageDiagnosticsService(ProxmoxClient pve)
{
    public async Task<string> GetAsync(string node, string storage, CancellationToken ct = default)
    {
        if (!pve.Options.EnableStorage) throw new InvalidOperationException("Storage tools are disabled.");
        node = pve.ResolveNode(node);
        var n = DiagnosticReads.Segment(node);
        var s = DiagnosticReads.Segment(storage);
        var errors = new List<object>();
        var missing = new List<string>();
        var observations = new List<string>();
        var backing = new List<object>();
        var reads = 0;
        async Task<JsonElement?> Read(string component, string path, JsonValueKind kind = JsonValueKind.Object)
        {
            try
            {
                if (++reads > 8) throw new InvalidOperationException("Read budget exceeded.");
                var data = DiagnosticReads.Data(await pve.ReadAsync(path, ct: ct));
                if (data.ValueKind != kind) throw new JsonException();
                return data;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                errors.Add(new { component, reason = DiagnosticReads.Error(ex) });
                return null;
            }
        }

        var config = await Read("configuration", $"storage/{s}");
        var status = await Read("status", $"nodes/{n}/storage/{s}/status");
        var availability = "unknown";
        if (status is { } st)
        {
            availability = DiagnosticReads.Flag(st, "active") switch { true => "active", false => "inactive", _ => "unknown" };
            if (DiagnosticReads.Flag(st, "enabled") == false) availability = "disabled";
        }
        if (config is { } c)
        {
            if (DiagnosticReads.Flag(c, "disable") == true) { availability = "disabled"; observations.Add("storage-disabled"); }
            var nodes = DiagnosticReads.Text(c, "nodes");
            if (!string.IsNullOrEmpty(nodes) && !nodes.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Contains(node, StringComparer.OrdinalIgnoreCase))
            { availability = "node-restricted"; observations.Add("storage-restricted-to-other-nodes"); }
            var type = DiagnosticReads.Text(c, "type");
            if (type is "dir" or "zfspool")
            {
                if (!pve.Options.EnableNodes) missing.Add("node-components-disabled");
                else if (type == "dir")
                {
                    var mounts = await Read("managedDirectories", $"nodes/{n}/disks/directory", JsonValueKind.Array);
                    var path = DiagnosticReads.Text(c, "path");
                    var matches = mounts is { ValueKind: JsonValueKind.Array } dirs && path is not null
                        ? dirs.EnumerateArray().Where(d => DiagnosticReads.Text(d, "path")?.TrimEnd('/') == path.TrimEnd('/')).ToArray() : [];
                    foreach (var match in matches) backing.Add(new { kind = "managed-directory-definition", configurationOnly = true,
                        evidence = DiagnosticReads.Project(match, "path", "device", "type", "unitfile") });
                    if (matches.Length == 0) missing.Add("no-matching-managed-directory-definition");
                    missing.Add("live-mount-state-unverified");
                }
                else
                {
                    var pool = DiagnosticReads.Text(c, "pool");
                    if (string.IsNullOrWhiteSpace(pool) || pool.StartsWith('/') || pool.Split('/').Any(part => part is "" or "." or ".." || part.Contains('\\') || part.Any(char.IsControl))) missing.Add("configured-pool-missing-or-invalid");
                    else
                    {
                        // zfspool may name a dataset. Its first component identifies the physical pool.
                        var poolName = DiagnosticReads.Segment(pool.Split('/')[0]);
                        var detail = await Read("zfsPool", $"nodes/{n}/disks/zfs/{poolName}");
                        if (detail is { } d) backing.Add(new { kind = "zfs-pool", configuredPool = pool, evidence = d });
                    }
                }
            }
            else missing.Add("unsupported-backend-backing-diagnostics");
        }
        else missing.Add("configuration-unavailable");
        if (availability == "unknown") missing.Add("availability-unknown");
        observations.Add("API visibility depends on privileges; a missing definition does not establish a missing mount or its cause.");
        return JsonSerializer.Serialize(new { schemaVersion = 1, observedAtUtc = DateTimeOffset.UtcNow, node, storage,
            configurationSummary = config is { } cfg ? DiagnosticReads.Project(cfg, "type", "content", "nodes", "disable", "is_mountpoint", "path", "pool", "shared") : null,
            availability, statusEvidence = status is { } stats ? DiagnosticReads.Project(stats, "active", "enabled", "avail", "used", "total") : null,
            backingEvidence = backing, observations, missingEvidence = missing, partial = errors.Count > 0 || missing.Count > 0, errors, upstreamReads = reads });
    }
}
