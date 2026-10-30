# HLCP Web

A LAN-first dashboard for [HLCP](https://github.com/goffamartin/homelab-control-plane),
built with ASP.NET Core Blazor Interactive Server and locally compiled Tailwind CSS.
A future MCP endpoint will share the application services, not the UI components.

## Status

Bootstrap in progress. This is not a deployed or production-ready management UI.
HLCP controller/agents remain in Go/CBOR; this repository is a separate client.
The initial slice is read-only. Authentication, actions, live logs and MCP are
separate planned stages, not implied by the project scaffolding.

See the [implementation plan (Czech)](docs/implementation-plan.md) for architecture,
milestones, acceptance checks and explicit deployment decisions.

The intended production host is the controller VM, with HLCP's HTTP API still on
loopback. No public tunnel or hosted Site is created. API credentials stay on the
server, outside source control; LAN/VPN reachability does not replace authentication.

## License

A project license has not yet been selected. Public repository visibility is not
a grant of an open-source license.
