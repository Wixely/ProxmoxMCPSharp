using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProxmoxMCPSharp.Services;

internal sealed class StructuredConfigurationService(ProxmoxClient pve)
{
    private static readonly string[] ResourceKeys = ["cores", "sockets", "vcpus", "memory", "balloon", "swap", "cpulimit", "cpuunits"];
    private static readonly string[] MetadataKeys = ["name", "hostname", "description", "onboot", "startup", "tags"];
    private static readonly string[] ForbiddenKeys = ["force", "skiplock", "lock", "args", "hookscript", "unprivileged", "features", "lxc", "delete"];
    private static readonly string[] NetworkModels = ["virtio", "e1000", "e1000e", "rtl8139", "vmxnet3"];

    public async Task<string> Update(string kind, int vmid, string? node, string? legacy, JsonElement? structured,
        string? expectedDigest, bool? dryRun, int waitSeconds, CancellationToken ct)
    {
        node = GuestManagement.Authorize(pve, kind, node, vmid, storage: false);
        var isLegacy = legacy is not null;
        if (isLegacy == structured.HasValue) throw new ArgumentException("Supply either keyValuePairs or fields, exclusively.");
        var preview = dryRun ?? !isLegacy;
        if (!preview) pve.EnsureWriteAllowed("Guest configuration");
        DiagnosticReads.Bound(waitSeconds, 10, 60, nameof(waitSeconds), 0);
        var fields = isLegacy ? ParseLegacy(legacy!) : ParseObject(structured!.Value);
        if (fields.Count > 64 || fields.Sum(p => p.Key.Length + p.Value.Length) > 131072) throw new ArgumentException("Configuration exceeds the supported field/size budget.");
        if (fields.Remove("digest", out var digest))
        {
            if (expectedDigest is not null && expectedDigest != digest) throw new ArgumentException("Conflicting expected digests.");
            expectedDigest = digest;
        }
        if (fields.Count == 0) throw new ArgumentException("At least one supported field is required.");
        var disks = fields.Keys.Where(k => IsDisk(k) || k == "delete").ToArray();
        if (disks.Length > 0)
        {
            if (!pve.Options.EnableStorage) throw new InvalidOperationException("Storage tools are disabled.");
            GuestManagement.WriteGate(pve, "Disk management", pve.Options.AllowDiskManagement, preview);
            if (fields.Count != 1) throw new InvalidOperationException("Disk configuration accepts one disk action per request; submit other changes separately with a fresh digest.");
            if (disks[0] == "delete") GuestManagement.Slot(kind, fields["delete"]);
        }
        var hasResources = fields.Keys.Any(ResourceKeys.Contains);
        if (hasResources) GuestManagement.WriteGate(pve, "Resource changes", pve.Options.AllowResourceChanges, preview);
        var extras = new Dictionary<string, object?>();
        foreach (var (key, value) in fields.Where(p => !ResourceKeys.Contains(p.Key) && !disks.Contains(p.Key)))
            extras.Add(key, ValidateField(kind, key, value));
        if (expectedDigest is null)
        {
            if (!isLegacy) throw new ArgumentException("Structured configuration requires expectedDigest.");
            expectedDigest = DiagnosticReads.Text((await GuestManagement.State(pve, node, kind, vmid, null, ct)).Config, "digest")
                ?? throw new InvalidOperationException("Current configuration digest is unavailable.");
        }
        GuestManagement.Digest(expectedDigest);
        string result;
        if (disks.Length > 0)
            result = await Disk(kind, vmid, node, disks[0], fields[disks[0]], expectedDigest, preview, waitSeconds, ct);
        else if (hasResources)
        {
            int? Integer(string key) => fields.TryGetValue(key, out var value) ? Int(value, key, int.MinValue, int.MaxValue) : null;
            double? limit = fields.TryGetValue("cpulimit", out var rawLimit) ? Number(rawLimit, "cpulimit", 0, 1024) : null;
            result = await new GuestConfigurationService(pve).UpdateResources(kind, vmid, expectedDigest, node,
                Integer("cores"), Integer("sockets"), Integer("vcpus"), Integer("memory"), Integer("balloon"), Integer("swap"),
                limit, Integer("cpuunits"), preview, waitSeconds, ct, extras);
        }
        else
        {
            var guest = await GuestManagement.State(pve, node, kind, vmid, expectedDigest, ct);
            if (extras.Keys.Any(k => GuestManagement.HasPending(guest.Pending, k))) throw new InvalidOperationException("Requested configuration has unresolved pending changes.");
            result = await new GuestConfigurationService(pve).Apply(kind, node, vmid, expectedDigest, guest, extras, preview, waitSeconds, null, [], ct);
        }
        if (!isLegacy) return result;
        using var report = JsonDocument.Parse(result);
        var operation = report.RootElement.GetProperty("operation");
        var task = operation.ValueKind == JsonValueKind.Object ? DiagnosticReads.Text(operation, "TaskId") : null;
        return JsonSerializer.Serialize(new { data = task, management = report.RootElement });
    }

    private async Task<string> Disk(string kind, int vmid, string node, string key, string value, string digest, bool dryRun, int waitSeconds, CancellationToken ct)
    {
        if (key == "delete")
            return await new DiskManagementService(pve).Execute(new(kind, vmid, "detach", value, digest, node, DryRun: dryRun, WaitSeconds: waitSeconds), ct);
        GuestManagement.Slot(kind, key);
        var properties = GuestManagement.Properties(value);
        string[] supported = kind == "qemu" ? ["volume", "file", "format", "cache", "discard", "ssd", "iothread"] : ["volume", "mp", "backup", "ro"];
        if (properties.Keys.Any(k => !supported.Contains(k))) throw new InvalidOperationException("Unsupported disk properties; size is observational and physical growth requires the resize tool.");
        var volume = properties.GetValueOrDefault("volume") ?? throw new ArgumentException("A disk allocation or owned unused volume is required.");
        var parts = volume.Split(':', 2);
        decimal? size = parts.Length == 2 && decimal.TryParse(parts[1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount) ? amount : null;
        string? unused = null;
        if (!size.HasValue)
        {
            if (kind != "qemu") throw new InvalidOperationException("LXC general configuration accepts managed allocation only; bind/device mounts are excluded.");
            if (properties.ContainsKey("format")) throw new ArgumentException("Attaching an existing volume cannot change its format.");
            var guest = await GuestManagement.State(pve, node, kind, vmid, digest, ct);
            var references = guest.Config.EnumerateObject().Where(p => Regex.IsMatch(p.Name, @"\Aunused[0-9]{1,3}\z") &&
                GuestManagement.Properties(p.Value.ToString()).GetValueOrDefault("volume") == volume).Select(p => p.Name).ToArray();
            if (references.Length != 1) throw new InvalidOperationException("Attachment requires exactly one discovered unused reference owned by this guest.");
            unused = references[0];
        }
        bool? Flag(string name) => properties.TryGetValue(name, out var flag) ? Bool(flag, name) == "1" : null;
        var discard = properties.GetValueOrDefault("discard") switch { null => (bool?)null, "on" => true, "ignore" => false, _ => throw new ArgumentException("Unsupported discard value.") };
        return await new DiskManagementService(pve).Execute(new(kind, vmid, size.HasValue ? "add" : "attach", key, digest, node,
            Storage: size.HasValue ? parts[0] : null, SizeGiB: size, Unused: unused, Format: properties.GetValueOrDefault("format"),
            Cache: properties.GetValueOrDefault("cache"), Discard: discard, Ssd: Flag("ssd"), IoThread: Flag("iothread"),
            MountPath: properties.GetValueOrDefault("mp"), Backup: Flag("backup"), ReadOnly: Flag("ro"), DryRun: dryRun, WaitSeconds: waitSeconds), ct);
    }

    private object ValidateField(string kind, string key, string value)
    {
        if (Regex.IsMatch(key, @"\Anet(?:[0-9]|[12][0-9]|3[01])\z"))
        {
            if (!pve.Options.EnableNetwork) throw new InvalidOperationException("Network configuration is disabled.");
            return Network(kind, value);
        }
        if (!MetadataKeys.Contains(key) || key == (kind == "qemu" ? "hostname" : "name")) throw new InvalidOperationException("Unsupported configuration field; arbitrary API parameters and privilege changes are excluded.");
        if (key is "name" or "hostname")
        {
            if (value.Length > 253 || value.Split('.').Any(label => label.Length > 63 || !Regex.IsMatch(label, @"\A[a-zA-Z0-9](?:[a-zA-Z0-9-]*[a-zA-Z0-9])?\z"))) throw new ArgumentException("Invalid guest name.");
        }
        else if (key == "description")
        {
            if (value.Length > 65535 || value.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'))) throw new ArgumentException("Invalid description.");
        }
        else if (key == "onboot") return Bool(value, key);
        else if (key == "tags")
        {
            if (value.Length > 4096 || (value.Length > 0 && !Regex.IsMatch(value, @"\A[a-zA-Z0-9_][a-zA-Z0-9_.+-]*(;[a-zA-Z0-9_][a-zA-Z0-9_.+-]*)*\z"))) throw new ArgumentException("Invalid tag list.");
        }
        else if (key == "startup")
        {
            var properties = GuestManagement.Properties(value);
            foreach (var p in properties)
            {
                if (p.Key is not ("order" or "up" or "down")) throw new ArgumentException("Unsupported startup property.");
                Int(p.Value, p.Key, 0, 86400);
            }
        }
        return value;
    }

    private static string Network(string kind, string value)
    {
        if (value.Length > 4096 || value.Any(char.IsControl)) throw new ArgumentException("Invalid network property string.");
        var properties = GuestManagement.Properties(value);
        var models = NetworkModels;
        if (kind == "qemu")
        {
            if (properties.Remove("volume", out var bareModel))
            {
                if (!properties.TryAdd("model", bareModel)) throw new ArgumentException("Duplicate network model.");
            }
            var aliases = properties.Keys.Where(models.Contains).ToArray();
            if (aliases.Length == 1)
            {
                if (properties.ContainsKey("model") || properties.ContainsKey("macaddr")) throw new ArgumentException("Duplicate network model/MAC.");
                properties["model"] = aliases[0];
                properties["macaddr"] = properties[aliases[0]];
                properties.Remove(aliases[0]);
            }
            if (!models.Contains(properties.GetValueOrDefault("model"))) throw new ArgumentException("An explicit supported network model is required.");
        }
        else if (!properties.ContainsKey("name")) throw new ArgumentException("An explicit LXC interface name is required.");
        foreach (var (key, val) in properties)
        {
            switch (key)
            {
                case "model" when kind == "qemu": break;
                case "macaddr" when kind == "qemu":
                case "hwaddr" when kind == "lxc":
                    if (!Regex.IsMatch(val, @"\A[0-9A-Fa-f]{2}(:[0-9A-Fa-f]{2}){5}\z")) throw new ArgumentException("Invalid MAC address.");
                    break;
                case "bridge":
                case "name" when kind == "lxc":
                    if (!Regex.IsMatch(val, @"\A[a-zA-Z][a-zA-Z0-9_.-]{0,14}\z")) throw new ArgumentException("Invalid interface/bridge name.");
                    break;
                case "firewall":
                case "link_down": Bool(val, key); break;
                case "tag": Int(val, key, 1, 4094); break;
                case "mtu": Int(val, key, kind == "lxc" ? 64 : 1, kind == "lxc" ? 65535 : 65520); break;
                case "trunks":
                    foreach (var vlan in val.Split(';')) Int(vlan, key, 1, 4094);
                    break;
                case "queues" when kind == "qemu": Int(val, key, 0, 64); break;
                case "rate": Number(val, key, 0, 1000000); break;
                case "type" when kind == "lxc" && val == "veth": break;
                case "ip" when kind == "lxc": Address(val, false, true); break;
                case "ip6" when kind == "lxc": Address(val, true, true); break;
                case "gw" when kind == "lxc": Address(val, false, false); break;
                case "gw6" when kind == "lxc": Address(val, true, false); break;
                default: throw new ArgumentException("Unsupported network property.");
            }
        }
        if (kind == "qemu" && (properties.ContainsKey("mtu") || properties.GetValueOrDefault("queues") is not (null or "0")) && properties["model"] != "virtio")
            throw new ArgumentException("MTU and multiple queues require VirtIO.");
        return string.Join(',', properties.Select(p => p.Key + "=" + p.Value));
    }

    internal static bool Equivalent(string key, string? current, string requested)
    {
        if (current is null) return false;
        if (key != "startup" && !Regex.IsMatch(key, @"\Anet[0-9]+\z")) return current == requested;
        try
        {
            Dictionary<string, string> Normalize(string value)
            {
                var fields = GuestManagement.Properties(value);
                if (key != "startup")
                {
                    if (fields.Remove("volume", out var model)) fields.Add("model", model);
                    foreach (var alias in NetworkModels)
                    {
                        if (!fields.Remove(alias, out var mac)) continue;
                        fields.Add("model", alias);
                        fields.Add("macaddr", mac);
                    }
                    foreach (var macKey in new[] { "macaddr", "hwaddr" })
                        if (fields.TryGetValue(macKey, out var mac)) fields[macKey] = mac.ToUpperInvariant();
                    foreach (var flag in new[] { "firewall", "link_down" })
                        if (fields.TryGetValue(flag, out var raw)) fields[flag] = Bool(raw, flag);
                }
                foreach (var number in new[] { "tag", "mtu", "queues", "rate", "order", "up", "down" })
                    if (fields.TryGetValue(number, out var raw) && decimal.TryParse(raw, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed))
                        fields[number] = parsed.ToString("G29", CultureInfo.InvariantCulture);
                return fields;
            }
            var actual = Normalize(current);
            return Normalize(requested).All(p => actual.GetValueOrDefault(p.Key) == p.Value);
        }
        catch (ArgumentException) { return false; }
    }

    private static void Address(string value, bool ipv6, bool cidr)
    {
        if (cidr && (value is "dhcp" or "manual" || ipv6 && value == "auto")) return;
        var parts = value.Split('/');
        if (value.Contains('%') || parts.Length != (cidr ? 2 : 1) || !IPAddress.TryParse(parts[0], out var ip) ||
            ip.AddressFamily != (ipv6 ? System.Net.Sockets.AddressFamily.InterNetworkV6 : System.Net.Sockets.AddressFamily.InterNetwork))
            throw new ArgumentException("Invalid network address or prefix.");
        if (cidr) Int(parts[1], "prefix", 0, ipv6 ? 128 : 32);
    }

    private static Dictionary<string, string> ParseObject(JsonElement fields)
    {
        if (fields.ValueKind != JsonValueKind.Object) throw new ArgumentException("fields must be a JSON object.");
        var result = new Dictionary<string, string>();
        foreach (var p in fields.EnumerateObject())
        {
            if (p.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False) ||
                !result.TryAdd(p.Name, p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText()))
                throw new ArgumentException("Fields must be unique scalar values; null, arrays and nested objects are excluded.");
        }
        return result;
    }

    private static Dictionary<string, string> ParseLegacy(string value)
    {
        if (value.Length > 131072) throw new ArgumentException("Configuration request is too large.");
        var result = new Dictionary<string, string>();
        string? current = null;
        foreach (var token in value.Split(','))
        {
            var pair = token.Split('=', 2);
            var key = pair[0].Trim();
            if (pair.Length == 2 && (current is null || ResourceKeys.Contains(key) || MetadataKeys.Contains(key) || ForbiddenKeys.Contains(key) ||
                key == "digest" || IsDisk(key) || Regex.IsMatch(key, @"\Anet[0-9]+\z")))
            {
                if (!result.TryAdd(key, pair[1])) throw new ArgumentException("Duplicate configuration field.");
                current = key;
            }
            else if (current is not null) result[current] += "," + token;
            else throw new ArgumentException("Expected key=value configuration fields.");
        }
        return result;
    }

    private static bool IsDisk(string key) => Regex.IsMatch(key, @"\A(?:(?:ide|sata|scsi|virtio|mp|unused)[0-9]+|rootfs|efidisk0|tpmstate0)\z");
    private static string Bool(string value, string name) => value.ToLowerInvariant() switch { "1" or "true" => "1", "0" or "false" => "0", _ => throw new ArgumentException("Invalid boolean for " + name) };
    private static int Int(string value, string name, int minimum, int maximum) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= minimum && n <= maximum ? n : throw new ArgumentException("Invalid integer for " + name);
    private static double Number(string value, string name, double minimum, double maximum) =>
        double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) && n >= minimum && n <= maximum ? n : throw new ArgumentException("Invalid number for " + name);
}
