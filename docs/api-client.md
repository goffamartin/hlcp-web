# Read-only client boundary

`Hlcp.Application` contains HLCP 1.1 inventory DTOs, `HlcpReadClient`, tag validation
and a pure dashboard projection. It does not reference Blazor, invoke the CLI,
submit commands, read filesystem credentials automatically or start log readers.

The host will supply `IHlcpTokenSource` after credential and authentication setup.
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

This is the first P1 slice, not the finished application authorization layer.
Group cards, refresh coordination, credential-file providers, authenticated host
integration, mutation tracking and live logs remain in later milestones.
