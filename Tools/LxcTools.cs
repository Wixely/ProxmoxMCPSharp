using System.ComponentModel;
using ProxmoxMCPSharp.Services;
using ModelContextProtocol.Server;

namespace ProxmoxMCPSharp.Tools;

[McpServerToolType]
public static class LxcTools
{
    [McpServerTool(Name = "list_lxc_containers"),
     Description("List LXC containers on a node.")]
    public static async Task<string> ListContainers(
        ProxmoxClient pve,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        if (!pve.Options.EnableLxc) throw new InvalidOperationException("LXC tools are disabled.");
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.GetAsync($"nodes/{n}/lxc", ct));
    }

    [McpServerTool(Name = "get_lxc_status"),
     Description("Get current status of an LXC container.")]
    public static async Task<string> GetStatus(
        ProxmoxClient pve,
        [Description("CTID of the container.")] int vmid,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        if (!pve.Options.EnableLxc) throw new InvalidOperationException("LXC tools are disabled.");
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.GetAsync($"nodes/{n}/lxc/{vmid}/status/current", ct));
    }

    [McpServerTool(Name = "get_lxc_config"),
     Description("Get the full configuration of an LXC container.")]
    public static async Task<string> GetConfig(
        ProxmoxClient pve,
        [Description("CTID of the container.")] int vmid,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        if (!pve.Options.EnableLxc) throw new InvalidOperationException("LXC tools are disabled.");
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.GetAsync($"nodes/{n}/lxc/{vmid}/config", ct));
    }

    [McpServerTool(Name = "start_lxc"),
     Description("Start an LXC container. Requires ReadOnly=false.")]
    public static async Task<string> Start(
        ProxmoxClient pve,
        [Description("CTID of the container.")] int vmid,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        if (!pve.Options.EnableLxc) throw new InvalidOperationException("LXC tools are disabled.");
        pve.EnsureWriteAllowed(nameof(Start));
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.PostAsync($"nodes/{n}/lxc/{vmid}/status/start", null, ct));
    }

    [McpServerTool(Name = "stop_lxc"),
     Description("Hard-stop an LXC container. Requires ReadOnly=false.")]
    public static async Task<string> Stop(
        ProxmoxClient pve,
        [Description("CTID of the container.")] int vmid,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        if (!pve.Options.EnableLxc) throw new InvalidOperationException("LXC tools are disabled.");
        pve.EnsureWriteAllowed(nameof(Stop));
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.PostAsync($"nodes/{n}/lxc/{vmid}/status/stop", null, ct));
    }

    [McpServerTool(Name = "shutdown_lxc"),
     Description("Graceful shutdown of an LXC container. Requires ReadOnly=false.")]
    public static async Task<string> Shutdown(
        ProxmoxClient pve,
        [Description("CTID of the container.")] int vmid,
        [Description("Timeout in seconds.")] int? timeout,
        [Description("Force-stop after timeout.")] bool? forceStop,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        if (!pve.Options.EnableLxc) throw new InvalidOperationException("LXC tools are disabled.");
        pve.EnsureWriteAllowed(nameof(Shutdown));
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);
        var parameters = new Dictionary<string, object?>();
        if (timeout is not null) parameters["timeout"] = timeout;
        if (forceStop is not null) parameters["forceStop"] = forceStop;
        return JsonOpts.Pass(await pve.PostAsync($"nodes/{n}/lxc/{vmid}/status/shutdown", parameters, ct));
    }

    [McpServerTool(Name = "reboot_lxc"),
     Description("Reboot an LXC container. Requires ReadOnly=false.")]
    public static async Task<string> Reboot(
        ProxmoxClient pve,
        [Description("CTID of the container.")] int vmid,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        if (!pve.Options.EnableLxc) throw new InvalidOperationException("LXC tools are disabled.");
        pve.EnsureWriteAllowed(nameof(Reboot));
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.PostAsync($"nodes/{n}/lxc/{vmid}/status/reboot", null, ct));
    }

    [McpServerTool(Name = "create_lxc_container"),
     Description("Create a new LXC container. Requires ReadOnly=false.")]
    public static async Task<string> Create(
        ProxmoxClient pve,
        [Description("CTID for the new container.")] int vmid,
        [Description("Hostname for the container.")] string hostname,
        [Description("Template volume, e.g. 'local:vztmpl/debian-12-standard_12.2-1_amd64.tar.zst'.")] string ostemplate,
        [Description("Root filesystem spec, e.g. 'storage:8' for an 8 GiB volume. Falls back to DefaultStorage:8.")] string? rootfs,
        [Description("Memory in MiB. Defaults to 1024.")] int? memoryMib,
        [Description("Swap in MiB. Defaults to 512.")] int? swapMib,
        [Description("Number of CPU cores. Defaults to 1.")] int? cores,
        [Description("Network device spec, e.g. 'name=eth0,bridge=vmbr0,ip=dhcp'.")] string? net0,
        [Description("If true, container is unprivileged.")] bool? unprivileged,
        [Description("Plain-text root password (alternative to ssh-public-keys).")] string? password,
        [Description("SSH public keys, newline-separated.")] string? sshPublicKeys,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        if (!pve.Options.EnableLxc) throw new InvalidOperationException("LXC tools are disabled.");
        pve.EnsureWriteAllowed(nameof(Create));
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);

        var rfs = rootfs;
        if (string.IsNullOrWhiteSpace(rfs) && !string.IsNullOrWhiteSpace(pve.Options.DefaultStorage))
            rfs = $"{pve.Options.DefaultStorage}:8";

        var parameters = new Dictionary<string, object?>
        {
            ["vmid"] = vmid,
            ["hostname"] = hostname,
            ["ostemplate"] = ostemplate,
            ["memory"] = memoryMib ?? 1024,
            ["swap"] = swapMib ?? 512,
            ["cores"] = cores ?? 1,
            ["unprivileged"] = unprivileged ?? true,
            ["net0"] = string.IsNullOrWhiteSpace(net0) ? $"name=eth0,bridge={pve.Options.DefaultBridge},ip=dhcp" : net0,
        };
        if (!string.IsNullOrWhiteSpace(rfs)) parameters["rootfs"] = rfs;
        if (!string.IsNullOrWhiteSpace(password)) parameters["password"] = password;
        if (!string.IsNullOrWhiteSpace(sshPublicKeys)) parameters["ssh-public-keys"] = sshPublicKeys;

        return JsonOpts.Pass(await pve.PostAsync($"nodes/{n}/lxc", parameters, ct));
    }

    [McpServerTool(Name = "delete_lxc_container"),
     Description("Permanently destroy an LXC container. Requires ReadOnly=false and AllowDestroy=true.")]
    public static async Task<string> Delete(
        ProxmoxClient pve,
        [Description("CTID of the container.")] int vmid,
        [Description("If true, also purge from jobs/replication/HA config.")] bool? purge,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        if (!pve.Options.EnableLxc) throw new InvalidOperationException("LXC tools are disabled.");
        pve.EnsureDestroyAllowed(nameof(Delete));
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);
        var parameters = new Dictionary<string, object?>();
        if (purge is not null) parameters["purge"] = purge;
        return JsonOpts.Pass(await pve.DeleteAsync($"nodes/{n}/lxc/{vmid}", parameters, ct));
    }
}
