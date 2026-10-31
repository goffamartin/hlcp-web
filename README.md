# HLCP Web

A LAN-first dashboard for [HLCP](https://github.com/goffamartin/homelab-control-plane),
built with ASP.NET Core Blazor Interactive Server and locally compiled Tailwind CSS.
A future MCP endpoint will share the application services, not the UI components.

## Status

The owner has confirmed HTTPS access and container log reads on the HomeLab deployment.
HLCP controller/agents remain in Go/CBOR; this repository is a separate client.
The UI supports one admin login, a refreshing dashboard, node/tag filters, linked node
and resource detail pages, individual allowlisted actions, command output and
web-originated operation history/result lookup, and filtered container log history/live streaming. No Identity
account system, roles or database is required. MCP remains planned. Production requires
an explicit canonical HTTPS origin, private external state and a trusted local reverse
proxy; the backend always stays on loopback. Full VPN and backup/restore acceptance
remain pending. See [validation](docs/validation.md) for the scope of each check.

Controller access requires explicitly configured server-side token files. Without
them the dashboard displays a connection/configuration error, never demo data.
Automated tests and browser QA use synthetic APIs only. Newly added detail pages
and action-output rendering require an updated web package on the target VM.

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
dotnet run --project src/Hlcp.Web -c Release --no-build --launch-profile http -- --set-admin-password
dotnet run --project src/Hlcp.Web -c Release --no-build --launch-profile http
```

Open `http://127.0.0.1:5088`. The host is forced to loopback during this bootstrap
stage. Set the admin password at the hidden interactive prompt; never pass it in
arguments or environment variables. The hash, keys and operation metadata default
to `src/Hlcp.Web/.local/` and are excluded from Git and publish. Stop the host with
Ctrl+C. If a project-local SDK is used, put its directory on
PATH and set DOTNET_ROOT appropriately; `.tools/` is ignored and not distributed.

Tailwind CSS 4.3.3 is compiled from Razor sources before web builds. To watch styles
while editing, run `npm run watch:css` separately. Generated CSS is ignored in Git
but included in publish output; Node/npm and external CDNs are not runtime dependencies.

```sh
dotnet publish src/Hlcp.Web -c Release --no-restore -p:PublishDir=../../artifacts/publish/
npm audit --audit-level=high
dotnet list Hlcp.Web.slnx package --vulnerable --include-transitive
```

Publish is a packaging check, not a LAN deployment. See [local setup and access](docs/local-setup.md)
for credentials, action semantics and the remaining production requirements.
The [API client boundary](docs/api-client.md) and tests use only synthetic data.
CI never needs HomeLab credentials.
The project-relative publish property also avoids MSBuild command-line parsing
issues with spaces/commas in a Windows checkout path.

## LAN deployment candidate

[Deployment guide (Czech)](docs/lan-deployment.md): `hlcp.home.goffa.cz`, AdGuard DNS,
Nginx, Certbot Cloudflare DNS-01 and a separate systemd account. No public tunnel or
router port forwarding is required. Review the VM, IP ranges and existing services
before installing anything; the guide keeps controller credentials and state separate.

CI builds a self-contained `linux-x64` candidate, checks startup/proxy restrictions
without controller credentials and uploads the archive/checksum as the seven-day
`hlcp-web-linux-x64` artifact. This is not an automatic release or deployment.
On a clean Linux checkout, `sh scripts/package-linux.sh` produces the same artifact
layout. The target needs native OS dependencies, but no SDK, Node or system .NET.

## License

A project license has not yet been selected. Public repository visibility is not
a grant of an open-source license.
