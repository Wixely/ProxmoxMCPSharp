using System.ComponentModel;
using ProxmoxMCPSharp.Services;
using ModelContextProtocol.Server;

namespace ProxmoxMCPSharp.Tools;

[McpServerToolType]
public static class QemuTools
{
    [McpServerTool(Name = "list_qemu_vms"),
     Description("List QEMU virtual machines on a node.")]
    public static async Task<string> ListVms(
        ProxmoxClient pve,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        if (!pve.Options.EnableQemu) throw new InvalidOperationException("QEMU tools are disabled.");
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.GetAsync($"nodes/{n}/qemu", ct));
    }

    [McpServerTool(Name = "get_qemu_status"),
     Description("Get current status (cpu, mem, uptime, qmpstatus) of a QEMU VM.")]
    public static async Task<string> GetStatus(
        ProxmoxClient pve,
        [Description("VMID of the QEMU VM.")] int vmid,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        if (!pve.Options.EnableQemu) throw new InvalidOperationException("QEMU tools are disabled.");
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.GetAsync($"nodes/{n}/qemu/{vmid}/status/current", ct));
    }

    [McpServerTool(Name = "get_qemu_config"),
     Description("Get the full configuration of a QEMU VM.")]
    public static async Task<string> GetConfig(
        ProxmoxClient pve,
        [Description("VMID of the QEMU VM.")] int vmid,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        [Description("If true return the pending config (with unapplied changes).")] bool? pending,
        CancellationToken ct = default)
    {
        if (!pve.Options.EnableQemu) throw new InvalidOperationException("QEMU tools are disabled.");
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);
        var path = $"nodes/{n}/qemu/{vmid}/config";
        if (pending == true) path += "?current=0";
        return JsonOpts.Pass(await pve.GetAsync(path, ct));
    }

    [McpServerTool(Name = "get_qemu_rrd"),
     Description("Get RRD performance data for a QEMU VM.")]
    public static async Task<string> GetRrd(
        ProxmoxClient pve,
        [Description("VMID of the QEMU VM.")] int vmid,
        [Description("Timeframe: hour, day, week, month or year.")] string timeframe,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        if (!pve.Options.EnableQemu) throw new InvalidOperationException("QEMU tools are disabled.");
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.GetAsync(
            $"nodes/{n}/qemu/{vmid}/rrddata?timeframe={Uri.EscapeDataString(timeframe)}", ct));
    }

    [McpServerTool(Name = "start_qemu"),
     Description("Start a QEMU VM. Requires ReadOnly=false.")]
    public static async Task<string> Start(
        ProxmoxClient pve,
        [Description("VMID of the QEMU VM.")] int vmid,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        if (!pve.Options.EnableQemu) throw new InvalidOperationException("QEMU tools are disabled.");
        pve.EnsureWriteAllowed(nameof(Start));
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.PostAsync($"nodes/{n}/qemu/{vmid}/status/start", null, ct));
    }

    [McpServerTool(Name = "stop_qemu"),
     Description("Hard-stop (power off) a QEMU VM. Requires ReadOnly=false.")]
    public static async Task<string> Stop(
        ProxmoxClient pve,
        [Description("VMID of the QEMU VM.")] int vmid,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        if (!pve.Options.EnableQemu) throw new InvalidOperationException("QEMU tools are disabled.");
        pve.EnsureWriteAllowed(nameof(Stop));
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.PostAsync($"nodes/{n}/qemu/{vmid}/status/stop", null, ct));
    }

    [McpServerTool(Name = "shutdown_qemu"),
     Description("Graceful ACPI shutdown of a QEMU VM. Requires ReadOnly=false.")]
    public static async Task<string> Shutdown(
        ProxmoxClient pve,
        [Description("VMID of the QEMU VM.")] int vmid,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        [Description("Timeout in seconds before forcing shutdown.")] int? timeout,
        [Description("If true, forces a hard stop after the timeout.")] bool? forceStop,
        CancellationToken ct = default)
    {
        if (!pve.Options.EnableQemu) throw new InvalidOperationException("QEMU tools are disabled.");
        pve.EnsureWriteAllowed(nameof(Shutdown));
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);
        var parameters = new Dictionary<string, object?>();
        if (timeout is not null) parameters["timeout"] = timeout;
        if (forceStop is not null) parameters["forceStop"] = forceStop;
        return JsonOpts.Pass(await pve.PostAsync($"nodes/{n}/qemu/{vmid}/status/shutdown", parameters, ct));
    }

    [McpServerTool(Name = "reboot_qemu"),
     Description("Reboot a QEMU VM. Requires ReadOnly=false.")]
    public static async Task<string> Reboot(
        ProxmoxClient pve,
        [Description("VMID of the QEMU VM.")] int vmid,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        if (!pve.Options.EnableQemu) throw new InvalidOperationException("QEMU tools are disabled.");
        pve.EnsureWriteAllowed(nameof(Reboot));
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.PostAsync($"nodes/{n}/qemu/{vmid}/status/reboot", null, ct));
    }

    [McpServerTool(Name = "reset_qemu"),
     Description("Hardware reset a QEMU VM. Requires ReadOnly=false.")]
    public static async Task<string> Reset(
        ProxmoxClient pve,
        [Description("VMID of the QEMU VM.")] int vmid,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        if (!pve.Options.EnableQemu) throw new InvalidOperationException("QEMU tools are disabled.");
        pve.EnsureWriteAllowed(nameof(Reset));
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.PostAsync($"nodes/{n}/qemu/{vmid}/status/reset", null, ct));
    }

    [McpServerTool(Name = "suspend_qemu"),
     Description("Suspend a QEMU VM. Requires ReadOnly=false.")]
    public static async Task<string> Suspend(
        ProxmoxClient pve,
        [Description("VMID of the QEMU VM.")] int vmid,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        [Description("If true the VM state is written to disk (hibernate).")] bool? toDisk,
        CancellationToken ct = default)
    {
        if (!pve.Options.EnableQemu) throw new InvalidOperationException("QEMU tools are disabled.");
        pve.EnsureWriteAllowed(nameof(Suspend));
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);
        var parameters = new Dictionary<string, object?>();
        if (toDisk is not null) parameters["todisk"] = toDisk;
        return JsonOpts.Pass(await pve.PostAsync($"nodes/{n}/qemu/{vmid}/status/suspend", parameters, ct));
    }

    [McpServerTool(Name = "resume_qemu"),
     Description("Resume a suspended QEMU VM. Requires ReadOnly=false.")]
    public static async Task<string> Resume(
        ProxmoxClient pve,
        [Description("VMID of the QEMU VM.")] int vmid,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        if (!pve.Options.EnableQemu) throw new InvalidOperationException("QEMU tools are disabled.");
        pve.EnsureWriteAllowed(nameof(Resume));
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.PostAsync($"nodes/{n}/qemu/{vmid}/status/resume", null, ct));
    }

    [McpServerTool(Name = "create_qemu_vm"),
     Description("Create a new QEMU VM. Most parameters mirror the Proxmox POST /nodes/{node}/qemu API. Requires ReadOnly=false.")]
    public static async Task<string> Create(
        ProxmoxClient pve,
        [Description("VMID for the new VM. Use get_next_vmid to find a free id.")] int vmid,
        [Description("Display name for the VM.")] string name,
        [Description("Number of CPU cores. Defaults to 2.")] int? cores,
        [Description("Memory in MiB. Defaults to 2048.")] int? memoryMib,
        [Description("Boot disk spec, e.g. 'storage:32' to create a 32 GiB disk on a storage. Falls back to DefaultStorage.")] string? scsi0,
        [Description("Network device spec, e.g. 'virtio,bridge=vmbr0'. Defaults to DefaultBridge.")] string? net0,
        [Description("OS type. Defaults to Proxmox:DefaultOsType (l26).")] string? ostype,
        [Description("ISO image to attach to ide2, e.g. 'local:iso/debian.iso'.")] string? iso,
        [Description("If true, start the VM immediately after creation.")] bool? start,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        if (!pve.Options.EnableQemu) throw new InvalidOperationException("QEMU tools are disabled.");
        pve.EnsureWriteAllowed(nameof(Create));
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);

        var disk = scsi0;
        if (string.IsNullOrWhiteSpace(disk) && !string.IsNullOrWhiteSpace(pve.Options.DefaultStorage))
            disk = $"{pve.Options.DefaultStorage}:32";

        var net = string.IsNullOrWhiteSpace(net0) ? $"virtio,bridge={pve.Options.DefaultBridge}" : net0;

        var parameters = new Dictionary<string, object?>
        {
            ["vmid"] = vmid,
            ["name"] = name,
            ["cores"] = cores ?? 2,
            ["memory"] = memoryMib ?? 2048,
            ["ostype"] = string.IsNullOrWhiteSpace(ostype) ? pve.Options.DefaultOsType : ostype,
            ["net0"] = net,
            ["scsihw"] = "virtio-scsi-single",
        };
        if (!string.IsNullOrWhiteSpace(disk)) parameters["scsi0"] = disk;
        if (!string.IsNullOrWhiteSpace(iso)) parameters["ide2"] = $"{iso},media=cdrom";
        if (start is not null) parameters["start"] = start;

        return JsonOpts.Pass(await pve.PostAsync($"nodes/{n}/qemu", parameters, ct));
    }

    [McpServerTool(Name = "update_qemu_config"),
     Description("Update a QEMU VM's configuration. Pass a comma-separated 'key=value' pair list. Requires ReadOnly=false.")]
    public static async Task<string> UpdateConfig(
        ProxmoxClient pve,
        [Description("VMID of the QEMU VM.")] int vmid,
        [Description("Comma-separated key=value parameters as accepted by PUT /nodes/{node}/qemu/{vmid}/config.")] string keyValuePairs,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        if (!pve.Options.EnableQemu) throw new InvalidOperationException("QEMU tools are disabled.");
        pve.EnsureWriteAllowed(nameof(UpdateConfig));
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);
        var parameters = ParseKvList(keyValuePairs);
        return JsonOpts.Pass(await pve.PutAsync($"nodes/{n}/qemu/{vmid}/config", parameters, ct));
    }

    [McpServerTool(Name = "clone_qemu"),
     Description("Clone a QEMU VM. Requires ReadOnly=false.")]
    public static async Task<string> Clone(
        ProxmoxClient pve,
        [Description("Source VMID.")] int vmid,
        [Description("New VMID for the clone.")] int newVmid,
        [Description("Optional name for the clone.")] string? name,
        [Description("If true, do a full clone (independent disks).")] bool? full,
        [Description("Target storage for the clone disks.")] string? targetStorage,
        [Description("Target node for the clone (must share storage with source).")] string? targetNode,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        if (!pve.Options.EnableQemu) throw new InvalidOperationException("QEMU tools are disabled.");
        pve.EnsureWriteAllowed(nameof(Clone));
        pve.EnsureVmAllowed(vmid);
        pve.EnsureVmAllowed(newVmid);
        var n = pve.ResolveNode(node);
        var parameters = new Dictionary<string, object?> { ["newid"] = newVmid };
        if (!string.IsNullOrWhiteSpace(name)) parameters["name"] = name;
        if (full is not null) parameters["full"] = full;
        if (!string.IsNullOrWhiteSpace(targetStorage)) parameters["storage"] = targetStorage;
        if (!string.IsNullOrWhiteSpace(targetNode)) parameters["target"] = targetNode;
        return JsonOpts.Pass(await pve.PostAsync($"nodes/{n}/qemu/{vmid}/clone", parameters, ct));
    }

    [McpServerTool(Name = "migrate_qemu"),
     Description("Migrate a QEMU VM to another node. Requires ReadOnly=false.")]
    public static async Task<string> Migrate(
        ProxmoxClient pve,
        [Description("VMID of the QEMU VM.")] int vmid,
        [Description("Target node name.")] string target,
        [Description("If true, use online (live) migration.")] bool? online,
        [Description("Source node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        if (!pve.Options.EnableQemu) throw new InvalidOperationException("QEMU tools are disabled.");
        pve.EnsureWriteAllowed(nameof(Migrate));
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);
        var parameters = new Dictionary<string, object?> { ["target"] = target };
        if (online is not null) parameters["online"] = online;
        return JsonOpts.Pass(await pve.PostAsync($"nodes/{n}/qemu/{vmid}/migrate", parameters, ct));
    }

    [McpServerTool(Name = "delete_qemu_vm"),
     Description("Permanently destroy a QEMU VM. Requires ReadOnly=false and AllowDestroy=true.")]
    public static async Task<string> Delete(
        ProxmoxClient pve,
        [Description("VMID of the QEMU VM.")] int vmid,
        [Description("If true, also purge from jobs/replication/HA config.")] bool? purge,
        [Description("If true, also destroy unreferenced disks owned by the VM.")] bool? destroyUnreferencedDisks,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        if (!pve.Options.EnableQemu) throw new InvalidOperationException("QEMU tools are disabled.");
        pve.EnsureDestroyAllowed(nameof(Delete));
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);
        var parameters = new Dictionary<string, object?>();
        if (purge is not null) parameters["purge"] = purge;
        if (destroyUnreferencedDisks is not null) parameters["destroy-unreferenced-disks"] = destroyUnreferencedDisks;
        return JsonOpts.Pass(await pve.DeleteAsync($"nodes/{n}/qemu/{vmid}", parameters, ct));
    }

    private static Dictionary<string, object?> ParseKvList(string raw)
    {
        var dict = new Dictionary<string, object?>();
        if (string.IsNullOrWhiteSpace(raw)) return dict;
        foreach (var pair in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var idx = pair.IndexOf('=');
            if (idx <= 0) continue;
            dict[pair[..idx].Trim()] = pair[(idx + 1)..].Trim();
        }
        return dict;
    }
}
