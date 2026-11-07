#!/usr/bin/env python3
"""Signs a licence token in the service's format (ES256 compact JWS, typ LICENSE) with the
throwaway test key, so expiry can be observed in seconds instead of the service's whole days.
Usage: sign-token.py <priv.pem> <seconds-valid> <lid> > token"""
import base64, json, subprocess, sys, datetime, uuid
key, secs, lid = sys.argv[1], int(sys.argv[2]), sys.argv[3] if len(sys.argv) > 3 else str(uuid.uuid4())
b64 = lambda b: base64.urlsafe_b64encode(b).decode().rstrip("=")
now = datetime.datetime.now(datetime.timezone.utc)
iso = lambda d: d.strftime("%Y-%m-%dT%H:%M:%SZ")
header = {"alg": "ES256", "typ": "LICENSE"}
payload = {"lid": lid, "org": "Audit Example", "email": "audit@example.com", "seats": 3, "plan": "granted",
           "validUntil": iso(now + datetime.timedelta(seconds=secs)),
           "planValidUntil": iso(now + datetime.timedelta(days=365))}
signing_input = f"{b64(json.dumps(header, separators=(',', ':')).encode())}.{b64(json.dumps(payload, separators=(',', ':')).encode())}"
der = subprocess.run(["openssl", "dgst", "-sha256", "-sign", key], input=signing_input.encode(), capture_output=True, check=True).stdout
# DER ECDSA-Sig-Value -> IEEE P1363 r||s, which is what the backend verifies.
def ints(d):
    i, out = 2 + (d[1] & 0x7f if d[1] & 0x80 else 0), []
    for _ in range(2):
        assert d[i] == 2; n = d[i + 1]; out.append(int.from_bytes(d[i + 2:i + 2 + n], "big")); i += 2 + n
    return out
r, s = ints(der)
print(f"{signing_input}.{b64(r.to_bytes(32, 'big') + s.to_bytes(32, 'big'))}")
