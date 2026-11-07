#!/usr/bin/env bash
set -euo pipefail
ulimit -c 0
proof_dir=$(cd "$(dirname "$0")" && pwd)
output=$(mktemp -d)
export PROOF_ISSUER=https://127.0.0.1:18081
export PROOF_TLS_CERT="$output/issuer.crt" PROOF_TLS_KEY="$output/issuer.key"
openssl req -x509 -newkey rsa:2048 -nodes -keyout "$output/ca.key" -out "$output/ca.crt" -days 1 -subj '/CN=Local proof CA' 2>/dev/null
openssl req -newkey rsa:2048 -nodes -keyout "$PROOF_TLS_KEY" -out "$output/issuer.csr" -subj '/CN=identity.example.com' 2>/dev/null
printf 'subjectAltName=IP:127.0.0.1\nextendedKeyUsage=serverAuth\n' >"$output/extensions"
openssl x509 -req -in "$output/issuer.csr" -CA "$output/ca.crt" -CAkey "$output/ca.key" -CAcreateserial -out "$PROOF_TLS_CERT" -days 1 -extfile "$output/extensions" 2>/dev/null
node "$proof_dir/../OidcStartupProof/issuer.mjs" >"$output/issuer.log" 2>&1 &
issuer_pid=$!
trap 'kill "$issuer_pid" 2>/dev/null || true' EXIT
for i in $(seq 1 30); do if curl --cacert "$output/ca.crt" -fsS "$PROOF_ISSUER/.well-known/openid-configuration" >/dev/null; then break; fi; sleep 1; done
dotnet build "$proof_dir" -o "$output/app"
if SSL_CERT_FILE=/dev/null dotnet "$output/app/BackendCaProof.dll" >"$output/untrusted.log" 2>&1; then
  echo 'Unexpected trust of private root' >&2; exit 1
fi
grep -Eq 'UntrustedRoot|PartialChain' "$output/untrusted.log"
PROOF_PUBLIC_TLS_URL=https://example.com SSL_CERT_FILE="$output/ca.crt" dotnet "$output/app/BackendCaProof.dll"
echo 'PASS: same .NET client fails without the private root and succeeds with SSL_CERT_FILE'
