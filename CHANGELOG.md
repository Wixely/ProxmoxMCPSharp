# Changelog

## Unreleased

- Add read-only SMART, ZFS pool and managed directory tools, plus storage diagnostics and explicitly scoped backup coverage reports.
- Add task offsets, date/type/VM/status filters and forward log paging. Correct `running=true` to use the active source and filter one bounded page. Preserve upstream totals; reject `running=true` with archived source.
- Correct task-log documentation and enforce explicit defaults/bounds: 50 tasks (maximum 500), 50 log lines (maximum 1,000), 200 syslog lines (maximum 1,000). Zero/unbounded limits are no longer accepted on these tools.
- Add syslog offset/date filters and optional disk discovery SMART/partition controls.
- Bound new diagnostic responses, support cancellation, sanitize diagnostic failures and project aggregate configuration onto safe fields. Omit directory mount options and ticket-login error bodies.
- Add synthetic HTTP and MCP protocol tests plus Windows/Linux pull-request CI. Live PVE and deployment-mode acceptance remains required before release; see [verification details](docs/diagnostics.md).
