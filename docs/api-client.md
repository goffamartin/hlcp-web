# HLCP API client boundary

`Hlcp.Application` contains HLCP 1.1 inventory DTOs, `HlcpReadClient`, tag validation
and a pure dashboard projection. It does not reference Blazor, invoke the CLI,
read filesystem credentials automatically. Explicit preview,
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

`HlcpLogClient` uses only GET with the observer credential. History pages have a
256 KiB transport cap, typed schema/record validation and opaque cursors. NDJSON
validates schema/semantics, one request ID, contiguous agent sequences and explicit
end events; controller keepalives and interruption ends use sequence zero. EOF without
end is incomplete. Lines are bounded to 64 KiB, live responses to 8 MiB, idle reads to
15 seconds and total follow duration to 10 minutes + 15 seconds. No automatic retry.

`LogReader` checks the admin session and fresh native-container allowlist, shares a
two-read process budget and rechecks session validity every 3 seconds even when idle.
Circuit disconnect cancels the read rather than silently resuming on reconnect.
The UI keeps up to 500 records / 512 KiB of accounted text/record memory and batches
renders at most four times per second. It renders escaped text, not HTML/ANSI.
Log bodies never enter the operation journal or a server-side archive.

The backend remains loopback-only. Production requires the explicit HTTPS proxy profile.
See [setup](local-setup.md) for exact
credential/operation/log semantics. Real HomeLab acceptance and LAN deployment
remain separate milestones.
