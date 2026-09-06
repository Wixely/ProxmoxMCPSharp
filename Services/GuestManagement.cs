using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProxmoxMCPSharp.Services;

internal static class GuestManagement
{
    public const long GiB = 1024L * 1024 * 1024;
    public const long MiB = 1024 * 1024;
    public const long MaximumVolumeBytes = 1024 * 1024 * GiB;

    public static string Authorize(ProxmoxClient pve, string kind, string? node, int vmid, bool storage = true)
    {
        if (kind is not ("qemu" or "lxc")) throw new ArgumentException("Invalid guest kind.");
        if (!(kind == "qemu" ? pve.Options.EnableQemu : pve.Options.EnableLxc)) throw new InvalidOperationException("Guest category is disabled.");
        if (storage && !pve.Options.EnableStorage) throw new InvalidOperationException("Storage tools are disabled.");
        if (vmid is < 100 or > 999999999) throw new ArgumentOutOfRangeException(nameof(vmid));
        pve.EnsureVmAllowed(vmid);
        return pve.ResolveNode(node);
    }

    public static void WriteGate(ProxmoxClient pve, string operation, bool allowance, bool dryRun)
    {
        if (dryRun) return;
        pve.EnsureWriteAllowed(operation);
        if (!allowance) throw new InvalidOperationException($"{operation} is disabled by its management allowance.");
    }

    public static string Path(string node, string kind, int vmid) => $"nodes/{DiagnosticReads.Segment(node)}/{kind}/{vmid.ToString(CultureInfo.InvariantCulture)}";

    public static void Digest(string digest)
    {
        if (digest is null || !Regex.IsMatch(digest, "\\A[0-9a-fA-F]{40}\\z")) throw new ArgumentException("An expected 40-character configuration digest is required.");
    }

    public static void Slot(string kind, string slot)
    {
        var valid = kind == "qemu"
            ? Regex.IsMatch(slot, "\\A(?:scsi(?:[0-9]|[12][0-9]|30)|virtio(?:[0-9]|1[0-5])|sata[0-5]|ide[0-3])\\z")
            : Regex.IsMatch(slot, "\\A(?:rootfs|mp(?:[0-9]|[12][0-9]|3[01]))\\z");
        if (!valid) throw new ArgumentException("Invalid managed disk slot; EFI/TPM/unused slots cannot be resized.");
    }

    public static long Size(string value, long? before = null)
    {
        if (value is null || value.Length > 40) throw new ArgumentException("Invalid size.");
        var match = Regex.Match(value, "\\A(\\+)?([0-9]+(?:\\.[0-9]+)?)([KMGT]?)\\z");
        if (!match.Success || !decimal.TryParse(match.Groups[2].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number) || number <= 0)
            throw new ArgumentException("Size must be positive bytes or K/M/G/T (binary units), optionally prefixed with +.");
        try
        {
            var multiplier = match.Groups[3].Value switch { "K" => 1024L, "M" => MiB, "G" => GiB, "T" => 1024 * GiB, _ => 1L };
            var bytes = checked(number * multiplier);
            if (bytes != decimal.Truncate(bytes)) throw new ArgumentException("Size must resolve to whole bytes.");
            if (match.Groups[1].Success) bytes = checked(bytes + (before ?? throw new ArgumentException("Relative size requires the observed original size.")));
            if (bytes > MaximumVolumeBytes) throw new ArgumentException("Volume target exceeds the 1 PiB safety bound.");
            return checked((long)bytes);
        }
        catch (OverflowException) { throw new ArgumentException("Size overflows the supported range."); }
    }

    public static Dictionary<string, string> Properties(string value)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var parts = value.Split(',');
        for (var i = 0; i < parts.Length; i++)
        {
            var pair = parts[i].Split('=', 2);
            var key = pair.Length == 1 && i == 0 ? "volume" : pair[0];
            var val = pair.Length == 1 && i == 0 ? pair[0] : pair.Length == 2 ? pair[1] : throw new ArgumentException("Malformed volume property.");
            if (!fields.TryAdd(key, val)) throw new ArgumentException("Duplicate volume property.");
        }
        if (!fields.ContainsKey("volume") && fields.TryGetValue("file", out var file)) fields["volume"] = file;
        return fields;
    }

    public static async Task<GuestState> State(ProxmoxClient pve, string node, string kind, int vmid, string? expectedDigest, CancellationToken ct)
    {
        var path = Path(node, kind, vmid);
        var config = DiagnosticReads.Data(await pve.ReadAsync(path + "/config", new Dictionary<string, object?> { ["current"] = 1 }, ct));
        if (expectedDigest is not null && !string.Equals(DiagnosticReads.Text(config, "digest"), expectedDigest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Configuration digest changed; read and preview the current configuration again.");
        if (!string.IsNullOrEmpty(DiagnosticReads.Text(config, "lock"))) throw new InvalidOperationException("Guest configuration is locked.");
        if (DiagnosticReads.Flag(config, "template") == true) throw new InvalidOperationException("Template management is not supported by this operation.");
        var pending = DiagnosticReads.Data(await pve.ReadAsync(path + "/pending", ct: ct));
        if (pending.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("Pending configuration is unavailable.");
        var status = DiagnosticReads.Data(await pve.ReadAsync(path + "/status/current", ct: ct));
        var state = DiagnosticReads.Text(status, "status");
        if (state is not ("running" or "stopped")) throw new InvalidOperationException("Guest run state is unknown or unsupported.");
        return new GuestState(config, pending, state);
    }

    public static bool HasPending(JsonElement pending, string key) => pending.EnumerateArray().Any(p => DiagnosticReads.Text(p, "key") == key &&
        (p.TryGetProperty("pending", out _) || DiagnosticReads.Number(p, "delete") is > 0));

    public static async Task<ManagedVolume> Volume(ProxmoxClient pve, string node, string kind, int vmid, string slot, JsonElement config, CancellationToken ct)
    {
        var definition = DiagnosticReads.Text(config, slot) ?? throw new ArgumentException("The selected disk slot is empty.");
        var fields = Properties(definition);
        if (fields.GetValueOrDefault("media") == "cdrom") throw new InvalidOperationException("Optical media cannot be managed as a disk.");
        var volume = fields.GetValueOrDefault("volume") ?? throw new InvalidOperationException("No volume identity.");
        var separator = volume.IndexOf(':');
        if (separator <= 0 || volume.StartsWith('/') || volume.Any(char.IsControl)) throw new InvalidOperationException("Only PVE-managed volumes are supported; bind/device mounts are excluded.");
        var storage = volume[..separator];
        DiagnosticReads.Segment(storage);
        var content = DiagnosticReads.Data(await pve.ReadAsync($"nodes/{DiagnosticReads.Segment(node)}/storage/{DiagnosticReads.Segment(storage)}/content",
            new Dictionary<string, object?> { ["vmid"] = vmid, ["content"] = kind == "qemu" ? "images" : "rootdir" }, ct));
        var matches = content.EnumerateArray().Where(v => DiagnosticReads.Text(v, "volid") == volume).ToArray();
        if (matches.Length != 1 || DiagnosticReads.Number(matches[0], "vmid") != vmid) throw new InvalidOperationException("Volume ownership cannot be established for this guest.");
        var size = DiagnosticReads.Number(matches[0], "size");
        if (size is null or <= 0) throw new InvalidOperationException("Actual volume capacity is unavailable.");
        return new ManagedVolume(volume, storage, size.Value, DiagnosticReads.Text(matches[0], "format"), fields);
    }

    public static async Task<JsonElement> Storage(ProxmoxClient pve, string node, string storage, CancellationToken ct) =>
        DiagnosticReads.Data(await pve.ReadAsync($"nodes/{DiagnosticReads.Segment(node)}/storage/{DiagnosticReads.Segment(storage)}/status", ct: ct));

    public static void ValidateLegacyConfig(ProxmoxClient pve, IDictionary<string, object?> fields)
    {
        foreach (var key in fields.Keys)
        {
            if (key is "name" or "description" or "onboot" or "startup" or "tags" or "digest") continue;
            if (key is "cores" or "sockets" or "vcpus" or "cpu" or "cpulimit" or "cpuunits" or "memory" or "balloon")
            {
                if (!pve.Options.AllowResourceChanges) throw new InvalidOperationException("Resource changes require AllowResourceChanges=true.");
                throw new InvalidOperationException("Use the typed resource tool for validated CPU/memory changes.");
            }
            throw new InvalidOperationException("This configuration field requires a focused management tool; arbitrary device, delete, force and lock parameters are not accepted.");
        }
    }
}

internal sealed record GuestState(JsonElement Config, JsonElement Pending, string Status);
internal sealed record ManagedVolume(string Id, string Storage, long Size, string? Format, Dictionary<string, string> Properties);
