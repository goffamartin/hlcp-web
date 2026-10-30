# Místní spuštění a přístup

Web zatím vynucuje Development a `http://127.0.0.1:5088`. Je určený pro místní
vývoj, nikoli publikování na LAN. Produkční HTTPS, systemd a DNS přijdou zvlášť.

## Jedno přihlášení

Po sestavení spusť v interaktivním terminálu:

```sh
dotnet run --project src/Hlcp.Web -c Release --no-build --launch-profile http -- --set-admin-password
```

Příkaz skrytě vyžádá heslo dvakrát, minimálně 16 znaků. Uloží jen salted hash pomocí
frameworkového `PasswordHasher`; nezavádí Identity účty, databázi ani role. Při chybě
vstupu nic nezmění. Přepsání hashe odvolá existující přihlášení při příští kontrole.
Bez uloženého hashe se nikdo nepřihlásí. Není registrace ani přístup bez hesla.

Cookie je HttpOnly, SameSite Strict, nejvýše 8 hodin, bez prodlužování a bez trvalého
„zapamatovat“. HTTPS nastaví Secure; výjimka HTTP platí jen pro tento vynucený místní
vývoj. Přihlášení/odhlášení mají antiforgery. Limit přihlašovací stránky je globálně
10 požadavků/minutu, včetně GET; odpověď 429 vyžaduje minutovou pauzu. Nejvýše 32
současných relací, restart webu je zruší. Odhlášení odvolá danou relaci; jiná otevřená
záložka přestane číst při dalším obnovení. Aktivní Blazor circuit ověřuje relaci také
každou minutu. Změna hesla nevrátí již zahájenou akci zpět.

Úložiště lze nastavit `Storage__Directory` (absolutní cesta). Volitelné
`Admin__PasswordFile` určuje cestu k hashi. Výchozí adresář je `.local` v content root
webu; obsahuje hash, Data Protection klíče a `operations/`. Nejde do Gitu ani publish.
Na Windows chraň adresář ACL, na Linuxu nastav vlastnictví a adresář 0700 před nasazením.
Nové hash/operation soubory se na Unixu vytvářejí 0600. Vývojové Data Protection klíče
zatím nejsou šifrované v úložišti; produkční ochrana klíčů je součástí nasazovací etapy.

## Kontroler a ovládání

Konfigurace přes proměnné prostředí (shell syntax uprav podle svého prostředí):

```sh
export Hlcp__Endpoint=http://127.0.0.1:9444
export Hlcp__TokenFile=/absolute/private/path/web-observer.token
export Hlcp__OperatorTokenFile=/absolute/private/path/web-operator.token
export Dashboard__ApplicationTags__0=kostka
export Dashboard__ApplicationTags__1=medaile
export Dashboard__ApplicationTags__2=infrastructure
dotnet run --project src/Hlcp.Web -c Release --no-build --launch-profile http
```

Jde o cesty k souborům, ne hodnoty tokenů. Nikdy nepoužívej produkční tokeny při
automatických testech. Credentialy vytvoř samostatně v policy kontroleru s odpovídajícím
scope; tato aplikace policy ani tokeny nevytváří a neupravuje. Endpoint přijímá pouze
loopback HTTP origin, bez redirects/proxy. Pro skutečný kontroler poběží web na stejné
VM; loopback neznamená jiný stroj ani hostitele z kontejneru.

Observer slouží pro dashboard a ověření viditelného allowlistu. Operator se používá
pro preview, spuštění a lookup vlastních operací. Bez OperatorTokenFile jsou ovládací
prvky nedostupné. Akce musí projít allowlistem observeru i oprávněním operatoru v HLCP.
Inventářová akce `logs` se nikdy neposílá jako COMMAND; logovací UI bude samostatně.
Jediné webové přihlášení má přístup ke všem takto nakonfigurovaným resources.

Vyber resource a akci. Náhled přes `/resolve` nic neprovádí; následné potvrzení ukáže
přesný uzel, resource, akci a ID. Před POST se znovu ověří allowlist a preview. Potvrzení
platí 5 minut. Není hromadná akce podle tagu ani libovolný shell.

## Operace a nejisté výsledky

Web nejprve uloží metadata s unikátním ID, potom create-once marker před POST. Dvojí
potvrzení stejného ID může jen dohledávat, nikoli znovu poslat COMMAND. Po timeoutu,
přerušení circuitu či restartu zůstává výsledek neznámý, dokud se nedohledá přes stejné
ID. Ani 404 není důkaz neprovedení. Web nic automaticky neopakuje. Nové ruční spuštění
je nová operace; při nejistém výsledku ji nevytvářej, nejprve dohledávej původní.

„Operace z webu“ obsahují posledních 50 zde připravených operací, nikoli úplnou historii
kontroleru. Metadata jsou soubory, ne databáze; ukládají cíl, akci, ID, čas a terminální
success/code. Neuchovávají text výsledku, logy ani tokeny. Limit 10 000 příprav uzavře
další příjem místo mazání nevyřešených ID. Automatické promazávání zatím není. Úložiště
je určeno jednomu procesu; HA, obnova po výpadku napájení a backup/restore ještě nejsou
ověřené. Stav „připraveno“ může zahrnovat zrušený nebo odmítnutý preview; neznamená odeslání.

Dashboard obnovuje data po 5 s, bez překrývání, s backoff do 60 s při chybě. Odpojený
circuit další čtení pozastaví. Starší snímek zůstane viditelný jako zastaralý; aktuální
počty se nenahrazují nulami. Filtry pracují nad již autorizovaným snímkem, nevytvářejí
nové oprávnění. Tabulka zobrazuje nejvýše 200 řádků; pro větší seznam použij filtry.

## Co zbývá před LAN nasazením

HTTPS hostname a certifikát, firewall/LAN/VPN kontrola, systemd pod neprivilegovaným
účtem, ochrana adresářů/klíčů a ověřená obnova webových dat. Potom samostatné read-only
přijetí na skutečném kontroleru. První akci testuj na vyhrazeném smoke resource, ne
restartem produkčních aplikací. Go kontroler a agenti nejsou tímto milestone změněni.
