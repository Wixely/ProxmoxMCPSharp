# Guest management

Review date: 2026-09-06. Owner: Agent. Next review: 2026-09-13 or before release.

## Disk growth

`resize_qemu_disk` and `resize_lxc_disk` require `vmid`, an explicit `disk`, `size`, and `expectedDigest` from the guest configuration. Optional `node` falls back to the configured default. `dryRun` defaults to **true**; a preview works with `ReadOnly=true` and does not require a management allowance. Set `dryRun=false` to submit an authorized change. Submission additionally requires `ReadOnly=false`, `AllowDiskResize=true`, the applicable guest category and `EnableStorage`. Node and guest allow/deny rules apply before any read.

```json
{"node":"node-a","vmid":100,"disk":"scsi0","size":"+10G","expectedDigest":"<current configuration SHA1>","dryRun":true}
```

Sizes are positive bytes or binary `K`, `M`, `G`, `T`, optionally prefixed with `+`. For example, 64 GiB plus `+10G` resolves to 74 GiB. The report returns the fixed absolute byte target; the PUT request always sends that absolute target. Zero growth, shrinkage, overflow and targets over the application safety bound of 1 PiB are rejected. Application alignment is 1 MiB, or 4 MiB for classic LVM; actual backend allocation granularity can differ and must be checked by readback. These are application bounds, not a claim that every backend uses that extent size.

QEMU slots include IDE, SATA, SCSI and VirtIO disks. EFI, TPM, optical, unused slots and unmanaged devices are excluded. LXC accepts `rootfs` and `mp0` through `mp31`, with ownership established by an exact volume ID in the storage content response. Bind and device mounts are excluded. Empty slots, unresolved pending disk changes, locks, templates and unknown guest states are rejected. Both current configuration and actual storage size must agree before calculating growth; mismatch requires reconciliation first.

The initial backend/format allowlist is:

| Backend | QEMU | LXC |
| --- | --- | --- |
| dir, nfs, cifs | raw, qcow2 | raw |
| lvm, lvmthin, rbd | raw | raw |
| zfspool | raw | raw, subvol |

The PVE APIs handle running/stopped guests through their backend and monitor implementations. No guest is automatically stopped or restarted. Unsupported formats/backends are rejected, and backend/online errors remain explicit task/readback outcomes. Source review does not establish live compatibility for each combination.

Previews include original/target size, volume identity, capacity evidence, blockers and backup visibility on the selected storage only. Missing backup evidence does not invent a restore guarantee or automatically start a backup. Unknown/insufficient free storage blocks submission; a preview still explains the blocker. Capacity is not reserved. Immediately before submission the service rechecks digest, disk identity, actual size, pending changes and guest run state, then passes the digest for PVE's locked check.

Mutation responses are size-bounded and sanitized. No automatic mutation retries occur. A lost response produces an unknown operation outcome, followed by readback when cancellation permits. Reusing the old digest after completed growth is rejected; divergence between configured and actual size also blocks further relative growth. This does not promise exactly-once execution across independent requests. Inspect task and disk state before making a new request after an uncertain result.

`waitSeconds` defaults to 10 and is bounded to 0–60. Task polling issues at most `waitSeconds + 1` status reads, plus a bounded first 1,000 log lines on completion; individual requests retain the configured HTTP timeout. A zero budget makes one immediate status check. Disabled task reads preserve the task ID without polling. Cancellation or exhausting the polling budget never stops a PVE task. Synchronous null responses are accepted for compatibility and require readback; they are not reported as a verified task success.

Reports distinguish task status, observed/configured virtual capacity and usable guest filesystem space. LXC's resize API can grow a volume and then warn that filesystem growth failed, even with an OK task result. QEMU guest-agent filesystem inspection runs only when enabled/configured and the guest is running; it remains observational because partition-to-disk correlation is not established. No guest commands, agent installation, partition changes or reboot are issued. The report supplies a concrete next action for guest-space verification.

`get_qemu_pending_config` and `get_lxc_pending_config` preserve the upstream array of current/pending/deleted values. `get_qemu_config(pending=false)` now explicitly sends `current=1`; true sends `current=0`, and null preserves its previous upstream-default behavior.

## Disk attachment, movement and managed mount points

`add_qemu_disk` creates a disk on explicit storage in an empty IDE/SATA/SCSI/VirtIO slot. `sizeGiB` is positive, at most 1 PiB and aligned to a MiB. Supported target backends are dir/nfs/cifs (raw or qcow2) and lvm/lvmthin/zfspool/rbd (raw). Raw is the default QEMU format. Typed cache, discard, SSD and IO-thread options are supported; SCSI IO threads require an active `virtio-scsi-single` controller. Storage content support, node restrictions, active status and available capacity are checked before allocation. Capacity is observed, not reserved.

`attach_qemu_disk` attaches a discovered `unusedN` reference owned by the same guest to an empty slot. `detach_qemu_disk` removes a regular slot through the configuration API without `force`, retaining the physical volume as an unused reference. Boot-order members and the configured boot disk cannot be detached; unresolved boot identity blocks detachment. This protection uses PVE configuration evidence and cannot identify every guest operating-system dependency. Optical, EFI/TPM, cross-guest and explicitly shared volumes are excluded.

`move_qemu_disk` and `move_lxc_volume` move within the same guest to a different explicit storage. The request sends `delete=0` by default. `deleteSource=true` additionally requires `AllowDestroy=true` for submission. Optional bandwidth is 1–1,048,576 KiB/s. LXC moves require a stopped container; the tool never stops it. QEMU moves accept compatible raw/qcow2 target formats; LXC uses its backend's format. Upstream snapshot and backend restrictions still apply and can reject a task after preflight.

`add_lxc_mount_point` allocates a managed volume in an empty `mp0`–`mp31`, with an explicit non-system absolute guest path and optional backup/read-only flags. Bind/device mounts and privilege settings are excluded. Current and pending mount-path collisions are rejected. `detach_lxc_mount_point` retains the managed volume; rootfs detachment is excluded. LXC targets use raw storage volumes or ZFS subvolumes. No guest formatting or initialization is performed.

All seven tools require an expected configuration digest, default to `dryRun=true`, and use the shared node/guest/category restrictions. Submission requires `ReadOnly=false` and `AllowDiskManagement=true`. The tool repeats preflight immediately before the mutation and sends the digest upstream. Task polling and uncertain-response handling use the same bounded, no-retry workflow as resize.

Readback separates current/pending configuration, target capacity, retained unused references and scoped source inventory. A pending allocation is not active. Source retention is verified only when both its unused reference and owned inventory entry are observed. Requested deletion is reported as source visibility, not proof of physical erasure. Task success does not establish guest filesystem usability.

The disk/configuration and move schemas and retention behavior were reviewed in the pinned QEMU/LXC revisions below, including QEMU `PVE/QemuServer.pm` and LXC `src/PVE/LXC/Config.pm`. Contract fixtures cover all seven operations, ownership, boot/pending conflicts, capacity gates, source retention/deletion, and accepted writes followed by response loss. MCP discovery/preview/write-gate checks are synthetic. Live backend and guest-state acceptance remains outstanding.

## Shared write safeguards

`update_qemu_resources` accepts typed optional cores, sockets, vcpus, memory MiB, balloon MiB, CPU limit and CPU units. `update_lxc_resources` accepts cores, memory/swap MiB, CPU limit and CPU units. Both require an expected digest and default to previews. Submission requires `AllowResourceChanges=true` and `ReadOnly=false`; host capacity reads require `EnableNodes`. Core/socket/vCPU and balloon/memory combinations are validated together, including existing values. Pending resource changes block new submissions. QEMU's scalar and `current=<MiB>` memory syntax are both understood.

The resource tools conservatively block submission when CPU changes exceed observed physical host CPUs or a memory increase exceeds observed free host RAM; CPU overcommit is not implicit. Previews explain these blockers. Application bounds are 1–1024 cores/sockets/vCPUs (maximum 1024 combined), 16–1,048,576 MiB memory, 0–1,048,576 MiB balloon/swap, CPU limit 0–1024, and CPU units 1–10,000 for LXC or 1–262,144 for QEMU. A zero CPU limit retains PVE's unlimited semantics. All requests use invariant numeric formatting. Current and pending values are read back separately; a pending change is not presented as active and no restart occurs.

`AllowDiskResize`, `AllowDiskManagement` and `AllowResourceChanges` default to false in configuration. The latter two are shared opt-ins for the subsequent management stages. Destructive source deletion additionally needs explicit deletion intent and `AllowDestroy=true`. No lock bypass or force parameter is exposed.

## Structured configuration and legacy compatibility

`update_qemu_config` accepts either a `fields` JSON object or the legacy `keyValuePairs` string, exclusively. `update_lxc_config` accepts a `fields` object. Structured calls require `expectedDigest` and default to preview. Values must be unique scalars; arrays, nested objects and nulls are rejected. Requests are bounded to 64 fields and 131,072 characters across field names and values.

```json
{"vmid":100,"expectedDigest":"<current configuration SHA1>","fields":{"memory":4096,"cores":4,"net0":"virtio=02:00:00:00:00:01,bridge=vmbr0,tag=20"}}
```

Supported metadata is QEMU `name` or LXC `hostname`, plus `description`, `onboot`, `startup` and `tags`. Startup supports `order`, `up` and `down`, each 0–86,400. Resource fields use their PVE names (`memory`, `balloon`, `swap`, `cpulimit`, `cpuunits`, `cores`, `sockets`, `vcpus`) and the same guest-specific ranges, capacity checks and `AllowResourceChanges` gate as the typed tools. Metadata, network and resource changes are validated together and sent as one configuration request. This does not promise that every field hotplugs together: PVE can leave pending changes or report partial application, so readback remains required.

`net0`–`net31` accepts validated property strings and additionally requires `EnableNetwork`. QEMU models are virtio/e1000/e1000e/rtl8139/vmxnet3, with optional MAC, bridge, VLAN tag/trunk list, firewall, link-down, rate, MTU and queues. LXC supports explicit interface name, veth type, bridge/MAC, IPv4/IPv6 addresses and gateways, VLAN tag/trunk list, firewall, link-down, rate and MTU. VLANs are 1–4094; trunks are semicolon-separated IDs, without ranges. Rate is bounded to 0–1,000,000 in PVE's native units; QEMU queues are 0–64. MTU is 1–65,520 for QEMU VirtIO and 64–65,535 for LXC. Interface/bridge names are bounded to 15 characters. The API retains responsibility for bridge availability and backend-specific constraints. No network interface is implicitly removed.

A disk operation must be the only field in the request. A regular QEMU slot accepts `storage:sizeGiB` allocation syntax and typed cache/discard/SSD/IO-thread/format options, or the volume ID of a discovered owned unused disk for attachment. An LXC `mpN` accepts managed allocation with `mp`, `backup` and `ro`. `delete=<regular slot>` routes to retained-volume detachment; deleting unused volumes, rootfs or boot disks is excluded. These requests use the focused disk service, including `AllowDiskManagement`, ownership, pending-state and capacity checks. Existing disks cannot be overwritten through this route. A disk's `size` property is rejected: physical growth uses resize, and storage moves use the move tools.

Legacy QEMU calls keep their argument names and default to submission unless `dryRun=true` is supplied. When no expected digest is supplied, the service reads one before preflight and passes it through the same concurrency checks. An explicit `digest` in legacy fields is supported; conflicting digests are rejected. Device/startup subproperties remain within their field rather than becoming separate API parameters. Use structured fields when metadata contains an ambiguous `,<field>=` sequence. Legacy output retains `data` (task ID or null) and adds a `management` report with operation/readback evidence; inspect it after uncertain outcomes instead of blindly retrying.

Arbitrary API fields, scripts, force/lock bypasses and privilege changes are rejected. This supported-field contract and the management gates tighten earlier unrestricted calls. Current/pending values are returned separately; NIC readback accounts for PVE model aliases, property ordering and MAC case. No automatic restart occurs.

Network property schemas were checked in both pinned release-period revisions: QEMU `PVE/QemuServer.pm` and LXC [`src/PVE/LXC/Config.pm` (8.2 period)](https://github.com/proxmox/pve-container/blob/ab862d6edf5d2903571596279537c48eb7fac125/src/PVE/LXC/Config.pm), [8.3 period](https://github.com/proxmox/pve-container/blob/a39c5b0617e15aabf5259f9743337beb16c2c173/src/PVE/LXC/Config.pm). This is source review, not live network acceptance.

## Clone and migration

`clone_lxc` adds parity with `clone_qemu`, accepting explicit source/destination IDs and optional target node/storage, name, full/linked mode and bandwidth. New LXC calls require `expectedDigest` and default to preview. Existing QEMU argument names and write-by-default behavior remain; an optional expected digest and `dryRun=true` are now supported. Templates can be cloned, with the upstream default selecting a linked clone for templates and a full clone for ordinary guests when `full` is omitted. Explicit target storage requires full cloning. Source guests are never stopped by the clone wrapper.

`migrate_lxc` accepts an explicit target node, expected digest, optional target storage and bandwidth. A running LXC guest is blocked unless `restartMigration=true` is explicit. That option requests upstream shutdown and restart with downtime; `shutdownTimeout` is 1–600 seconds (default 180). Pinned LXC migration workers reject live migration of running containers. `migrate_qemu` requires `online=true` for a running VM and additionally accepts target storage and `withLocalDisks`. No force, insecure transport or lock-bypass option is exposed. Both migration wrappers block HA-managed guests because upstream HA delegation can override request options.

All four tools require the guest/storage categories and node/cluster reads for preflight. Submission requires `ReadOnly=false` and `AllowDiskManagement=true`, including existing QEMU clone/migrate calls. Source and destination nodes/IDs are authorized before any upstream read. The tool checks source location/type, destination-ID collisions for clones, locks, pending changes, guest run state and selected destination-storage status/content/node restrictions. Cross-node clone requires shared target storage when explicitly selected; source-volume/backend constraints remain upstream checks. No cluster-wide data is returned by the scoped report.

Bandwidth is bounded to 1–1,048,576 KiB/s. Polling uses the shared 0–60 second budget. Reports retain a `data` task ID field for existing QEMU consumers and add operation, downtime and destination-location evidence. Destination presence alone does not prove clone provenance or application health. Unknown responses are never retried automatically.

Neither clone nor migration accepts a digest in the reviewed APIs. The wrapper captures or checks the source digest and repeats configuration, run-state, inventory and HA/storage preflight immediately before submission, but there is no atomic upstream digest check. Concurrent administration can still change eligibility, destination occupancy or HA policy. Storage capacity is not reserved or comprehensively estimated; PVE also checks source-volume sharing, bridge availability and transport/backend constraints. Task and guest verification remains required.

The POST clone/migrate schemas were reviewed in the pinned API sources below. LXC running-guest behavior is implemented in [`PVE/LXC/Migrate.pm`](https://github.com/proxmox/pve-container/blob/a39c5b0617e15aabf5259f9743337beb16c2c173/src/PVE/LXC/Migrate.pm). Synthetic tests cover both guest kinds, preview/write behavior, destination restrictions, templates, explicit migration modes, HA exclusion and response loss. No live clone, migration, shutdown or restart has been performed during development.

## Pinned source review

The resize request schemas are identical between the two reviewed release-period revisions. Both use PUT, accept a digest, require guest `VM.Config.Disk` and check datastore `Datastore.AllocateSpace`. Both reviewed versions return task IDs. The QEMU worker rechecks digest under its guest lock; LXC performs early checks and repeats them in the locked worker. Both query actual volume size. LXC records updated volume size before attempting raw filesystem growth, which can emit warnings.

| Package version | Revision / source |
| --- | --- |
| qemu-server 8.2.0 | [c82cb51](https://github.com/proxmox/qemu-server/blob/c82cb5159092b2d5d360400892b35aa1590fd25a/PVE/API2/Qemu.pm) |
| qemu-server 8.3.0 | [7547eb3](https://github.com/proxmox/qemu-server/blob/7547eb37a96a648a7946c584400295eab23f594e/PVE/API2/Qemu.pm) |
| pve-container 5.0.10 (PVE 8.2 period) | [ab862d6](https://github.com/proxmox/pve-container/blob/ab862d6edf5d2903571596279537c48eb7fac125/src/PVE/API2/LXC.pm) |
| pve-container 5.2.2 (PVE 8.3 period) | [a39c5b0](https://github.com/proxmox/pve-container/blob/a39c5b0617e15aabf5259f9743337beb16c2c173/src/PVE/API2/LXC.pm) |

Current/pending configuration was also checked in these revisions (`PVE/API2/Qemu.pm`, `src/PVE/API2/LXC/Config.pm`, and the LXC pending endpoint). This is source evidence; no live VM has been resized during development.

## Implementation status

- Diagnostics M0–M3: implemented and committed, with synthetic tests; live acceptance remains outstanding.
- G1: resize, current/pending correction, bounded mutations and shared gate enforcement implemented with synthetic and MCP protocol tests.
- G2: typed resource/disk operations and structured configuration implemented with synthetic and MCP protocol tests. Live acceptance remains outstanding. Owner: Agent for local checks, operator for live acceptance.
- G3: managed LXC mount points and clone/migration parity implemented with synthetic tests and MCP discovery checks. Live acceptance remains outstanding. Owner: Agent for local checks, operator for live acceptance.
- M4: final package/runtime checks and documentation follow implementation. Live mutation acceptance requires an explicitly authorized disposable guest/storage scope; deployment is separate. Owner: operator for live acceptance, Agent for local verification.

Recommended next action: Agent reconciles the external plan and completes the requirement audit and local release verification. Operator supplies a separately authorized disposable scope for live acceptance. The installed service and everyday guests are outside the development test scope.
