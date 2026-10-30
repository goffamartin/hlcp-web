# HLCP Web — implementační plán

Stav: 2026-10-30. Samostatný veřejný repozitář `goffamartin/hlcp-web`.
Cíl: malá lokální administrační aplikace nad existujícím HLCP 1.1.0+, nikoli
přepis kontroleru či agentů. Protokol Go/CBOR a jejich autorizační pravidla zůstávají.

## 1. Dohodnutý rozsah

- ASP.NET Core / .NET 10 LTS, Blazor Web App, režim Interactive Server.
- Tailwind CSS 4 sestavovaný lokálně; žádné CDN, externí fonty ani Node.js na serveru.
- Dashboard, uzly, resources, filtry tagů, detail, povolené akce a kontejnerové logy.
- Web přístupný pouze z LAN/WireGuard VPN přes vlastní HTTPS hostname v AdGuard DNS.
- Pozdější samostatný MCP endpoint ve stejném ASP.NET Core hostu.
- České uživatelské rozhraní; názvy API, typů a kódu anglicky.
- Jediné správcovské přihlášení bez Identity účtů, rolí a databáze; potvrzeno vlastníkem.

Mimo první verzi: veřejný hosting/tunel pro správu, správa YAML přes web,
provisioning kontejnerů, libovolné shell příkazy, hromadné akce podle tagů,
HA, scheduler, CPU/RAM grafy bez zdroje dat, monitoring aplikačního zdraví.

## 2. Architektura a hranice

```text
Prohlížeč → HTTPS → Blazor UI ─┐
                             ├→ společné aplikační služby → HTTP API HLCP → agenti
MCP klient → HTTPS → /mcp ────┘         (pozdější etapa)
```

Web je samostatný proces pod neprivilegovaným účtem na VM kontroleru. Výpadek
nebo restart webu nesmí restartovat kontroler, agenty ani aplikace. HLCP API
zůstane na `127.0.0.1:9444`; backend tokeny nikdy neposílá do prohlížeče.
První nasazení bude nativní systemd služba. Kontejner má jiný loopback namespace;
nelze bez návrhu sítě předpokládat, že v něm `127.0.0.1` znamená hostitele.

```text
src/Hlcp.Application/  DTO, typovaný HTTP klient, projekce a aplikační rozhraní
src/Hlcp.Web/          host, Razor komponenty, přihlášení, Tailwind a konfigurace
tests/Hlcp.Tests/      klient, projekce, oprávnění a integrační testy webu
deploy/               příklady systemd/reverse proxy, později instalační návod
docs/                 tento plán, architektura, provoz a smlouvy API
```

Nevytváříme kopii HAVIT enterprise vrstev ani obecný plugin framework. MCP
později použije stejné služby, ne komponenty UI a ne parsování textu CLI.

## 3. Ověřená návaznost na HLCP 1.1.0

Zdroj kontroly: `homelab-control-plane`, tag `v1.1.0`, commit
`0f119514b1739652d709aee7b07db742d44dc0a0`.

| Potřeba webu | Existující rozhraní / omezení |
|---|---|
| Uzly a pozorovaný stav | `GET /v1/nodes`, `GET /v1/nodes/{node}` |
| Resources a tagy | `GET /v1/nodes/{node}/resources?tag=...`, `GET /v1/nodes/{node}/tags` |
| Detail / povolené akce | `GET /v1/nodes/{node}/resources/{resource}`, suffix `/actions` |
| Náhled a spuštění | `POST /v1/nodes/{node}/resolve`, suffix `/commands` |
| Výsledek operace | `GET /v1/nodes/{node}/commands/{id}` — pouze známé ID |
| Logy | suffix `/logs` a `/logs/stream` — historie a NDJSON |
| Diagnostika | Omezený auditní ring, ne úplná historie operací; globální metriky vyžadují all-node scope |

**Chybí endpoint pro výpis všech historických operací.** Nevyrobíme seznam
domnělých výsledků z auditních zpráv. První seznam bude označen „Operace z webu“
a bude obsahovat pouze požadavky přijaté tímto webem, s výsledkem dohledaným v HLCP.
Úplný seznam operací z CLI i webu je případné samostatné rozšíření kontroleru.

Tagy jsou přesné lowercase hodnoty; opakování znamená AND. Klient při filtrování
ověří `X-HLCP-Resource-Tags: 1`, aby starý kontroler nevrátil neomezený seznam.
Žádné globální tagy nebo počty nesmějí obejít scope použitého credentialu.

## 4. Dashboard — pravdivé údaje

1. Souhrn viditelných uzlů: online/degraded/offline, čas posledního úspěšného čtení.
2. Resources: čerstvý running/stopped/error/unknown a samostatně zastaralé údaje.
3. Karty aplikací podle explicitně zvolených aplikačních tagů, např. Kostka,
   Medaile a infrastruktura. `production` není automaticky další aplikace.
4. Upozornění: odpojený uzel, stale pozorování, chyba API, nedořešená vlastní operace.
5. Operace z webu až po implementaci jejich evidence; do té doby poctivý prázdný stav.

Kontejner running **není** aplikační health check. U nedostupného kontroleru
nesmí dashboard ukázat „vše v pořádku“ nebo nuly místo chyby. Zachovaná data
se označí jako stará. Jeden resource může patřit do více karet; celkové počty
počítají unikátní dvojice node/resource, ne součet členství v kartách.

Responzivní layout: kompaktní horní lišta, přehledné stavové karty, seznam potřebující
pozornost a detailní tabulka. Stav vyjádří text i barva, ovládání funguje klávesnicí.
Refresh přibližně po 5 s, bez překrývajících se dotazů; zrušení při opuštění stránky,
omezení frekvence a backoff při chybě/429. Nezavádíme nový SignalR hub na kontroleru.

## 5. Bezpečnost a evidence

- Základ klienta přijme pouze důvěryhodný loopback HTTP endpoint, bez URL credentials,
  query nebo cesty. ID se escapují jako segmenty, filtry jako query hodnoty.
- Žádné redirects, environment proxy ani libovolné URL z prohlížeče; timeouty,
  omezení velikosti odpovědi a bezpečné chybové kódy. Token načítá jen server.
- Produkční web vyžaduje přihlášení i za VPN. Zjednodušený dohodnutý základ: jediné
  správcovské heslo, salted hash v chráněném souboru, cookie a lokální změna hesla.
  Žádné Identity účty, databáze, veřejná registrace nebo viewer/operator role webu.
- Vlastní operační metadata jsou omezená evidence v souborech, ne kopie stavu HLCP
  ani obsah logů. Webové přihlášení a HLCP observer/operator credentials jsou různé věci.
- Cookie Secure/HttpOnly, antiforgery, omezení login pokusů, persistentní Data Protection
  klíče, žádné sdílení admin tokenu přes HTML/JS. Autorizace i v aplikačních službách,
  nikoli pouze skrytím tlačítka. Testy zahrnou anonymní, odvolanou a expirovanou relaci.
- Web dostane vlastní observer credential; operator credential až s mutační etapou.
  HLCP pak audituje identitu gateway; konkrétního webového uživatele eviduje web.
  Nepředstíráme per-user HLCP audit pomocí důvěry v `X-User` hlavičku.
- Akce jen z aktuálního allowlistu resource. Potvrzení ukáže přesný uzel/resource/akci,
  ne pouze text „restart aplikace“. Žádné automatické rozšíření akce na celý tag.
- Stabilní command ID uložit před POST; při reconnectu, timeoutu nebo dvojkliku
  dohledat původní ID, neopakovat požadavek s novým. Chybějící výsledek je neznámý,
  nikoli důkaz selhání nebo rollbacku. Odpojení circuitu nevrací provedenou akci zpět.
- Vlastní operační metadata mají limit 10 000 příprav; plný store odmítá další příjem.
  Automatické mazání/retence zatím není zavedena. Neuchovávat
  tokeny, raw HTTP hlavičky ani potenciálně citlivé výsledkové/logovací texty v auditu.

## 6. Etapy a akceptační brány

Implementováno: P0/P1, místní dashboard s jedním přihlášením (P2) a jednotlivé akce
s evidencí ID (základ P4). Ověření je syntetické; přijetí na skutečném HomeLabu ani
LAN nasazení neproběhlo. P3 zahrnuje samostatnou logovací stránku s historií, filtry,
stránkováním a ručním streamem; obecné detailní stránky, P5 a P6 zbývají.
Viz [místní provoz a přesné limity](local-setup.md).

### P0 — repozitář a sestavitelný základ

Veřejné GitHub repo, README, plán, .gitignore, připnutý stabilní SDK a npm lock,
Blazor Interactive Server, lokální Tailwind build, test project a CI.
Backend knihovna nesmí záviset na Razor komponentách. Žádný cloudový Site hosting.

Hotovo když: čistý clone má popsaný restore/build/test/publish postup, CSS vznikne
z Razor zdrojů, do publish výstupu nejdou Node/tooling/secrets a CI pracuje bez
produkčního kontroleru. První commit nesmí tvrdit, že je hotové přihlášení či LAN nasazení.

### P1 — čtecí klient a model dashboardu

Typované smlouvy HLCP, testovatelný transport, seznam uzlů a resources, přesné
tag filtry, slučování pozorování, stale/offline/unknown stavy a výpočty karet.
Žádný POST pro běžné zobrazení stavu. Syntetická data pouze ve viditelně označeném
Development/demo režimu, nikdy automatický fallback při chybě skutečného API.

Hotovo když: testy řeší prázdnou instalaci, offline uzel s dříve running resource,
duplicitní členství v kartách, neznámý/stale stav, tag AND, nepodporované filtrování,
403, neplatný JSON, přerostlou odpověď, timeout a cancellation. Žádné připojení
k reálnému kontroleru nebo čtení jeho tokenu bez explicitní konfigurace.

### P2 — bezpečný read-only web

Jediné přihlášení, dashboard a stránky uzlů/resources, filtry a pravdivé prázdné/chybové
stavy. Zvlášť ověřit lifecycle Interactive Server/prerender, cancellation a refresh.

Hotovo když: lokální browser QA ověří desktop/mobil/klávesnici a reconnect; nepřihlášený
uživatel ani přímý HTTP požadavek nezíská soukromá data. Bez credentials není demo
zaměněno za provoz. Pokud není dostupná browser QA, bude tato brána označena neověřená.

### P3 — logy a detail

Historie, stránkování, filtry a ručně zapínaný live stream. Logy jsou nedůvěryhodný
text: escapované HTML, žádný MarkupString/ANSI execution. Maximálně 500 řádků nebo
512 KiB v UI bufferu, dávkované vykreslení, cancellation při zavření/navigaci.
Řídit počet streamů podle limitů HLCP; přetížení zobrazit, nikoli tiše dropovat.

Hotovo když: testy rozliší end/keepalive/gap/truncated/limit/forbidden/session loss,
respektují neprůhledný next_cursor a nehlásí uzavření bez korektního end jako úspěch.
Žádný příslib nekonečného bezeztrátového streamu nebo log archivu. Zastavení čtení
nesmí zastavit kontejner; přerušení nemá vést k nekonečné automatické reconnect smyčce.

### P4 — jednotlivé akce a vlastní historie

Platné přihlášení a oddělený HLCP operator credential, resolve preview, potvrzení, přidělení/uložení command ID,
submission a dohledání výsledku. Nabízet jen akce povolené konkrétním resource.
Historie se jasně jmenuje „Operace z webu“, nikoli „všechny operace kontroleru“.

Hotovo když: dvojklik/reconnect/retry stejného ID nezpůsobí novou logickou operaci,
nepřihlášený nemůže mutovat přes UI ani backend, timeout zůstane neznámým výsledkem, restart webu zachová
evidované IDs. Mutační live test jen na vyhrazeném smoke resource; ne na produkčních
aplikacích bez dalšího souhlasu.

### P5 — LAN/VPN nasazení a první webové vydání

Reprodukovatelný publish pro Linux, systemd pod `hlcp-web`, perzistentní data/klíče,
credential files mimo repo, HTTPS reverse proxy/Kestrel, přesný firewall a hostname.
AdGuard pouze privátní DNS; žádný veřejný Cloudflare tunnel pro správu. WireGuard
musí zajistit routu i dosažitelné DNS. DNS záznam není přístupové oprávnění.

Hotovo když: přístup funguje z LAN a VPN, mimo ně ne; HLCP API dál poslouchá jen na
loopbacku; restart webu neovlivní kontroler/kontejnery; logy neobsahují secrets;
je ověřen backup/restore pouze nových webových dat a omezená retence.

### P6 — MCP až následně

Oficiální C# SDK se Streamable HTTP na `/mcp`. Nejprve jen čtení přes samostatnou
identitu: uzly, resources/tagy, detail, známý výsledek a omezený výřez logů.
Samostatná autentizace MCP klienta, nikoli předpoklad browser cookie. Žádný
nekonečný stream jako tool result, libovolný shell, raw proxy nebo parser CLI.
Obsah logů je vstupní data, nikoli instrukce pro agenta.

Hotovo když: nástroje vynucují scope na serveru, mají timeout/limity a nepovolí
mutaci read-only identitě. Lokální/VPN endpoint není automaticky dostupný cloudovému
MCP klientovi; konkrétní klient a autentizační profil musí být předem ověřeny.
Mutační tools nejsou součástí této první MCP etapy.

## 7. Verifikace a postup práce

Každá etapa: malý samostatný commit, místní testy, kontrola rozdílu a CI. Žádný
automatický deploy do HomeLabu při pushi. Release a živé nasazení jsou oddělené kroky.

- Unit: projekce/stavy/počty/filtry a pravidla operací.
- HTTP integrační: syntetický HLCP server, auth, cesty, limity, error/stream scénáře.
- Web integrační: cookies, antiforgery, odvolání relace, přímé neautorizované požadavky.
- Browser: navigace, filtry, přístupnost, log buffer, reconnect a potvrzení akcí.
- CI: locked restore, Release build, test, Tailwind build, publish, kontrola závislostí.
- Provozní přijetí: skutečný kontroler 1.1.0+, read-only zkouška před každou mutací.

## 8. Rozhodnutí před produkčním nasazením

- Přesný LAN hostname, IP, firewall rozsahy a způsob vydávání/obnovování HTTPS certifikátu.
- Konkrétní node/resource scopes obou webových credentials a provozní nastavení hesla.
- Ověření obnovy a údržby souborové evidence operací; případná automatická retence.
- Licence veřejného repozitáře — veřejnost sama není licence; zatím ji nevymýšlíme.
- MCP klient, jeho síťová dosažitelnost a autentizace se rozhodnou před etapou P6.

## Zdroje

- [HLCP 1.1.0 API implementace](https://github.com/goffamartin/homelab-control-plane/blob/v1.1.0/internal/controller/http.go)
- [HLCP logovací smlouva a limity](https://github.com/goffamartin/homelab-control-plane/blob/v1.1.0/docs/reference/56-container-logs.md)
- [HLCP credential scopes](https://github.com/goffamartin/homelab-control-plane/blob/v1.1.0/docs/reference/10-scoped-api-credentials.md)
- [ASP.NET Core Interactive Server](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/render-modes?view=aspnetcore-10.0)
- [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)
- [Tailwind CLI](https://tailwindcss.com/docs/installation/tailwind-cli)
- [MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk)
