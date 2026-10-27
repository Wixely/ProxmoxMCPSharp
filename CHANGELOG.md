# Changelog

## v1.2.0-preview.2 - 2026-10-27

- Add opt-in `VerifyCertificateHostname` (default false). With `CaCertificatePath`, certificates from the configured CA are accepted for any host name, as before; when enabled, they must also match the endpoint's host. Default validation and `IgnoreCertificateErrors` are unchanged.
- Add ordered multi-endpoint support (`BaseUrls`; single `BaseUrl` still works). Reads fail over on unreachable, connect-timeout, TLS or authentication failures within one request budget; failed endpoints back off (30s doubling to 240s by default) and become eligible again without a restart. Mutations go to one recently healthy or freshly probed endpoint and are never resent; transport failures report `mutation-not-delivered` or `mutation-indeterminate`. Certificate validation is unchanged for every endpoint. Tool results carry the serving endpoint in `_meta`, `get_api_endpoints` reports and probes endpoint health, and `/healthz` shows endpoint counts. See [endpoint failover](docs/endpoint-failover.md).
- Add `/access` tools: list users, roles, ACL entries, token metadata and effective permissions; preview-by-default creation of password-less users and privilege-separated tokens, explicit ACL assignment/removal, token revocation, dependency-checked user deletion, and a composite `provision_service_identity`. Writes need `ReadOnly=false` and the new `AllowAccessManagement` (default false); revocation, ACL removal and deletion also need `AllowDestroy`. Token secrets are returned once and never logged or listed; existing tokens are never rotated; access-administration/console roles and this server's own identity are refused; mutations are never retried and partial state is reported. See [access management](docs/access-management.md).
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
