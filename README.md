# ProxmoxMCPSharp

An MCP (Model Context Protocol) server for Proxmox VE.

Exposes a broad set of tools for:

- Read cluster, node, VM (QEMU) and container (LXC) statistics and diagnostics
- Start, stop, reboot, shutdown, suspend, resume and reset VMs and containers
- Create, clone, migrate, update and destroy VMs and containers
- List/create/rollback/delete snapshots
- List/configure backup jobs and trigger ad-hoc vzdump backups
- Browse storage content, delete backup volumes, see storage usage
- Inspect node tasks, status, logs and RRD performance data
- Configure node networking (bridges) with pending/reload/revert semantics

The server uses the official Proxmox PVE2 HTTPS API (`api2/json/...`).

## Configuration

All settings live under `Proxmox` in `ProxmoxMCPSharp.json` (or via environment variables prefixed with `PROXMOXMCP_`). Environment variables win over JSON; use `__` for nested keys, numeric indexes for arrays such as `PROXMOXMCP_Proxmox__AllowedNodes__0=pve1`, and `true`/`false` for booleans.

A `ReadOnly` flag is **enabled by default** and blocks every write/destroy tool until it is set to `false`. Destructive operations also require `AllowDestroy=true`, manual backups require `AllowManualBackup=true`, and snapshot rollback requires `AllowSnapshotRollback=true`.

Authentication supports either:

- API token (preferred): `ApiTokenId` (e.g. `root@pam!mcp`) and `ApiTokenSecret`.
- Ticket auth: `Username` (with realm), `Password`, optional `TotpCode`.
- `Server:Password` is blank by default. Set it to require an MCP endpoint password; clients may send `Authorization: Bearer <password>`, the Basic auth password, or `X-MCP-Password`.

## Running

### Docker (recommended)

```sh
docker run --rm -p 5705:5705 \
  -e PROXMOXMCP_Proxmox__BaseUrl=https://your-pve:8006/ \
  -e PROXMOXMCP_Proxmox__ApiTokenId='root@pam!mcp' \
  -e PROXMOXMCP_Proxmox__ApiTokenSecret='...' \
  -e PROXMOXMCP_Proxmox__IgnoreCertificateErrors=true \
  -e PROXMOXMCP_Proxmox__AllowedNodes__0=pve1 \
  -e PROXMOXMCP_Server__Password=change-me \
  ghcr.io/wixely/proxmoxmcpsharp:latest
```

### Standalone

```sh
dotnet run --project ProxmoxMCPSharp.csproj
```

### Windows service

Publish the binary, then register:

```powershell
sc.exe create ProxmoxMCPSharp binPath= "C:\Path\To\ProxmoxMCPSharp.exe"
sc.exe start ProxmoxMCPSharp
```

The host detects `WindowsServiceHelpers.IsWindowsService()` and switches to service mode automatically.

## MCP client connection

Use the HTTP endpoint from any Streamable HTTP MCP client:

```json
{
  "mcpServers": {
    "proxmox": {
      "type": "http",
      "url": "http://localhost:5705/mcp"
    }
  }
}
```
