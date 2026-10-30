# HLCP API client boundary

`Hlcp.Application` contains HLCP 1.1 inventory DTOs, `HlcpReadClient`, tag validation
and a pure dashboard projection. It does not reference Blazor, invoke the CLI,
read filesystem credentials automatically or start log readers. Explicit preview,
submit and result-lookup methods also use the bounded transport; inventory reads
never submit a command.

The authenticated host supplies `IHlcpTokenSource` from an explicitly selected file.
Real HTTP clients must use `HlcpReadClient.CreateHandler()` (no redirects or
environment proxy). Endpoint validation accepts only a plain loopback HTTP origin,
matching the Go controller's management boundary. The request deadline includes
headers/body and is 5 seconds by default; reads are capped at 2 MiB and JSON depth 16.

Server-side failures expose fixed enum codes, not HTTP bodies, token values or
credential paths. Caller cancellation remains cancellation; a request deadline is
a separate timeout. A successful filtered response must acknowledge tag support
and its returned resources must actually match all requested tags.

Inventory `null` resource arrays are treated as empty, but a malformed/null root
response or missing node contracts is not silently turned into an empty healthy
dashboard. The projection makes offline/degraded/stale observations stale and
missing/unobserved values unknown; running containers are not application health.
Tag filtering uses exact AND semantics and total counts deduplicate node/resource
identities. Tag filters restrict the contributing nodes as well as resources.

`DashboardState` keeps the last successful snapshot, marks it stale after errors
or 20 seconds, and supplies 5–60 second backoff. The UI performs sequential polling
only after Interactive Server connects; it pauses while the circuit is disconnected.
Filtering is local over an already scoped snapshot, including explicit app-tag cards.

`DashboardReader` and `ResourceActions` check the server-side admin session before
every read/mutation. Actions additionally require a fresh visible resource allowlist,
an operator credential, a resolve preview and a persisted create-once submission
marker. Request bodies contain only command ID, resource ID and action. Command
results must match the requested ID and success/code contract; diagnostic message
text is omitted from web metadata. This is not a general HTTP proxy or shell API.

The local host remains Development-only. See [setup](local-setup.md) for exact
credential/operation semantics. Live logs, real HomeLab acceptance and LAN deployment
remain separate milestones.
