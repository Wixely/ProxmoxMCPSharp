namespace ProxmoxMCPSharp.Configuration;

/// <summary>
/// Configuration for connecting to a Proxmox VE cluster via the PVE2 HTTPS API.
/// </summary>
public sealed class ProxmoxOptions
{
    public const string SectionName = "Proxmox";

    /// <summary>Base URL for the Proxmox API. e.g. https://pve.example.com:8006/</summary>
    public string BaseUrl { get; set; } = "https://localhost:8006/";

    /// <summary>API token id in the form "user@realm!tokenid". Preferred over username/password.</summary>
    public string? ApiTokenId { get; set; }

    /// <summary>API token secret (UUID). Used together with ApiTokenId.</summary>
    public string? ApiTokenSecret { get; set; }

    /// <summary>Username including realm (e.g. "root@pam"). Used only when no API token is configured.</summary>
    public string? Username { get; set; }

    /// <summary>Password for ticket-based authentication. Used only when no API token is configured.</summary>
    public string? Password { get; set; }

    /// <summary>One-time TOTP code, when ticket auth requires TFA. Leave empty otherwise.</summary>
    public string? TotpCode { get; set; }

    /// <summary>When true, skip TLS certificate validation. Useful for clusters using the default self-signed cert.</summary>
    public bool IgnoreCertificateErrors { get; set; } = false;

    /// <summary>Optional path to a PEM file containing the cluster's CA certificate, when not ignoring validation.</summary>
    public string? CaCertificatePath { get; set; }

    /// <summary>HTTP request timeout in seconds.</summary>
    public int RequestTimeoutSeconds { get; set; } = 60;

    /// <summary>User-Agent header sent to the Proxmox API.</summary>
    public string UserAgent { get; set; } = "ProxmoxMCPSharp";

    /// <summary>Default node name used when tools are called without one. Optional.</summary>
    public string? DefaultNode { get; set; }

    /// <summary>When true, ALL write/delete/start/stop/configure tools are blocked. Default true.</summary>
    public bool ReadOnly { get; set; } = true;

    /// <summary>Optional allow-list of node names. Empty = no restriction.</summary>
    public List<string> AllowedNodes { get; set; } = new();

    /// <summary>Optional deny-list of node names. Evaluated after AllowedNodes.</summary>
    public List<string> BlockedNodes { get; set; } = new();

    /// <summary>Optional allow-list of VMIDs (qemu or lxc). Empty = no restriction.</summary>
    public List<int> AllowedVmIds { get; set; } = new();

    /// <summary>Optional deny-list of VMIDs that must never be acted upon (write/destroy).</summary>
    public List<int> BlockedVmIds { get; set; } = new();

    /// <summary>If true, expose tools that read node statistics, status and diagnostics.</summary>
    public bool EnableNodes { get; set; } = true;

    /// <summary>If true, expose tools that manage QEMU virtual machines.</summary>
    public bool EnableQemu { get; set; } = true;

    /// <summary>If true, expose tools that manage LXC containers.</summary>
    public bool EnableLxc { get; set; } = true;

    /// <summary>If true, expose tools that manage storage and content.</summary>
    public bool EnableStorage { get; set; } = true;

    /// <summary>If true, expose snapshot tools (list/create/rollback/delete).</summary>
    public bool EnableSnapshots { get; set; } = true;

    /// <summary>If true, expose backup and vzdump tools.</summary>
    public bool EnableBackups { get; set; } = true;

    /// <summary>If true, expose cluster-wide tools (resources, status, ha).</summary>
    public bool EnableCluster { get; set; } = true;

    /// <summary>If true, expose node-level network configuration tools (bridges, bonds, vlans).</summary>
    public bool EnableNetwork { get; set; } = true;

    /// <summary>If true, expose task tools (list, status, log, stop).</summary>
    public bool EnableTasks { get; set; } = true;

    /// <summary>If true, expose QEMU guest agent tools (introspection + in-guest commands).</summary>
    public bool EnableQemuAgent { get; set; } = true;

    /// <summary>If true, expose APT update tools on nodes (list updates, refresh, changelog).</summary>
    public bool EnableApt { get; set; } = true;

    /// <summary>If true, expose firewall tools at cluster, node and VM/CT levels.</summary>
    public bool EnableFirewall { get; set; } = true;

    /// <summary>If true, expose backup restore tools (re-creates VMs/CTs from vzdump archives).</summary>
    public bool EnableRestore { get; set; } = true;

    /// <summary>If true, allow destroy/delete operations even when ReadOnly is false. Default false.</summary>
    public bool AllowDestroy { get; set; } = false;

    /// <summary>If true, the next manual vzdump backup may be triggered. Requires ReadOnly=false.</summary>
    public bool AllowManualBackup { get; set; } = true;

    /// <summary>If true, snapshot rollback is permitted. Requires ReadOnly=false.</summary>
    public bool AllowSnapshotRollback { get; set; } = false;

    /// <summary>If true, allow running arbitrary commands inside guests via the QEMU guest agent. Requires ReadOnly=false. Default false.</summary>
    public bool AllowGuestExec { get; set; } = false;

    /// <summary>If true, allow writing files into guests via the QEMU guest agent. Requires ReadOnly=false. Default false.</summary>
    public bool AllowGuestFileWrite { get; set; } = false;

    /// <summary>If true, allow setting in-guest user passwords via the QEMU guest agent. Requires ReadOnly=false. Default false.</summary>
    public bool AllowGuestPasswordChange { get; set; } = false;

    /// <summary>Default storage to target for new VM disks and ISO uploads when not specified.</summary>
    public string? DefaultStorage { get; set; }

    /// <summary>Default bridge used for new VM network interfaces. Typically vmbr0.</summary>
    public string DefaultBridge { get; set; } = "vmbr0";

    /// <summary>Default OS type for new QEMU VMs. e.g. "l26" (linux 2.6+).</summary>
    public string DefaultOsType { get; set; } = "l26";
}

public sealed class ServerOptions
{
    public const string SectionName = "Server";

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5705;
    public string Path { get; set; } = "/mcp";

    /// <summary>Service name when running as a Windows Service.</summary>
    public string WindowsServiceName { get; set; } = "ProxmoxMCPSharp";
}
