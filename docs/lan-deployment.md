# Nasazení: hlcp.home.goffa.cz

Připravený profil: web jako vlastní neprivilegovaná systemd služba na VM kontroleru,
Nginx na 443, Kestrel jen `127.0.0.1:5088`, Go API dál `127.0.0.1:9444`.
Tento návod ani vytvoření balíčku samo nic neinstaluje. Skutečné nasazení dosud
neproběhlo; první kroky provedeme po kontrole aktuálního stavu VM.

## 1. Nejprve zjisti stav

Na **VM kontroleru**, ne na agentovi:

```sh
hostname -I
cat /etc/os-release
uname -m
systemctl is-active nginx caddy || true
sudo ss -lntp '( sport = :80 or sport = :443 or sport = :5088 )'
```

Ověř IP kontroleru a volné porty. Dřívější konfigurace měla 192.168.0.15; neber ji
bez této kontroly jako aktuální. Zjistíme také subnet LAN a zdrojové IP/CIDR příchozího
WireGuard provozu. Neinstaluj druhou proxy na obsazené porty a neměň ostatní aplikace.

## 2. Privátní DNS

V AdGuard Home přidej DNS rewrite:

```text
hlcp.home.goffa.cz → OVĚŘENÁ_IP_KONTROLERU
```

Na klientovi ověř `nslookup hlcp.home.goffa.cz`. DNS klienta i WireGuard připojení
musí používat AdGuard a VPN musí routovat k IP kontroleru. Ve veřejném Cloudflare
nevytvářej A/AAAA záznam ani Tunnel pro tento web. DNS rewrite není přístupové právo.

## 3. Důvěryhodný certifikát bez otevření portů do internetu

Nameservery goffa.cz byly ověřeny jako Cloudflare. Použijeme Let's Encrypt DNS-01:
Certbot dočasně vytvoří TXT `_acme-challenge.hlcp.home.goffa.cz`; web nemusí být
dostupný z internetu. Certifikát/hostname bude veřejně dohledatelný v CT.

V Cloudflare vytvoř API token s `Zone / DNS / Edit` pouze pro zónu `goffa.cz`,
nikoli globální API key. Token zůstává na VM; neposílej jej do chatu nebo Gitu.

Na ověřené čisté VM Debian 12/13:

```sh
sudo apt update
sudo apt install nginx certbot python3-certbot-dns-cloudflare ca-certificates curl
# Pouze pokud je to nová proxy z tohoto návodu:
sudo systemctl disable --now nginx
sudo install -d -m 700 /root/.secrets/certbot
sudo install -m 600 /dev/null /root/.secrets/certbot/cloudflare.ini
sudoedit /root/.secrets/certbot/cloudflare.ini
```

Do souboru vlož `dns_cloudflare_api_token = TVŮJ_TOKEN`. Při opakování nevytvářej
prázdný soubor přes existující credential; otevři stávající přes sudoedit.

```sh
sudo certbot certonly --dns-cloudflare \
  --dns-cloudflare-credentials /root/.secrets/certbot/cloudflare.ini \
  --dns-cloudflare-propagation-seconds 60 \
  -d hlcp.home.goffa.cz
```

Certbot se interaktivně zeptá na kontaktní e-mail a podmínky. Ověř vydání certifikátu
v `/etc/letsencrypt/live/hlcp.home.goffa.cz/`. Není potřeba otevírat porty 80/443
na routeru. Odchozí HTTPS k ACME/Cloudflare musí fungovat.

## 4. Balíček a účet služby

V zeleném CI běhu stáhni artefakt **hlcp-web-linux-x64**, který obsahuje tar.gz a
SHA-256. Jde o kandidáta označeného přesným commitem, ne automatické veřejné vydání.
Na Windows stažený ZIP nejprve rozbal. Na VM kontroleru pak v adresáři balíčku:

```sh
sha256sum -c hlcp-web-*-linux-x64.tar.gz.sha256
```

Pokračuj jen při OK, s ověřeným x86_64 Debianem a verzí odpovídající schválenému CI.
Balíček je self-contained, není potřeba Node, SDK ani systémový .NET runtime.
Stále potřebuje nativní knihovny OS. Podle [oficiálního seznamu .NET](https://github.com/dotnet/core/blob/main/release-notes/10.0/dotnet-dependencies.md)
ověř libc/libgcc/libstdc++/Kerberos/zlib, ICU a OpenSSL; Debian 13 používá libicu76
a libssl3t64, Debian 12 libicu72 a libssl3. Přesný apt příkaz vybereme podle výstupu kroku 1.

Připrav účet `hlcp-web` bez shellu, rootem vlastněné `/opt/hlcp-web/releases/` a
`/etc/hlcp-web/` 0700, dále `/var/lib/hlcp-web/` vlastněné hlcp-web:hlcp-web 0700.
Nevytvářej znovu účet/adresáře naslepo, pokud již existují. Rozbal kandidáta do nové
verzované složky pod releases, zkontroluj BUILD-INFO a obsah app/, a teprve potom
nastav `/opt/hlcp-web/current` na tuto složku. App nesmí být zapisovatelná účtem služby.
Obsah archivu patří do verze, nikoli do `/` nebo přes jinou aplikaci.

## 5. HLCP credentials a heslo

Připravíme vlastní observer credential v aktuální policy Go kontroleru. Nejprve
zkontrolujeme způsob konfigurace a zachováme existující CLI principal i scope.
Nevkládej do policy smyšlené tokeny a nepřepisuj ji celou podle příkladu. Zdrojový
token uloží root do `/etc/hlcp-web/observer.token` s 0600. Systemd jej přes
LoadCredential zpřístupní službě; hodnota tokenu není v prostředí ani příkazové řádce.

Nasazení začne **jen pro čtení**. Pro skutečné ovládání později přidáme oddělený
operator token se záměrně zvoleným scope a soubor `deploy/operator.conf` jako drop-in.
Agentům ani aplikacím se tím nerozšiřují jejich allowlisty.

Správcovské heslo nastav pod účtem webu, skrytým interaktivním vstupem:

```sh
cd /opt/hlcp-web/current/app
sudo -u hlcp-web env ASPNETCORE_ENVIRONMENT=Production \
  Hosting__PublicOrigin=https://hlcp.home.goffa.cz \
  Storage__Directory=/var/lib/hlcp-web \
  /opt/hlcp-web/current/app/Hlcp.Web --set-admin-password
```

Do prostředí patří pouze cesty/veřejná konfigurace, ne heslo. Hash je ve state,
společně s Data Protection klíči a metadaty operací. Klíče nejsou samostatně
šifrované; chrání je soukromý adresář/účet a ochrana disku i zálohy. State nesdílej
s jinými aplikacemi a nemaž jej při upgradu.

## 6. Proxy a služba

V rozbaleném `deploy/nginx.conf` nahraď CLIENT_LAN_CIDR a CLIENT_VPN_CIDR skutečnými
rozsahy. Nehádej je; podle routování může WireGuard přicházet pod LAN IP routeru.
Bez nahrazení konfigurace záměrně neprojde nginx -t. Proxy přijímá jen zvolené
zdrojové adresy, podporuje WebSocket a nebufferuje výpis. Backend nepřijímá
X-Forwarded-Host a věří X-Forwarded-Proto jen od místní IPv4 proxy.

Nainstaluj pouze tuto novou konfiguraci do sites-available/hlcp-web a symlink do
sites-enabled. Výchozí welcome konfiguraci vypni jen u čerstvé instalace z tohoto
návodu; nedotýkej se jiných virtual hosts. Nainstaluj hlcp-web.service do
`/etc/systemd/system/`, zkontroluj soubory a pak:

```sh
sudo nginx -t
sudo systemd-analyze verify /etc/systemd/system/hlcp-web.service
sudo systemctl daemon-reload
sudo systemctl enable --now hlcp-web.service nginx.service
```

Firewall povolí 443 pouze z dohodnutých LAN/VPN rozsahů. SSH a existující
controller/agent porty zůstanou podle dosavadní konfigurace; pravidla neflushujeme.
5088 a 9444 zůstávají pouze na loopbacku. Žádný veřejný port forward ani Tunnel.

## 7. Přijetí, obnova a certifikáty

Z LAN i VPN otevři https://hlcp.home.goffa.cz bez výjimky certifikátu. Ověř přihlášení,
scoped inventář a read-only logy. Ověř odmítnutí z nepovoleného zdroje, skutečné bindy
přes ss a chování po restartu **pouze webu**. Teprve pak přidáme operator credential;
první akci uděláme na smoke resource, ne na produkčních kontejnerech.

Nainstaluj `deploy/renew-nginx.sh` jako rootem vlastněný spustitelný hook do
`/etc/letsencrypt/renewal-hooks/deploy/hlcp-web-nginx`, ověř certbot.timer a proveď
`sudo certbot renew --dry-run`. Samotný dry-run běžně nespouští deploy hook; samostatně
ověř nginx -t a reload. Obnova certifikátu nesmí vyžadovat otevření internetu dovnitř.

Záloha webu: službu zastavit, bezpečně archivovat celý state (hash/keys/operations)
a konfiguraci/credential zdroje zvlášť, uložit šifrovaně mimo VM, spustit službu.
Neobnovovat agentí journal ani controller ledger. Obnovu nejprve ověř na izolované
kopii bez produkčních API tokenů. Při rollbacku binárek zachovat state; nevracet
starší evidenci ID, která by mohla zatajit již odeslanou operaci.

## Zdroje

- [Certbot Cloudflare DNS plugin](https://certbot-dns-cloudflare.readthedocs.io/en/stable/)
- [Let's Encrypt DNS-01](https://letsencrypt.org/docs/challenge-types/#dns-01-challenge)
- [Nginx WebSocket proxy](https://nginx.org/en/docs/http/websocket.html)
- [ASP.NET proxy headers](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0)
- [systemd credentials](https://systemd.io/CREDENTIALS/)
