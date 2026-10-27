# Changelog

## Unreleased

- Advertise `update_qemu_config`/`update_lxc_config` `fields` as a constrained object (known keys, scalar value types, `netN`/disk-slot key patterns) instead of the `true` schema, which hid the contract and broke some clients' schema converters. Runtime validation is unchanged.
- **Breaking for unauthenticated remote setups:** startup now fails when `Server:Host` is not loopback and `Server:Password` is blank, including the Docker image's default `0.0.0.0` bind. Set a password, or opt in with `Server:AllowUnauthenticatedRemote=true` (default false, logs a warning). The MCP endpoint also refuses non-loopback clients if the password is blanked by a configuration reload. `/healthz` reports the authentication mode, and the Docker example uses an env file instead of command-line secrets.

## v1.2.0-preview.1 - 2026-09-06

- Preserve observed task outcomes when log inspection fails, and leave restart requirements unverified when pending-configuration readback is missing or malformed.

- Exclude local overrides from release packages and Docker build context, include license notices, disable unused IIS assets, and document a systemd unit and verified Windows/Linux console results.

- Add LXC clone/migration and harden QEMU equivalents with source/destination authorization, previews, bounded task/readback evidence and shared management gates. Running migration needs explicit QEMU online mode or LXC restart intent; HA-managed migration is excluded.

- Add structured QEMU/LXC configuration with shared resource/disk safeguards, validated network properties, digest checks and previews. Preserve legacy QEMU payloads and `data` responses while adding management evidence; device/startup commas stay inside their fields. Unsupported arbitrary fields now fail explicitly.

- Add guarded QEMU disk allocation/attachment/detachment, same-guest QEMU/LXC storage moves and managed LXC mount points. Preview by default; detach/move retain volumes, with source deletion separately gated. Report task, pending configuration and volume inventory evidence independently.

- Add typed QEMU/LXC resource previews and opt-in updates with host capacity checks and current/pending readback, without automatic restart.

- Add QEMU/LXC managed-disk growth previews and opt-in submission with digest/ownership checks, absolute targets, no mutation retries and separate task/capacity/filesystem outcomes.
- Add pending-configuration reads and correct explicit `pending=false` QEMU reads. Add management opt-in settings and restrict legacy configuration bypasses; see [guest management](docs/guest-management.md).

- Add read-only SMART, ZFS pool and managed directory tools, plus storage diagnostics and explicitly scoped backup coverage reports.
- Add task offsets, date/type/VM/status filters and forward log paging. Correct `running=true` to use the active source and filter one bounded page. Preserve upstream totals; reject `running=true` with archived source.
- Correct task-log documentation and enforce explicit defaults/bounds: 50 tasks (maximum 500), 50 log lines (maximum 1,000), 200 syslog lines (maximum 1,000). Zero/unbounded limits are no longer accepted on these tools.
- Add syslog offset/date filters and optional disk discovery SMART/partition controls.
- Bound new diagnostic responses, support cancellation, sanitize diagnostic failures and project aggregate configuration onto safe fields. Omit directory mount options and ticket-login error bodies.
- Add synthetic HTTP and MCP protocol tests plus Windows/Linux pull-request CI. Live PVE and deployment-mode acceptance remains pending and required before stable release; see [verification details](docs/diagnostics.md).
