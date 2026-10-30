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

## Navazující milník: logovací UI

2026-10-30, stejné SDK/runtime. Celkem 104 úspěšných testů.

- Typed history a GET-only NDJSON, přesné filtry a neprůhledné cursory; HTTP chyby
  včetně 410/429 bez výpisu odpovědí, tokenů a soukromých cest.
- Validace schématu, významu proudů, ID a sekvence; keepalive, korektní end, gap,
  truncation, předčasné EOF, změněné ID, neplatný JSON a nedovolené pořadí.
- Omezený řádek a kumulativní stream, historické tělo bez Content-Length, idle timeout,
  odlišné ruční cancellation, zavření response body a omezený UI buffer.
- Anonymní přístup bez kontaktu s kontrolerem; observer bez operator tokenu, fresh
  allowlist, procesní budget, uvolnění slotů a rušení při výpadku circuitu. Změna hesla
  zruší i idle stream, aniž čeká na další záznam. Čtení nevytváří command/operation.
- Obyčejná task/script akce pojmenovaná logs zůstává běžnou akcí; nativní logovací
  čtení se zpřístupňuje pouze kontejneru s povolenou akcí logs.
- Browser QA proti syntetickému API: historie a druhá stránka téhož snímku, live
  výpis, ruční stop a uzavření odběru při navigaci, EOF bez end jako přerušení,
  BYTE_LIMIT/gap jako neúplné doručení, desktop 1440 px a mobil 390 px bez přetečení.
  HTML payloady zůstaly inertním textem (žádný vložený img/script ani spuštěný kód),
  řídicí znaky se nespustily. Konzole prohlížeče bez errors/warnings.

## Příprava LAN nasazení

2026-10-30: lokálně 116 úspěšných testů, locked restore, Release build bez warnings,
portable publish i self-contained Linux x64 cross-publish. Audity NuGet/npm bez
nalezených zranitelností. Nové testy odmítají neplatný produkční origin, zápis state
do app adresáře, cizí proxy, chybějící HTTPS a změněný Host. Backend se nevystavuje
na LAN; proxy profil přijímá forwarded headers jen od místního IPv4 peeru.

Workflow navíc sestavuje Linux archiv s kontrolním součtem a provádí native smoke:
spuštění bez systémového .NET a bez controller credentials, HTTPS požadavek přes
reálný Nginx s jednorázovým testovacím certifikátem, WebSocket upgrade 101, source-IP
ACL, Secure antiforgery cookie, anonymní API 401 a offline validaci systemd unit.
Výsledek konkrétního běhu je v jeho GitHub Checks; tato sada nenahrazuje provozní
přijetí na cílové VM. Nic se z workflow nenasazuje.

V rámci této syntetické sady neověřeno: skutečný HLCP, scope produkčních tokenů, skutečné LAN/VPN/HTTPS, běh
systemd služby na cílové VM, backup/restore a výpadek napájení. Žádná produkční
aplikace nebyla restartována.

## Detaily uzlů/resources a výstupy akcí

2026-10-30: 129 úspěšných testů, locked restore a Release build bez warnings.
Audity npm a NuGet bez nalezených zranitelností.

- Testy ověřují předání úspěšného i chybového `message`, UTF-8 limit 4096 bajtů,
  prázdný výstup, odmítnutí chybějícího výstupu v API a čtení starých metadat bez něj.
- Výstup zůstává v paměti; journal neobsahuje jeho text. Známý výsledek lze načíst
  observer credentialem i po vypnutí operator konfigurace. Filtrování historie
  na uzel/resource předchází limitu posledních 50 položek.
- Read-only status se před odesláním znovu ověřuje. Změněný mutační preview se odmítne
  bez POST; vlastní task/script status vyžaduje potvrzení.
- Browser QA na syntetickém loopback API: dashboard → uzly → uzel → resource → logy
  a zpět, přechod mezi resources stejné routy bez starého výstupu, okamžitý status,
  načtení výstupu z historie, zrušený script status a potvrzená syntetická restart akce.
  JSON byl odsazený, HTML inertní; desktop a mobil 390 px bez přetečení stránky,
  konzole bez errors/warnings. Testovací restart neposílal příkaz do HomeLabu.

## Průběžné potvrzení vlastníka na HomeLabu

Vlastník potvrdil funkční HTTPS přístup na `hlcp.home.goffa.cz` a čtení kontejnerových
logů přes nasazenou předchozí verzi. Nové detailní stránky a výstupy akcí tímto ještě
nejsou živě ověřené. VPN, backup/restore a výpadek napájení zůstávají nepřijaté;
MCP zatím není implementovaný.
