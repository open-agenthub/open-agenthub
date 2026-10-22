# Workspace sign-in measurement

Credential-free measurements use the pinned runtimes in a real PTY, rendered by the same
headless terminal used in the session agent. The proof runner keeps authorization material in
memory and emits sanitized screens only. Do not capture raw terminal frames or screenshots of
live authorization values as proof artifacts.

Measured on 2026-10-22:

- Claude 2.1.287: `/login` opens a numbered method chooser. Subscription login displays
  `Browser didn't open? Use the url below to sign in (c to copy)`, a wrapped authorization
  address, `Paste code here if prompted >`, and `Esc to cancel`. An intentionally invalid code
  displays `OAuth error: Invalid code. Please make sure the full code was copied`,
  `Press Enter to retry.`, and `Esc to cancel`.
- Codex 0.160.0: the runtime's `login --device-auth` prints a numbered pair of instructions:
  `Open this link in your browser and sign in to your account`, followed by the address, then
  `Enter this one-time code (expires in 15 minutes)`, followed by the code. This flow polls;
  it does not accept a pasted authorization code.
- Cursor 2026.10.22-27dcf5f: `agent login` prints `Waiting for browser authentication...`,
  `Open a browser and navigate to this link:` and a wrapped address. It offers a QR code with
  `q`. Ctrl+C exits with 130 without printing a cancellation outcome.
- OpenClaw 2026.7.1-2: the runtime's `models auth add` presents provider and method selectors,
  then `Paste Anthropic setup-token`. It does not generate a browser authorization address
  on that route.
- OpenCode 1.18.35: the runtime's `auth login` presents a searchable provider selector. Its
  default route prints `Create an api key at` and an address, then `Enter your API key`.
  Empty submission reports `Required` and keeps the input active.

Successful account authorization cannot be proven without an account authorization. These
measurements use no real credentials and do not claim successful external account access.

## Decision

Recognize the CLI's own live screen, as the existing menu notice does. Preserve the difference
between device authorization, pasted authorization codes, and credential entry. Replacing all
of them with one paste-code box would send text into polling processes or hide the token that
another CLI actually needs. Starting a separate web login implementation would also diverge
from the CLI's state, especially after a session's credentials expire and `/login` runs again.
