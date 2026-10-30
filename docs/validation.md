# Ověření místního ovládacího milníku

2026-10-30, .NET SDK 10.0.403 / runtime 10.0.14, Windows x64.

- Locked restore, Release build bez warnings, 61 úspěšných testů, publish.
- NuGet audit všech projektů i `npm audit`: bez nalezených zranitelností.
- Publish obsahuje lokální CSS a dialogový modul; neobsahuje `.local`, credentials,
  secrets, soukromé appsettings ani Node/tooling.
- Web integrační testy: anonymní přístup, cookie, antiforgery login/logout, změna hesla,
  expirovaná/odvolaná relace, rate limit, odmítnutí cizího Origin u Blazor spojení.
- Syntetické akce: allowlist při přípravě i potvrzení, chybějící operator credential,
  dvojklik, ztracená odpověď, metadata po nové instanci journalu, chyba zápisu bez POST,
  validace výsledkového ID a success/code. Žádný test nespouští kontejner nebo shell.
- Browser QA proti oddělenému syntetickému API: přihlášení a interaktivní dashboard,
  dva tagy s AND, nativní potvrzovací dialog, Escape/zrušení, potvrzený restart testovacího
  resource s právě jedním POST, výsledek v historii, mobil 390 px bez vodorovného
  přetečení celé stránky, odhlášení a odmítnutí návratu na chráněnou stránku.
- Po vypnutí syntetického API zůstaly starší resources označené jako zastaralé,
  souhrnné aktuální počty se zobrazily jako „—“ a ovládání bylo nedostupné.
- Pauza/obnovení polling smyčky při ztrátě circuitu je ověřena unit testem; plný
  browser reconnect po výpadku sítě ještě není samostatně provozně přijatý.

Neověřeno: skutečný HLCP, scope produkčních tokenů, LAN/VPN/HTTPS, systemd, backup/
restore a výpadek napájení. Žádná produkční aplikace nebyla restartována. Logovací UI,
MCP, samostatné detailní stránky a produkční nasazení nejsou součástí tohoto milníku.
