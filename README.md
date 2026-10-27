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

Read-only diagnostics now include paged task history/logs, SMART, ZFS pool health,
managed directory definitions, storage diagnostics and bounded backup coverage.
See [diagnostic contracts, examples and verification limits](docs/diagnostics.md).

Guest disk growth supports read-only previews and explicitly enabled submission.
See [guest management safeguards and examples](docs/guest-management.md).

Users, privilege-separated API tokens, roles and ACLs can be listed, and service
identities provisioned behind a dedicated `AllowAccessManagement` gate.
See [access management safeguards and examples](docs/access-management.md).

## Configuration

All settings live under `Proxmox` in `ProxmoxMCPSharp.json` (or via environment variables prefixed with `PROXMOXMCP_`). Environment variables win over JSON; use `__` for nested keys, numeric indexes for arrays such as `PROXMOXMCP_Proxmox__AllowedNodes__0=pve1`, and `true`/`false` for booleans.

A `ReadOnly` flag is **enabled by default** and blocks every write/destroy tool until it is set to `false`. Destructive operations also require `AllowDestroy=true`, manual backups require `AllowManualBackup=true`, and snapshot rollback requires `AllowSnapshotRollback=true`.

Authentication supports either:

- API token (preferred): `ApiTokenId` (e.g. `root@pam!mcp`) and `ApiTokenSecret`.
- Ticket auth: `Username` (with realm), `Password`, optional `TotpCode`.
- `Server:Password` sets the MCP endpoint password; clients may send `Authorization: Bearer <password>`, the Basic auth password, or `X-MCP-Password`. It is blank by default, which is only allowed while `Server:Host` is loopback (`localhost`, `127.0.0.1`, `::1`). Startup fails if the server would listen on any other address without a password. `Server:AllowUnauthenticatedRemote=true` overrides this and logs a warning; it exposes every enabled tool to anyone who can reach the port.
- `/healthz` reports the effective MCP `authentication` mode (`password`, `none-loopback`, `none-remote-override`) without revealing the password.

## Running

### Docker (recommended)

The image listens on `0.0.0.0`, so it requires `Server:Password`. Keep secrets in an environment file readable only by you rather than on the command line:

```sh
cat > proxmoxmcp.env <<'EOF'
PROXMOXMCP_Proxmox__BaseUrl=https://your-pve:8006/
PROXMOXMCP_Proxmox__ApiTokenId=root@pam!mcp
PROXMOXMCP_Proxmox__ApiTokenSecret=...
PROXMOXMCP_Proxmox__IgnoreCertificateErrors=true
PROXMOXMCP_Proxmox__AllowedNodes__0=pve1
PROXMOXMCP_Server__Password=a-long-random-password
EOF
chmod 600 proxmoxmcp.env

docker run --rm -p 5705:5705 --env-file proxmoxmcp.env ghcr.io/wixely/proxmoxmcpsharp:latest
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

### Linux systemd

An example unit is provided in [deploy/proxmoxmcpsharp.service](deploy/proxmoxmcpsharp.service).
It runs the self-contained Linux binary from `/opt/proxmoxmcpsharp` as a dedicated
`proxmoxmcp` user/group. Create that account and directory during an authorized
deployment, make the application's `logs` directory writable by that account,
and keep configuration files readable only by the service account and administrator.
Optional environment overrides belong in `/etc/proxmoxmcpsharp/environment`.
Install the reviewed unit under `/etc/systemd/system` with root ownership and mode
0644 (not executable or world-writable), then reload systemd and
enable/start `proxmoxmcpsharp`. The unit uses `Type=simple`; readiness is checked
through `/healthz`, and SIGTERM requests graceful shutdown.

See the [verification record](docs/verification.md) for tested modes and remaining
release acceptance. Providing a unit is not evidence of an installed-service test.

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
