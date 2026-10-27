# Endpoint failover

One MCP server can use several API endpoints of the same Proxmox cluster, so it stays
available while a node is offline or in maintenance.

## Configuration

```json
"Proxmox": {
  "BaseUrls": [ "https://pve1.example.com:8006/", "https://pve2.example.com:8006/" ],
  "EndpointRetryAfterSeconds": 30,
  "EndpointConnectTimeoutSeconds": 5
}
```

| Setting | Default | Meaning |
| --- | --- | --- |
| `BaseUrls` | empty | Endpoints in preference order. When empty, `BaseUrl` is used alone, as before. |
| `EndpointRetryAfterSeconds` | 30 | How long a failed endpoint is skipped. Doubles on consecutive failures, capped at 8x (240s by default). |
| `EndpointConnectTimeoutSeconds` | 5 | Connect/TLS timeout per attempt, bounding the delay an offline node adds. |

Each entry must be an absolute URL without credentials, query or fragment. Duplicates are
ignored. Invalid entries stop startup.

All endpoints must belong to one cluster. Endpoint selection is transport only; `AllowedNodes`
and `BlockedNodes` still decide which nodes' resources may be used.

## Behaviour

**Reads** (GET) use the first endpoint that is not cooling down. They move to the next one
only for:

| Failure | Meaning |
| --- | --- |
| `unreachable` | Connection refused or name not resolved. |
| `connect-timeout` | No connection within `EndpointConnectTimeoutSeconds`. On Windows a refused loopback port also appears here. |
| `tls-failure` | Certificate rejected or handshake failed. |
| `authentication-rejected` | HTTP 401. With a cluster-wide token or ticket, this means the node disagrees about credentials, for example because it belongs to another cluster. |
| `transport-failure` | The connection broke during the exchange. |

Any other HTTP status is a real answer from a working node and is returned unchanged. All
attempts share the existing `RequestTimeoutSeconds` budget. If the budget runs out or the
caller cancels, the request stops and no endpoint is marked as failed.

A failed endpoint is skipped until its cooldown expires. It then becomes eligible again
without a restart, and the preferred endpoint is used again once it answers. When every
endpoint is cooling down, they are still tried in order rather than failing immediately.
When none answers, the error lists each endpoint and its failure
(`all-endpoints-unavailable`).

**Mutations** (POST, PUT, DELETE) are sent exactly once, to one endpoint:

- The first eligible endpoint is used if it answered within the last
  `EndpointRetryAfterSeconds`. Otherwise an authenticated `GET /version` with read failover
  selects one first.
- A transport failure is never resent to another endpoint. The error is
  `mutation-not-delivered` when the request cannot have reached Proxmox (unreachable,
  connect timeout, TLS failure), and `mutation-indeterminate` otherwise. Guarded management
  tools report the latter as an `unknown` outcome. Inspect tasks and current state before
  retrying.

**Authentication.** API tokens and PVE tickets are valid across a cluster, so one token or
ticket is shared by all endpoints. Ticket login uses read failover, because it creates no
cluster state.

**TLS.** Every endpoint uses the same certificate policy: normal validation,
`CaCertificatePath` (optionally with `VerifyCertificateHostname`), or `IgnoreCertificateErrors`.
Failover never relaxes it. With `VerifyCertificateHostname`, each endpoint's certificate must
match that endpoint's own host.

## Observability

- Every MCP tool result carries `_meta["proxmox/endpoints"]`, the endpoints that served it.
  When endpoints were skipped, it also carries `_meta["proxmox/failover"]` as
  `[{ endpoint, failure }]`.
- `get_api_endpoints` lists endpoints in order with state (`unused`, `available`,
  `cooling-down`, `eligible-for-retry`), last failure, last success and retry time.
  `probe=true` checks each endpoint independently now, which also clears a recovered
  endpoint's cooldown.
- `/healthz` (unauthenticated) shows only the configured and cooling-down counts.
- The log records one warning per endpoint failure and one entry when an endpoint recovers.

## Verification limits

Contract tests cover:

- endpoint ordering;
- each failure kind;
- cooldown expiry and recovery of the preferred endpoint;
- bounded backoff;
- all endpoints down;
- the request budget and caller cancellation;
- mutation selection and no-replay;
- shared tickets;
- `_meta` over MCP.

Against real sockets, the classification of a closed port, an unroutable address (bounded by
the connect timeout) and an untrusted TLS certificate was checked. Failover against a live
two-node PVE cluster has not been run yet.
