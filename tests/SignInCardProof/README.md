# Sign-in card proof (#344)

`shots344.mjs` walks the Workspace sign-in card in Chromium with the pinned CLIs behind
`scripts/proofs/242/browser-hub.cjs`. Claude Code: `/login`, the login method menu, the code
step, the page opened in a new tab, an invalid code, Try again, a valid fake code, Continue.
Codex: `login --device-auth` and Copy into the real clipboard. For every step it records the card's
visible buttons and links, which one is primary, and a screenshot.

Setup (see `../../docs/proofs/242-cli-sign-in.md` and the #242 hub's header):

```bash
SIGNIN_DEVICE_TOKEN_STATUS=403 SIGNIN_TLS_DIR=<scratch>/tls node scripts/proofs/242/mock-auth.cjs &
SIGNIN_TLS_CA=<scratch>/tls/ca.pem SIGNIN_CLI_BIN=<pinned clis>/node_modules/.bin node scripts/proofs/242/browser-hub.cjs &
(cd frontend && npx vite --port 5173)                      # the frontend under test
SHOTS=<scratch> APP=http://localhost:5173 node tests/SignInCardProof/shots344.mjs after
```

`SIGNIN_DEVICE_TOKEN_STATUS=403` keeps Codex polling, so its device step stays on screen. Without it,
the fake identity signs Codex in on the first poll. The script masks the device code in every
screenshot, and replaces it with `[code]` in the recorded text. It writes `shots/344-<label>.json` and
the PNGs to `$SHOTS`, never to the repository. For the baseline, serve main's frontend and pass
`before`.
