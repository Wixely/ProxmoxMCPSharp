# Read-only diagnostics

Implementation reviewed: 2026-09-06. Owner: Agent. Next compatibility review: 2026-09-13, or before release.

The new tools use the existing HTTPS client and HTTP MCP host. They add no SSH, shell, database, maintenance operations or write allowances. The existing single-file deployment remains; NativeAOT and a hosting rewrite are outside this change.

## Task and log paging

`list_node_tasks` accepts optional `node`, `running`, `limit`, `source`, `start`, `since`, `until`, `typeFilter`, `vmid` and `statusFilter`. `start` is a nonnegative offset. `limit` defaults to 50 and must be 1–500. `since` and `until` are inclusive UTC Unix seconds; reversed or negative ranges are rejected. `statusFilter` is a comma-separated selection of `ok`, `warning`, `error`, `unknown`. `source` is `archive` (default), `active` or `all`.

`running=true` selects `source=active` and removes terminal tasks from that one upstream page. It rejects an explicit `source=archive`; an explicit `all` is narrowed to `active`. It never sends the unsupported `running` HTTP parameter. `false` and omitted values do not add a running filter. `total` remains the upstream pre-filter count. Advance `start` by the requested `limit`, even when the filtered page is empty. A source page can contain recently finished tasks as well as running tasks.

`get_task_log` preserves upstream `data`, line numbers and `total`. It reads forward from `start=0` by default, with `limit=50` and a maximum of 1,000. The old description incorrectly promised a 1,000-line tail; tailing was never implemented. New arguments are optional in MCP discovery and old argument payloads remain valid within the documented bounds. Zero or excessive limits are now rejected.

Example MCP arguments to find an older backup:

```json
{"node":"node-a","source":"archive","typeFilter":"vzdump","start":500,"limit":500}
```

Then page its log with `{"upid":"<UPID from task history>","node":"node-a","start":0,"limit":1000}` and offsets 1000, 2000, etc. Stop according to upstream totals/available lines. Offset paging is a live view: rotation, new tasks and concurrent log writes can change results. History is retained and permission-limited; an empty response cannot establish that nothing ran. Numeric task IDs are not universally VMIDs. Only an explicit task `vmid` filter is checked against configured guest restrictions.

## Hardware and system evidence

| Tool | Contract |
| --- | --- |
| `list_node_disks` | Adds optional `skipSmart` and `includePartitions`; omitted values preserve upstream defaults. |
| `get_node_disk_smart` | Required `disk` is the `/dev/` path discovered above; optional `node` and `healthOnly`. Returns upstream health, attributes and vendor text without treating absent/UNKNOWN health as healthy. Does not start a SMART test. |
| `list_node_zfs_pools` | Optional node; returns physical pool capacity and health. |
| `get_node_zfs_pool_status` | Required physical pool name, optional node; preserves state, errors, vdev children and current/latest scan text. No historical scrub guarantee. |
| `list_node_directory_mounts` | Optional node; returns PVE-managed directory definitions projected to path, device, type and unitfile. Omits mount options. This is configuration, not a live mount table. |
| `get_node_syslog` | Adds optional `start`, `since`, `until`; default 200 lines, maximum 1,000. Dates use `yyyy-MM-dd` or `yyyy-MM-dd HH:mm[:ss]` in the node's local timezone, unlike task epochs. |

All these reads require `EnableNodes`. Task reads require `EnableTasks`. Category flags are enforced in tool bodies and do not remove tools from discovery. New/extended tools carry read-only, idempotent and open-world annotations. All requested nodes pass the configured allow/deny rules.

## Storage diagnostics

`get_storage_diagnostics` accepts a storage ID and optional node. Its version-1 envelope includes `observedAtUtc`, node, storage, `configurationSummary`, `availability`, `statusEvidence`, `backingEvidence`, `observations`, `missingEvidence`, `partial`, `errors` and `upstreamReads`.

It distinguishes disabled, node-restricted, active, inactive and unknown availability. Failed reads have a component and sanitized reason such as `permission-denied`, `http-404`, `read-timeout` or `response-byte-budget-exceeded`; absent information has a separate missing-evidence reason. Successful evidence remains available after another component fails.

Only type, content, node restrictions, disable, required-mountpoint setting, path, pool and shared setting are copied from configuration. Mount options, credentials and account fields are omitted. Operational paths remain useful in live responses. Directory definitions are matched by configured path. A `zfspool` dataset such as `pool-a/guests` identifies physical pool `pool-a`; the storage ID is never guessed to be the pool name. Other backends retain availability evidence with an unsupported-backing-diagnostics reason.

The report requires `EnableStorage`; directory and ZFS components additionally require `EnableNodes`. Disabled components are marked unavailable. It performs at most three reads in the implemented branches, with a hard eight-read guard and 4 MiB per-response cap. It never enumerates storage content, probes credentials or establishes why a filesystem failed to mount. Even a matched managed definition leaves live mount state unverified.

## Backup coverage

`get_backup_coverage` requires an explicit `node`, `vmids` array (1–100) and `storages` array (1–20). Optional `lookbackDays` defaults to 30 (1–365), `taskPageBudget` defaults to 20 (1–20), and `maxAgeHours` must be positive and finite if supplied. Duplicate scope entries are queried once. Guest restrictions are checked before any upstream request. Only requested guest IDs appear in guest results; pre-existing broad inventory tools retain their existing visibility semantics.

```json
{"node":"node-a","vmids":[100,101],"storages":["backup-a"],"lookbackDays":30,"maxAgeHours":48,"taskPageBudget":10}
```

This report requires `EnableBackups`, `EnableStorage`, `EnableTasks`, `EnableNodes`, `EnableQemu` and `EnableLxc`. It reads node QEMU/LXC inventories, backup jobs, requested target status/content and up to the task-page budget using `typefilter=vzdump`. No pool or cluster inventory expansion occurs. Pool-selected jobs remain unresolved; all/explicit lists, exclusions and node constraints are evaluated. Job schedules, enabled state and target availability are separate facts. Targets named by jobs but absent from the requested storage scope are marked `not-queried`.

Coverage states are `recent-visible-backup`, `stale-visible-backup`, `no-visible-backup` or `unknown`. Without a freshness threshold, a dated archive uses `visible-backup` with its age; no universal overdue rule is invented. Archive times are the API's `ctime`. Archives without a usable guest ID or timestamp leave evidence incomplete. A stale result requires complete visible archive enumeration; a recent observed archive remains useful when other reads are incomplete. A guest absent from requested-node inventory has unknown coverage.

Only guest-specific vzdump tasks are correlated to guests. Node-wide tasks remain separate evidence; their success is not assigned to each guest or a particular job. Warning/error terminal statuses are reported as failure-or-warning. Raw task status text, backup notes and upstream error bodies are not copied into reports.

The report includes scanned pages/window, queried targets and query node, read count, errors, truncation and per-guest completeness/reasons. Reads run sequentially, each with a 1 MiB decoded-response cap, for at most 63 upstream reads (3 inventory/job + 40 target + 20 task pages). Content endpoints have no pagination contract, so an oversized response is discarded and flagged incomplete. Inactive/unknown targets are not enumerated. Distinct storage IDs are not assumed to be one backend or deduplicated across aliases. Task history uses live offsets and remains subject to privileges and retention even when all visible pages were read.

An enabled job, a saved archive or an OK task does not establish recoverability. Nothing here triggers backup, prune, mount, scrub or restore.

## Pinned API review

The following release-period revisions were reviewed on 2026-09-06, including their `debian/changelog` version. This is source compatibility evidence, not verification of an installed PVE system or a promise covering every PVE 8 release.

| Component | PVE 8.2-period source | PVE 8.3-period source |
| --- | --- | --- |
| pve-manager | [8.2.2, fb60669](https://github.com/proxmox/pve-manager/tree/fb60669f30534478976c2ca4e299e244a66430f2) | [8.3.0, c1689cc](https://github.com/proxmox/pve-manager/tree/c1689ccb1065a83be900bca61c2a56314126f4ea) |
| libpve-storage-perl | [8.2.1, bb72798](https://github.com/proxmox/pve-storage/tree/bb7279806475f9326a7f934ab6c4f18a72a2e295) | [8.2.9, 39fd552](https://github.com/proxmox/pve-storage/tree/39fd552d14c075a9760fcdbbecd207b8c8028731) |

| API source / endpoint | Checked contract in both periods | Privilege evidence |
| --- | --- | --- |
| `PVE/API2/Tasks.pm`: tasks and task log | Task offsets, epochs, type/VM/status filters and archive/active/all; log offset/limit and totals. No `running` query property. | Own tasks visible; broader reads use `Sys.Audit` on `/nodes/{node}`. |
| `PVE/API2/Nodes.pm`: syslog | Offset/limit, node-local date strings, service max length 128. | `Sys.Syslog` on `/nodes/{node}`. |
| `src/PVE/API2/Disks.pm`: list, smart | `skipsmart`, `include-partitions`, `disk`, `healthonly`. | List: root or node `Sys.Audit`; SMART: root `Sys.Audit`. |
| `src/PVE/API2/Disks/ZFS.pm` | Pool list and detail, state/errors/children/scan. | Root `Sys.Audit`. |
| `src/PVE/API2/Disks/Directory.pm` | Managed mount-unit definitions including path/device/type/options. | Root `Sys.Audit`. |

The 8.3-period [backup job source](https://github.com/proxmox/pve-manager/blob/c1689ccb1065a83be900bca61c2a56314126f4ea/PVE/API2/Backup.pm) requires root `Sys.Audit` for job listing. The [storage content source](https://github.com/proxmox/pve-storage/blob/39fd552d14c075a9760fcdbbecd207b8c8028731/src/PVE/API2/Storage/Content.pm) documents optional VMID/ctime and storage `Datastore.Audit` or `Datastore.AllocateSpace`. Other component reads retain their upstream privilege checks; reports do not infer effective privileges from an empty result.

## Verification and remaining release work

Run `dotnet build ProxmoxMCPSharp.sln -c Release`, then `dotnet test ProxmoxMCPSharp.sln -c Release --no-build`. Tests use synthetic HTTP fixtures and an in-process MCP HTTP host, including old payloads, tool schema optionality, GET-only execution, request bounds, paging, multibyte logs, cancellation/timeouts, denied access, hardware responses, storage states, partial backup coverage and maximum 100-guest/20-storage/20-page scope. Test source/content is excluded from application packaging.

Milestones M0–M3 have local implementation and automated coverage. M4 includes documentation and Windows/Linux CI configuration; a configured CI job is not a completed Linux verification. Release artifacts retain one day for tag builds and three for manual development builds; test evidence retains seven days. Existing VS Code debugging and deployment structure are preserved.

Local results on 2026-09-06: Release solution build passed; all 26 discovered tests passed with no skips; whitespace review passed. Windows x64 and Linux x64 self-contained single-file publishing passed, each producing an executable plus configuration and no test assets. The Windows executable started in console mode and returned HTTP 200 with `status=ok` and `readOnly=true` from `/healthz`, using a synthetic upstream URL. It was stopped after the check. The existing `SYSLIB0057` certificate-loading warning remains. Linux was cross-published from Windows and was not executed.

Remaining before release (owner: operator; review 2026-09-13): read-only acceptance on selected PVE 8.2/8.3 nodes/devices, any newer version claimed, Windows Service mode, Linux execution/systemd and Docker. Compare raw responses with tool output without starting maintenance operations. Decide the minimum supported PVE version from this evidence. Publishing and replacing the installed MCP service require a separate release/deployment step. Recommended next action: the operator should run the live compatibility checks, then request release review.

Superseding verification on 2026-09-06 after guest-management implementation: 133 tests passed. All four Windows/Linux x64 package variants published and passed content/default-gate checks. Self-contained console health checks passed on Windows and Linux under WSL. Windows Service, systemd, Docker and live PVE acceptance remain unverified. See the current [requirement audit and release verification](verification.md); the earlier 26-test result above is historical.
