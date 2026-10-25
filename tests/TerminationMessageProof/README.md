# TerminationMessageProof

What the session list shows when an agent CLI fails, checked against the real CLI rather than a
fake frame. Since #244 the session agent writes the CLI's last lines to the termination message
whenever the CLI exits non-zero. Everyone who can read the session list sees that message, so it
must not carry authorization data. #287 found a real case that did: on main, the real Codex
drew its device code, the device token request failed, and the code ended up in the message.

`codex-device-failure.cjs` runs the pinned `codex login --device-auth` inside the real
`common/server.js` of a chosen ref, with real node-pty. The fake identity in
`scripts/proofs/242/mock-auth.cjs` answers the token poll with HTTP 500:

```bash
SIGNIN_TLS_DIR=/tmp/tls SIGNIN_DEVICE_TOKEN_STATUS=500 node scripts/proofs/242/mock-auth.cjs &
CODEX_BIN=<pinned codex binary> SIGNIN_TLS_CA=/tmp/tls/ca.pem \
  [SOURCE_REF=origin/main] node tests/TerminationMessageProof/codex-device-failure.cjs
```

It prints the message with the device code replaced by `<DEVICE CODE>` and fails when the code is
in it. Measured with Codex 0.160.0:

- main: `Codex exited with code 1: <DEVICE CODE> | … | Error logging in with device code: device
  auth failed with status 500 Internal Server Error`.
- #287: the code line is gone, and the error line stays (3 of 3 runs).
