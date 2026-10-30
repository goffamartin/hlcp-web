# HLCP Web working agreements

This is a personal HomeLab repository, not a HAVIT work repository. Use plain,
descriptive English commit messages. Keep changes as small validated milestones.

- Preserve the Go/CBOR HLCP controller/agent boundary. This app calls its HTTP API.
- Blazor Interactive Server and locally compiled Tailwind; no CDN or hosted Site.
- Never commit real controller tokens, credential files, database data or Data Protection keys.
- No production access or mutations during automated tests. Use synthetic HTTP handlers.
- Never present mock data or stale resource observations as current healthy production data.
- Resource tags filter only; they neither grant permissions nor enable bulk actions.
- Do not implement mutation controls before server-side authorization and stable command-ID handling.
- Keep the bootstrap web host Development-only and loopback-only until authentication is implemented.
- Add frontend styles with complete literal Tailwind class names so source scanning can find them.
- Run restore/build/test/publish plus dependency audits for the milestone; distinguish browser QA
  and real HomeLab acceptance from unit/integration tests.
- Do not add a project license or change repository/site visibility without the owner's choice.

See `docs/implementation-plan.md` for the agreed stages and acceptance gates.
