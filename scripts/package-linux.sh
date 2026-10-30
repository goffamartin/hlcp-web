#!/bin/sh
set -eu
[ "$(uname -s)" = Linux ] || { echo 'Run packaging on Linux (the CI runner is supported).' >&2; exit 2; }
root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
cd "$root"
git diff --quiet && git diff --cached --quiet || { echo 'Commit source changes before packaging.' >&2; exit 2; }
[ -z "$(git ls-files --others --exclude-standard)" ] || { echo 'Commit or ignore untracked files before packaging.' >&2; exit 2; }
revision=$(git rev-parse HEAD)
stage=$(mktemp -d "$root/artifacts-stage.XXXXXX")
case "$stage" in "$root"/artifacts-stage.*) ;; *) echo 'Unexpected staging path.' >&2; exit 1 ;; esac
trap 'rm -rf -- "$stage"' EXIT HUP INT TERM
npm ci
dotnet publish src/Hlcp.Web -c Release -r linux-x64 --self-contained true \
  -p:RestoreLockedMode=true -p:PublishDir="../../${stage##*/}/app/" -p:SourceRevisionId="$revision"
for name in .local credentials secrets appsettings.Local.json node_modules .tools; do
  [ ! -e "$stage/app/$name" ] || { echo 'Private/tooling content found in publish.' >&2; exit 1; }
done
cp -R deploy "$stage/deploy"
cp docs/lan-deployment.md "$stage/INSTALL.md"
printf 'revision=%s\nruntime=linux-x64\nself_contained=true\n' "$revision" > "$stage/BUILD-INFO"
find "$stage" -type d -exec chmod 755 {} +
find "$stage" -type f -exec chmod 644 {} +
chmod 755 "$stage/app/Hlcp.Web" "$stage/deploy/renew-nginx.sh"
mkdir -p artifacts/packages
archive="hlcp-web-$revision-linux-x64.tar.gz"
tar --owner=0 --group=0 --numeric-owner -czf "artifacts/packages/$archive" -C "$stage" app deploy INSTALL.md BUILD-INFO
(cd artifacts/packages && sha256sum "$archive" > "$archive.sha256")
printf 'Package prepared: artifacts/packages/%s\nNot installed or deployed.\n' "$archive"
