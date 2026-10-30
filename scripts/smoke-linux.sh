#!/bin/sh
set -eu
[ "$#" -eq 1 ] || { echo 'Usage: smoke-linux.sh PACKAGE.tar.gz' >&2; exit 2; }
archive=$(realpath "$1")
stage=$(mktemp -d)
pid=''
nginx_started=false
cleanup() {
  if [ "$nginx_started" = true ]; then nginx -e "$stage/nginx-error.log" -p "$stage/" -c "$stage/nginx.conf" -s quit 2>/dev/null || true; fi
  if [ -n "$pid" ]; then kill "$pid" 2>/dev/null || true; wait "$pid" 2>/dev/null || true; fi
  rm -rf -- "$stage"
}
trap cleanup EXIT HUP INT TERM
tar -xzf "$archive" -C "$stage"
mkdir -m 700 "$stage/state"
(
  cd "$stage/app"
  exec env ASPNETCORE_ENVIRONMENT=Production Hosting__PublicOrigin=https://hlcp.home.goffa.cz \
    Storage__Directory="$stage/state" ./Hlcp.Web > "$stage/server.log" 2>&1
) &
pid=$!
tries=0
while :; do
  code=$(curl -s -o /dev/null -w '%{http_code}' -H 'Host: hlcp.home.goffa.cz' -H 'X-Forwarded-Proto: https' http://127.0.0.1:5088/api/dashboard || true)
  [ "$code" = 401 ] && break
  tries=$((tries + 1))
  [ "$tries" -lt 50 ] && kill -0 "$pid" 2>/dev/null || { cat "$stage/server.log" >&2; exit 1; }
  sleep 0.2
done
[ "$(curl -s -o /dev/null -w '%{http_code}' -H 'Host: hlcp.home.goffa.cz' http://127.0.0.1:5088/account/login)" = 400 ]
curl --fail -s -D "$stage/headers" -o "$stage/login.html" -H 'Host: hlcp.home.goffa.cz' -H 'X-Forwarded-Proto: https' http://127.0.0.1:5088/account/login
grep -q '__RequestVerificationToken' "$stage/login.html"
grep -i '^Set-Cookie:' "$stage/headers" | grep -qi 'secure'
# Verify the actual proxy template with a disposable certificate and high loopback port.
openssl req -x509 -newkey rsa:2048 -nodes -keyout "$stage/test.key" -out "$stage/test.crt" \
  -days 1 -subj '/CN=hlcp.home.goffa.cz' -addext 'subjectAltName=DNS:hlcp.home.goffa.cz' >/dev/null 2>&1
sed -e 's/CLIENT_LAN_CIDR/192.0.2.0\/24/' -e 's/CLIENT_VPN_CIDR/198.51.100.0\/24/' \
  -e 's/listen 443 ssl;/listen 127.0.0.1:18443 ssl;/' \
  -e "s|/etc/letsencrypt/live/hlcp.home.goffa.cz/fullchain.pem|$stage/test.crt|" \
  -e "s|/etc/letsencrypt/live/hlcp.home.goffa.cz/privkey.pem|$stage/test.key|" \
  "$stage/deploy/nginx.conf" > "$stage/site.conf"
cat > "$stage/nginx.conf" <<EOF
worker_processes 1;
pid $stage/nginx.pid;
error_log $stage/nginx-error.log;
events { worker_connections 128; }
http {
  client_body_temp_path $stage/body;
  proxy_temp_path $stage/proxy;
  include $stage/site.conf;
}
EOF
nginx -e "$stage/nginx-error.log" -p "$stage/" -c "$stage/nginx.conf" -t
nginx -e "$stage/nginx-error.log" -p "$stage/" -c "$stage/nginx.conf"
nginx_started=true
url=https://hlcp.home.goffa.cz:18443
curl --noproxy '*' --fail -s --resolve hlcp.home.goffa.cz:18443:127.0.0.1 --cacert "$stage/test.crt" "$url/account/login" > "$stage/tls-login.html"
grep -q '__RequestVerificationToken' "$stage/tls-login.html"
[ "$(curl --noproxy '*' -s -o /dev/null -w '%{http_code}' --interface 127.0.0.2 --resolve hlcp.home.goffa.cz:18443:127.0.0.1 --cacert "$stage/test.crt" "$url/account/login")" = 403 ]
curl --noproxy '*' --fail -s --resolve hlcp.home.goffa.cz:18443:127.0.0.1 --cacert "$stage/test.crt" -X POST \
  -H 'Origin: https://hlcp.home.goffa.cz' "$url/_blazor/negotiate?negotiateVersion=1" > "$stage/negotiate.json"
token=$(node -e 'const fs=require("fs");process.stdout.write(encodeURIComponent(JSON.parse(fs.readFileSync(process.argv[1],"utf8")).connectionToken))' "$stage/negotiate.json")
curl --noproxy '*' -s --http1.1 --max-time 2 --resolve hlcp.home.goffa.cz:18443:127.0.0.1 --cacert "$stage/test.crt" \
  -H 'Origin: https://hlcp.home.goffa.cz' -H 'Connection: Upgrade' -H 'Upgrade: websocket' \
  -H 'Sec-WebSocket-Version: 13' -H 'Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==' \
  -D "$stage/ws-headers" -o "$stage/ws-body" "$url/_blazor?id=$token" || [ "$?" -eq 28 ]
grep -q ' 101 ' "$stage/ws-headers"
awk -v executable="$stage/app/Hlcp.Web" '/^ExecStart=/{print "ExecStart=" executable; next} {print}' "$stage/deploy/hlcp-web.service" > "$stage/hlcp-web.service"
systemd-analyze verify "$stage/hlcp-web.service"
echo 'Linux boot, real Nginx TLS/WebSocket, source ACL, private API and Secure cookie verified. No controller accessed.'
