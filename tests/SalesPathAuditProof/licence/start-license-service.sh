#!/usr/bin/env bash
# Starts a checkout of the (private) licence service locally with SQLite, a throwaway ES256
# key and a throwaway admin key. No payment-provider key: checkout/portal are out of scope, grant,
# claim, token and heartbeat need none.
#   LS_SRC=<license-service checkout> RIG=<scratch dir> ./start-license-service.sh
set -euo pipefail
LS_SRC=${LS_SRC:-$HOME/rig/license-service}; RIG=${RIG:-$HOME/rig/lic}; PORT=${LS_PORT:-18500}
export PATH=$HOME/.dotnet:$PATH DOTNET_ROOT=$HOME/.dotnet DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
mkdir -p "$RIG"; cd "$RIG"
[ -f priv.pem ] || { openssl ecparam -name prime256v1 -genkey -noout | openssl pkcs8 -topk8 -nocrypt -out priv.pem; openssl ec -in priv.pem -pubout -out pub.pem 2>/dev/null; }
[ -f admin.key ] || openssl rand -hex 24 > admin.key
chmod 600 priv.pem admin.key
[ -f bin/LicenseService.dll ] || dotnet build "$LS_SRC" -c Release -o "$RIG/bin" >/dev/null
# LICENSE_SERVICE_CONFIG points away from /etc so a host config can never be picked up.
LICENSE_SERVICE_CONFIG=/nonexistent \
License__PrivateKeyFile="$RIG/priv.pem" Admin__ApiKey="$(cat admin.key)" \
Billing__TokenLifetimeDays="${TOKEN_LIFETIME_DAYS:-45}" \
ConnectionStrings__Db="Data Source=$RIG/licenses.db" ASPNETCORE_URLS="http://127.0.0.1:$PORT" \
exec dotnet bin/LicenseService.dll
