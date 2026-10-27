# Access management

Typed tools for the Proxmox `/access` API: users, privilege-separated API tokens,
roles and ACL entries. There is no raw API or shell escape hatch.

## Gates

| Setting | Default | Effect |
| --- | --- | --- |
| `EnableAccess` | `true` | Exposes the read tools and previews. `false` blocks every access tool. |
| `ReadOnly` | `true` | Must be `false` for any submission. |
| `AllowAccessManagement` | `false` | Must be `true` for any identity mutation. |
| `AllowDestroy` | `false` | Additionally required for `remove_access_acl`, `delete_user_token` and `delete_access_user`. |

Every write tool defaults to `dryRun=true`. A preview only reads, reports `blockers`
and `canSubmit`, and shows the role's privileges, so the change can be reviewed
before submission.

The server's own Proxmox identity needs the corresponding upstream privileges
(for example `User.Modify` and `Permissions.Modify`). Without them Proxmox refuses
the request and the step is reported as `rejected` with `permission-denied`.

## Tools

| Tool | Purpose |
| --- | --- |
| `list_access_users` | userid, enable, expire, comment, groups, realm type. No email or names. |
| `list_access_roles` | Roles and privileges. |
| `list_access_acl` | ACL entries, optionally filtered to an exact path and/or user or token. |
| `list_user_tokens` | Token id, comment, expiry and `privsep`. Secrets are never returned. |
| `get_access_permissions` | Effective privileges of a user or token at a path, with non-`.Audit` privileges listed separately. |
| `create_access_user` | Creates an enabled user **without a password**. Refuses existing users. Warns for non-`pve` realms. |
| `create_user_token` | Creates a token with `privsep=1` (always). Refuses existing tokens; never rotates. |
| `set_access_acl` | Grants one explicit role to one user or token at one explicit path. |
| `remove_access_acl` | Removes one existing entry at its exact path. |
| `delete_user_token` | Revokes a token and reports its ACL entries before and after. |
| `delete_access_user` | Deletes a user only once it has no tokens and no ACL entries. |
| `provision_service_identity` | Composite: user, token, and the role for both at one path. |

## Safeguards

- **Secrets.** A token secret appears only in the successful creation response
  (`secret`, with `secretNotice`). It is never logged, listed, included in errors
  or readback. The MCP SDK logs message bodies only at `Trace`; keep the
  `ModelContextProtocol` log level at `Debug` or above.
- **No rotation.** Repeating a create request is blocked when the user or token
  exists. To replace a token, revoke it explicitly and create a new one.
- **Explicit privileges.** Role, path and subject have no defaults. Roles granting
  `Permissions.Modify`, `User.Modify`, `Realm.Allocate`, `Realm.AllocateUser`,
  `Group.Allocate` or `Sys.Console` are never assigned. This server's own user and
  token, and `root@pam`, cannot be changed or deleted through it.
- **Validation.** User ids (`name@realm`), token ids, role ids and ACL paths (`/`,
  `/access`, `/nodes`, `/vms`, `/storage`, `/pool`, `/sdn`, `/mapping` subtrees) are
  validated before any request. Unknown realms, users, tokens and roles are blockers.
- **No retries.** Each mutation is sent once. HTTP 4xx is reported as `rejected`;
  transport failures, timeouts and 5xx are `unknown`. Readback after submission
  shows the observed state to reconcile against. If a token creation is `unknown`
  but the token exists, its secret is lost: revoke it and create a new one.
- **Partial state.** The composite reports each step as `completed`, `rejected`,
  `unknown` or `not-attempted`, with `outcome` `partial` when some steps
  completed. Completed steps are not rolled back.

## Privilege separation

A privilege-separated token receives the intersection of its own ACLs and its
user's. `provision_service_identity` therefore grants the role to both the user
and the token in one ACL update. It requires a `pve` realm so the user has no
password login. `set_access_acl` warns when a token is granted a role its user
does not hold on that path or a propagating ancestor.

## Example: read-only monitoring identity

Preview, then submit:

```json
{ "name": "provision_service_identity",
  "arguments": { "userId": "shinobi@pve", "tokenId": "monitor", "roleId": "PVEAuditor", "path": "/" } }
```

```json
{ "name": "provision_service_identity",
  "arguments": { "userId": "shinobi@pve", "tokenId": "monitor", "roleId": "PVEAuditor", "path": "/", "dryRun": false } }
```

The response carries `tokenId` (`shinobi@pve!monitor`), the one-time `secret`, the
step outcomes and `readback.tokenEffectivePermissions`. For `PVEAuditor` at `/` the
`nonAuditPrivileges` list is empty. Its audit privileges (`Sys.Audit`, `VM.Audit`,
`Datastore.Audit` and others) are what Proxmox documents for `GET /version`,
`GET /nodes`, `GET /cluster/resources` and `GET /nodes/{node}/apt/update`. Refreshing
APT, changing guests or administering access need non-audit privileges the token
does not hold. The `/access` configuration is cluster-wide, so the token works
against any node.

## Verification limits

Contract tests use an in-memory model of the `/access` API: provisioning,
duplicates, invalid inputs, escalating and unknown roles, self-identity
protection, upstream permission denial, partial failure, lost responses and
secret redaction. Live acceptance against a PVE cluster, including the
token's read and denied-write behaviour across nodes, has not been run yet.
