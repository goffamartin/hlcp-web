# HLCP Web

A LAN-first dashboard for [HLCP](https://github.com/goffamartin/homelab-control-plane),
built with ASP.NET Core Blazor Interactive Server and locally compiled Tailwind CSS.
A future MCP endpoint will share the application services, not the UI components.

## Status

Bootstrap implemented. This is not a deployed or production-ready management UI.
HLCP controller/agents remain in Go/CBOR; this repository is a separate client.
The initial slice is read-only. Authentication, actions, live logs and MCP are
separate planned stages, not implied by the project scaffolding.

The local preview is an explicitly unconnected dashboard shell, not fake production
data. The read-only library and its projection are tested separately; the UI does
not load a controller token or make production requests yet. Non-Development startup
is intentionally refused until authentication is implemented.

See the [implementation plan (Czech)](docs/implementation-plan.md) for architecture,
milestones, acceptance checks and explicit deployment decisions.

The intended production host is the controller VM, with HLCP's HTTP API still on
loopback. No public tunnel or hosted Site is created. API credentials stay on the
server, outside source control; LAN/VPN reachability does not replace authentication.

## Local development

Install the stable .NET SDK selected by `global.json` (10.0.403, runtime 10.0.14)
and Node.js 24 with npm. No prerelease SDK is selected. Then:

```sh
npm ci
dotnet restore Hlcp.Web.slnx --locked-mode
dotnet build Hlcp.Web.slnx -c Release --no-restore
dotnet test Hlcp.Web.slnx -c Release --no-build --no-restore
dotnet run --project src/Hlcp.Web -c Release --no-build --launch-profile http
```

Open `http://127.0.0.1:5088`. The host is forced to loopback during this bootstrap
stage. Stop it with Ctrl+C. If a project-local SDK is used, put its directory on
PATH and set DOTNET_ROOT appropriately; `.tools/` is ignored and not distributed.

Tailwind CSS 4.3.3 is compiled from Razor sources before web builds. To watch styles
while editing, run `npm run watch:css` separately. Generated CSS is ignored in Git
but included in publish output; Node/npm and external CDNs are not runtime dependencies.

```sh
dotnet publish src/Hlcp.Web -c Release --no-restore -p:PublishDir=../../artifacts/publish/
npm audit --audit-level=high
dotnet list Hlcp.Web.slnx package --vulnerable --include-transitive
```

Publish is a packaging check, not authorization to deploy this unauthenticated
bootstrap on LAN. The initial [API client boundary](docs/api-client.md) and
dashboard-state tests use only synthetic data. CI never needs HomeLab credentials.
The project-relative publish property also avoids MSBuild command-line parsing
issues with spaces/commas in a Windows checkout path.

## License

A project license has not yet been selected. Public repository visibility is not
a grant of an open-source license.
