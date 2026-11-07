#!/usr/bin/env bash
# Stand-in for the prospect's certificate authority: one throwaway CA and a certificate for the hub
# and for the identity provider. A real team uses a public CA or its internal one; the rig cannot
# get a publicly trusted certificate for a name that only exists inside the guest.
# Output: $OUT (default ~/rig/m2/pki), never committed.
set -euo pipefail
OUT=${OUT:-$HOME/rig/m2/pki}; mkdir -p "$OUT"; cd "$OUT"
[ -f ca.key ] || openssl req -x509 -newkey rsa:2048 -nodes -keyout ca.key -out ca.crt -days 30 -subj "/CN=Demo stand-in CA (not trusted anywhere)" 2>/dev/null
for host in hub.example.com id.example.com; do
  [ -f "$host.crt" ] && continue
  openssl req -newkey rsa:2048 -nodes -keyout "$host.key" -out "$host.csr" -subj "/CN=$host" 2>/dev/null
  printf 'subjectAltName=DNS:%s\nextendedKeyUsage=serverAuth\n' "$host" > "$host.ext"
  openssl x509 -req -in "$host.csr" -CA ca.crt -CAkey ca.key -CAcreateserial -days 30 -extfile "$host.ext" -out "$host.crt" 2>/dev/null
done
ls "$OUT"
