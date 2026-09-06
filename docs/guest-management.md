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

## Shared write safeguards

`AllowDiskResize`, `AllowDiskManagement` and `AllowResourceChanges` default to false in configuration. The latter two are shared opt-ins for the subsequent management stages. Destructive source deletion additionally needs explicit deletion intent and `AllowDestroy=true`. No lock bypass or force parameter is exposed.

The legacy QEMU configuration writer cannot act as an arbitrary bypass: disk, delete, force, lock and CPU/memory fields require validated management handling. Metadata fields (`name`, `description`, `onboot`, `startup`, `tags`) retain their legacy route. Structured configuration and typed resource/disk tools in G2 must use the same validation and gates; accepting arbitrary PVE fields is outside the contract. This tightening must be considered when upgrading existing clients.

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
- G2: typed disk/resource/configuration management remains in progress. Owner: Agent.
- G3: managed LXC mount points and clone/migration parity remain in progress. Owner: Agent.
- M4: final package/runtime checks and documentation follow implementation. Live mutation acceptance requires an explicitly authorized disposable guest/storage scope; deployment is separate. Owner: operator for live acceptance, Agent for local verification.

Recommended next action: Agent completes G2, then G3, without changing the installed service or using everyday guests as test fixtures.
