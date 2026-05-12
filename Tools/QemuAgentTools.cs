using System.ComponentModel;
using System.Text;
using ProxmoxMCPSharp.Services;
using ModelContextProtocol.Server;

namespace ProxmoxMCPSharp.Tools;

[McpServerToolType]
public static class QemuAgentTools
{
    [McpServerTool(Name = "qemu_agent_ping"),
     Description("Ping the QEMU guest agent. Confirms agent is installed, running and responsive.")]
    public static async Task<string> Ping(
        ProxmoxClient pve,
        [Description("VMID of the QEMU VM.")] int vmid,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        EnsureEnabled(pve);
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.PostAsync($"nodes/{n}/qemu/{vmid}/agent/ping", null, ct));
    }

    [McpServerTool(Name = "qemu_agent_get_osinfo"),
     Description("Get OS info reported by the QEMU guest agent (distro, kernel, version).")]
    public static async Task<string> GetOsInfo(
        ProxmoxClient pve,
        [Description("VMID of the QEMU VM.")] int vmid,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        EnsureEnabled(pve);
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.GetAsync($"nodes/{n}/qemu/{vmid}/agent/get-osinfo", ct));
    }

    [McpServerTool(Name = "qemu_agent_get_network_interfaces"),
     Description("Get the guest's network interfaces and IP addresses via the QEMU guest agent.")]
    public static async Task<string> GetNetworkInterfaces(
        ProxmoxClient pve,
        [Description("VMID of the QEMU VM.")] int vmid,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        EnsureEnabled(pve);
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.GetAsync($"nodes/{n}/qemu/{vmid}/agent/network-get-interfaces", ct));
    }

    [McpServerTool(Name = "qemu_agent_get_users"),
     Description("Get the list of logged-in users from inside the guest via the QEMU guest agent.")]
    public static async Task<string> GetUsers(
        ProxmoxClient pve,
        [Description("VMID of the QEMU VM.")] int vmid,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        EnsureEnabled(pve);
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.GetAsync($"nodes/{n}/qemu/{vmid}/agent/get-users", ct));
    }

    [McpServerTool(Name = "qemu_agent_get_fsinfo"),
     Description("Get mounted filesystem info from inside the guest via the QEMU guest agent.")]
    public static async Task<string> GetFsInfo(
        ProxmoxClient pve,
        [Description("VMID of the QEMU VM.")] int vmid,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        EnsureEnabled(pve);
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.GetAsync($"nodes/{n}/qemu/{vmid}/agent/get-fsinfo", ct));
    }

    [McpServerTool(Name = "qemu_agent_get_host_name"),
     Description("Get the in-guest hostname via the QEMU guest agent.")]
    public static async Task<string> GetHostName(
        ProxmoxClient pve,
        [Description("VMID of the QEMU VM.")] int vmid,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        EnsureEnabled(pve);
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.GetAsync($"nodes/{n}/qemu/{vmid}/agent/get-host-name", ct));
    }

    [McpServerTool(Name = "qemu_agent_get_timezone"),
     Description("Get the in-guest timezone via the QEMU guest agent.")]
    public static async Task<string> GetTimezone(
        ProxmoxClient pve,
        [Description("VMID of the QEMU VM.")] int vmid,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        EnsureEnabled(pve);
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.GetAsync($"nodes/{n}/qemu/{vmid}/agent/get-timezone", ct));
    }

    [McpServerTool(Name = "qemu_agent_fstrim"),
     Description("Run fstrim inside the guest via the QEMU guest agent. Requires ReadOnly=false.")]
    public static async Task<string> FsTrim(
        ProxmoxClient pve,
        [Description("VMID of the QEMU VM.")] int vmid,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        EnsureEnabled(pve);
        pve.EnsureWriteAllowed(nameof(FsTrim));
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.PostAsync($"nodes/{n}/qemu/{vmid}/agent/fstrim", null, ct));
    }

    [McpServerTool(Name = "qemu_agent_fsfreeze"),
     Description("Freeze guest filesystems via the QEMU guest agent. Requires ReadOnly=false.")]
    public static async Task<string> FsFreeze(
        ProxmoxClient pve,
        [Description("VMID of the QEMU VM.")] int vmid,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        EnsureEnabled(pve);
        pve.EnsureWriteAllowed(nameof(FsFreeze));
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.PostAsync($"nodes/{n}/qemu/{vmid}/agent/fsfreeze-freeze", null, ct));
    }

    [McpServerTool(Name = "qemu_agent_fsthaw"),
     Description("Thaw frozen guest filesystems via the QEMU guest agent. Requires ReadOnly=false.")]
    public static async Task<string> FsThaw(
        ProxmoxClient pve,
        [Description("VMID of the QEMU VM.")] int vmid,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        EnsureEnabled(pve);
        pve.EnsureWriteAllowed(nameof(FsThaw));
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.PostAsync($"nodes/{n}/qemu/{vmid}/agent/fsfreeze-thaw", null, ct));
    }

    [McpServerTool(Name = "qemu_agent_shutdown"),
     Description("Request an in-guest shutdown via the QEMU guest agent (cleaner than ACPI). Requires ReadOnly=false.")]
    public static async Task<string> Shutdown(
        ProxmoxClient pve,
        [Description("VMID of the QEMU VM.")] int vmid,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        EnsureEnabled(pve);
        pve.EnsureWriteAllowed(nameof(Shutdown));
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.PostAsync($"nodes/{n}/qemu/{vmid}/agent/shutdown", null, ct));
    }

    [McpServerTool(Name = "qemu_agent_reboot"),
     Description("Request an in-guest reboot via the QEMU guest agent. Requires ReadOnly=false.")]
    public static async Task<string> Reboot(
        ProxmoxClient pve,
        [Description("VMID of the QEMU VM.")] int vmid,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        EnsureEnabled(pve);
        pve.EnsureWriteAllowed(nameof(Reboot));
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.PostAsync($"nodes/{n}/qemu/{vmid}/agent/reboot", null, ct));
    }

    [McpServerTool(Name = "qemu_agent_exec"),
     Description("Execute a command inside the guest via the QEMU guest agent. Returns a pid; poll qemu_agent_exec_status to read output. Requires ReadOnly=false and AllowGuestExec=true.")]
    public static async Task<string> Exec(
        ProxmoxClient pve,
        [Description("VMID of the QEMU VM.")] int vmid,
        [Description("Command to run, with arguments separated by spaces (or a JSON array if the API accepts).")] string command,
        [Description("Optional stdin to feed to the process.")] string? input,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        EnsureEnabled(pve);
        pve.EnsureWriteAllowed(nameof(Exec));
        if (!pve.Options.AllowGuestExec)
            throw new InvalidOperationException("Guest exec is disabled. Set Proxmox:AllowGuestExec=true to enable.");
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);
        var parameters = new Dictionary<string, object?>
        {
            ["command"] = command,
        };
        if (!string.IsNullOrEmpty(input)) parameters["input-data"] = input;
        return JsonOpts.Pass(await pve.PostAsync($"nodes/{n}/qemu/{vmid}/agent/exec", parameters, ct));
    }

    [McpServerTool(Name = "qemu_agent_exec_status"),
     Description("Get the status and captured output of a previously started guest agent exec command.")]
    public static async Task<string> ExecStatus(
        ProxmoxClient pve,
        [Description("VMID of the QEMU VM.")] int vmid,
        [Description("PID returned by qemu_agent_exec.")] int pid,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        EnsureEnabled(pve);
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.GetAsync($"nodes/{n}/qemu/{vmid}/agent/exec-status?pid={pid}", ct));
    }

    [McpServerTool(Name = "qemu_agent_file_read"),
     Description("Read a small file from inside the guest via the QEMU guest agent.")]
    public static async Task<string> FileRead(
        ProxmoxClient pve,
        [Description("VMID of the QEMU VM.")] int vmid,
        [Description("Absolute path inside the guest.")] string file,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        EnsureEnabled(pve);
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);
        return JsonOpts.Pass(await pve.GetAsync($"nodes/{n}/qemu/{vmid}/agent/file-read?file={Uri.EscapeDataString(file)}", ct));
    }

    [McpServerTool(Name = "qemu_agent_file_write"),
     Description("Write a file inside the guest via the QEMU guest agent. Requires ReadOnly=false and AllowGuestFileWrite=true.")]
    public static async Task<string> FileWrite(
        ProxmoxClient pve,
        [Description("VMID of the QEMU VM.")] int vmid,
        [Description("Absolute path inside the guest.")] string file,
        [Description("File content. Plain text; will be sent as the 'content' parameter.")] string content,
        [Description("If true, encode content as base64 and pass encode=1.")] bool? base64,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        EnsureEnabled(pve);
        pve.EnsureWriteAllowed(nameof(FileWrite));
        if (!pve.Options.AllowGuestFileWrite)
            throw new InvalidOperationException("Guest file write is disabled. Set Proxmox:AllowGuestFileWrite=true to enable.");
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);
        var parameters = new Dictionary<string, object?>
        {
            ["file"] = file,
            ["content"] = base64 == true ? Convert.ToBase64String(Encoding.UTF8.GetBytes(content)) : content,
        };
        if (base64 == true) parameters["encode"] = true;
        return JsonOpts.Pass(await pve.PostAsync($"nodes/{n}/qemu/{vmid}/agent/file-write", parameters, ct));
    }

    [McpServerTool(Name = "qemu_agent_set_user_password"),
     Description("Set the password of an in-guest user via the QEMU guest agent. Requires ReadOnly=false and AllowGuestPasswordChange=true.")]
    public static async Task<string> SetUserPassword(
        ProxmoxClient pve,
        [Description("VMID of the QEMU VM.")] int vmid,
        [Description("In-guest username.")] string username,
        [Description("New password.")] string password,
        [Description("If true, the password is already crypted (encoded as a hash the OS expects).")] bool? crypted,
        [Description("Node name. Falls back to Proxmox:DefaultNode.")] string? node,
        CancellationToken ct = default)
    {
        EnsureEnabled(pve);
        pve.EnsureWriteAllowed(nameof(SetUserPassword));
        if (!pve.Options.AllowGuestPasswordChange)
            throw new InvalidOperationException("Guest password change is disabled. Set Proxmox:AllowGuestPasswordChange=true to enable.");
        pve.EnsureVmAllowed(vmid);
        var n = pve.ResolveNode(node);
        var parameters = new Dictionary<string, object?>
        {
            ["username"] = username,
            ["password"] = password,
        };
        if (crypted is not null) parameters["crypted"] = crypted;
        return JsonOpts.Pass(await pve.PostAsync($"nodes/{n}/qemu/{vmid}/agent/set-user-password", parameters, ct));
    }

    private static void EnsureEnabled(ProxmoxClient pve)
    {
        if (!pve.Options.EnableQemuAgent)
            throw new InvalidOperationException("QEMU guest agent tools are disabled.");
    }
}
