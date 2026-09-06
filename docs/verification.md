# Implementation and release verification

Reviewed: 2026-09-06. Next review: 2026-09-13 or before release. Owner: Agent for implementation and local checks; operator for live acceptance and deployment.

This record tracks the diagnostics and guest-management plan. It distinguishes implemented behavior from synthetic contract coverage, package checks and live acceptance. The feature commits through `3b80293` contain the requested tool surface; release readiness is not yet established.

## Requirement audit

| Plan requirement | Authoritative implementation and evidence | Remaining verification |
| --- | --- | --- |
| M0: HTTP seam, target schemas, real tests | Injectable `ProxmoxClient`; solution includes the xUnit/TestHost project; pinned API revisions, privilege checkpoint and backend constraints in `diagnostics.md` and `guest-management.md` | Actual PVE compatibility, effective token privileges and minimum supported version |
| M1: tasks/logs, SMART, ZFS | `TaskTools`, `DiskTools`, `ZfsTools`, `NodeTools`; `ReadTests` checks query parameters, unrelated/older tasks, changing/empty pages, multibyte logs, ATA/vendor/missing health, bounds and errors | Selected live hardware/history acceptance |
| M2: managed directories and storage report | `StorageDiagnosticsService`; `AggregateTests` distinguishes disabled/restricted/inactive, absent/denied evidence and matching paths | Live mount/storage comparison; API evidence cannot prove full mount state |
| M3: backup coverage | `BackupCoverageService`; mixed archive/job/task fixtures, denied history, inactive targets and 100-guest/20-storage/20-page maximum (63 reads) | Selected live backup scope; no restore guarantee |
| G1: growth and pending reads | `DiskResizeService`, `GuestDiskTools`, corrected QEMU getter; `ResizeTests` covers absolute growth, gates, races, uncertain responses, task/readback and guest-space limits | Authorized disposable QEMU/LXC/backend acceptance |
| G2: disk/resource/configuration | `DiskManagementService`, `GuestConfigurationService`, `StructuredConfigurationService`; disk/resource/structured and MCP tests, including missing/malformed pending readback | Live backend/state acceptance; supported-field tightening documented |
| G3: managed mount points and transfer parity | Disk service plus `GuestTransferService`; `TransferTests` checks both endpoints before reads, templates, migration mode, HA exclusion and response loss | Authorized clone/migration acceptance, including explicit LXC restart mode |
| Bounded reads/mutations and no retry | Byte/page/poll budgets, sanitized failures, readback after uncertain writes; cancellation/timeout fixtures | No claim of exactly-once execution or atomic clone/migration digest checks |
| Write/destruction/visibility gates | ReadOnly and category/target checks; three false-by-default management allowances; explicit source deletion additionally requires AllowDestroy | Live least-privilege validation; broad legacy inventory semantics remain documented |
| Compatibility and MCP contract | Legacy QEMU/task payload tests, optional new parameters, structured object calls and tool annotations | Installed-client acceptance against a candidate release |
| Packaging, CI and documentation | Windows/Linux PR matrix; 7-day test evidence, 1-day inter-job/tag and 3-day development artifacts; changelog, contracts, retained debug configuration and rootless OCI container smoke test | CI has not run for local-only commits; installed-service acceptance and release review |

## Local results

- Release build and all 144 discovered tests passed, with no skips, after the evidence-reporting audit. Tests use synthetic upstream responses and in-process MCP hosting. The audit corrected missing-pending readback being mistaken for no pending changes, and retained known terminal task status when task logs are unavailable.
- Windows x64 and Linux x64 self-contained and framework-dependent single-file publishing passed after the packaging changes. Each of four clean package directories contained exactly the executable, default JSON configuration, MIT license and third-party notices. A synthetic `ProxmoxMCPSharp.PackagingAudit.Local.json` was present during publishing and excluded from every package, then removed. Packaged configuration was checked: ReadOnly=true and all destruction/disk/resource allowances=false. No test source, symbols, smoke logs or IIS assets were present.
- Self-contained Windows console and Linux console under WSL both returned HTTP 200 with `status=ok` and `readOnly=true` from `/healthz`. Both used a synthetic upstream URL and were stopped after the checks. Linux stopped after a bounded SIGTERM timeout; Windows stopped through its tool-owned console session.
- Standalone framework-dependent packages were built but not executed. The Dockerfile's framework-dependent build was executed in its runtime image. Linux console execution under WSL does not establish systemd, other distributions or ARM64 behavior.
- Docker Engine/CLI was not available in the checked Windows/WSL environments, but existing rootless Podman 4.9.3 was available. It successfully built the unchanged Dockerfile for linux/amd64. The container ran as UID 1654 with synthetic upstream configuration and a loopback-only published test port, returned HTTP 200 with `status=ok` and `readOnly=true`, and shut down through bounded SIGTERM. The application directory contained the executable, default configuration, license notices and logs directory. Automatic test-container removal was verified; the test image was removed afterward. Shared build cache was not pruned. No runtime or integration was installed. Docker Engine itself and ARM64 remain untested.
- `systemd-analyze verify` parsed the example unit but returned failure because `/opt/proxmoxmcpsharp/ProxmoxMCPSharp` is not installed. It also warned about executable/world-writable mode on the Windows-mounted unit file. This is not successful unit/deployment acceptance. The unit has not been registered or started; a deployment must install it with normal root ownership and non-executable, non-world-writable permissions.
- The existing SYSLIB0057 certificate-loading warning remains. It does not fail the build.
- The existing MCPHub Proxmox connection answered a read-only version probe with PVE 8.2.2. Its exposed catalogue lacks the new resize/storage-report/LXC-clone tools. This confirms existing connectivity only, not candidate endpoint acceptance. No live Proxmox mutation, push, release, installed-service replacement or service registration was performed.

## Remaining work and owners

1. Agent: local fixture and privilege-source audits are complete. All four candidate packages were refreshed after the reporting fixes and passed the four-file content check. Console-host smoke checks preceded those reporting-only fixes; no hosting/dependency code changed afterward.
2. Operator: provide an explicitly authorized disposable guest/storage scope for management acceptance, and selected nodes/devices for diagnostic comparison on each claimed PVE version.
3. Operator: authorize isolated Windows Service and systemd acceptance in an appropriate environment; no replacement of the installed MCP service is implicit. OCI container acceptance passed under Podman; any Docker Engine-specific or ARM64 support claim needs its own verification.
4. Agent: perform the full outgoing-history identity/privacy review if a push is requested, and prepare release notes from the final changelog. Publishing/deployment is a separate action.

Recommended next action: obtain authorized live acceptance scope and isolated installed-service test environments. Keep regression tests; generated `artifacts`, `TestResults`, `bin` and `obj` are ignored and disposable after evidence has been reviewed. Do not package smoke-test logs with a release.
