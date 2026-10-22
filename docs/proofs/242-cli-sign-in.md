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

## Implementation and proof

The Workspace card offers a new-tab sign-in action with `noopener noreferrer`, a device code
with clipboard copying, or a password field that sends one bracketed paste directly to the
PTY. Values never enter composer drafts, browser storage, notifications, or application logs.
A changed screen clears the field. The card distinguishes failure, account sign-in success,
and a saved credential; the latter does not establish that the credential works.

The runtime renders a second headless terminal and keeps a separate export buffer. It omits
whole authorization screens instead of replacing complete addresses in raw PTY chunks. A real
browser proof caught why this matters: resize clears a dialog before drawing it again, and a
blank frame followed by address fragments must remain private. Quarantine ends only at a real
CLI prompt. Known addresses, their soft-wrapped fragments, device codes and submitted values
are retained only in memory to scrub native transcript exports, including live reads. This
change does not address general session-secret masking on live reads (tracked separately in
#245). The live authorized terminal still receives the CLI bytes.

State archives are withheld during sign-in and while non-excluded native state contains its
authorization material. Existing credential/cache exclusions are honored. Modifying native
transcripts to remove it was rejected because that could break the CLI's resume state. An
older safe state archive can remain the latest resume archive when a CLI retains authorization
data in its diagnostics; readable history continues to upload with masking.

Additional real PTY outcomes:

- Claude successful login against a local fake identity prints `Logged in as person@example.com`
  and `Login successful. Press Enter to continue…`. The card offers Continue. This proves the
  real CLI's success branch, not authorization of an external account.
- Codex with an unreachable proxy exits 1 and prints `Error logging in with device code:`.
- OpenCode accepts an example API key, masks it on screen and exits 0 with `Done`; it has stored
  a credential without verifying account access. Its alternate account login supports both
  browser authorization and headless device authorization.
- OpenClaw rejects an example setup token with `Expected token starting with`, then cancellation
  prints `Setup cancelled.` and exits 1.

The reproducible browser harness runs the branch's real common server and real pinned CLIs.
Chromium uses an isolated hub fixture for session metadata. Its HTTP and WebSocket traffic is
relayed to this frontend so a localhost secure context can exercise the real clipboard.
The harness does not log or save terminal frames, addresses, codes or credential input.

`browser-check.cjs` passed: Claude `/login`, invalid-code delivery, failure, Retry and repeated
`/login`; a real 30-second persistence interval with no auth material in exported history;
Codex device-code copy through the real clipboard; Cursor browser-only sign-in; OpenCode's
credential field; OpenClaw's setup-token field. Both the terminal and paste secrets are kept
out of proof screenshots. External account success and real external expired-account recovery
remain unverified without account authorization.

Validation at the implementation checkpoint: 751 runtime tests, 856 frontend tests, frontend
production build, screen-detector parity, real headless-terminal redraw/fragment scrubbing,
and real Chromium. Run `browser-hub.cjs`, then `browser-check.cjs` with `SIGNIN_UI_HOST` and
`SIGNIN_CDP` for the isolated browser. CLI paths can be supplied through `SIGNIN_CLI_BIN` and
`SIGNIN_CURSOR_BIN`. Start `mock-auth.cjs` for the local fake-identity success measurement.

## Final hardening and recovery proof

The expired-credential fixture starts the real CLI with expired example credentials, rejects
its refresh at a local TLS mock, then completes `/login` with a fake identity through the
actual Workspace card. Chromium observed the successful outcome and pressed Continue. No
external account was authorized. Cursor's local fake polling identity prints `Login successful!`
and `Authentication tokens stored securely.`; OpenClaw accepts a syntactically valid example
token and prints `Auth profile:` without checking external account access.

The final browser check compares actual addresses, fragments, device codes and submitted
values retained only in memory. A generic code-shaped regular expression was rejected: native
transcript identifiers can contain the same character pattern and produce false positives.
It inspects the real HTTP upload payloads and decompresses the actual tar/gzip state upload in
memory. Active sign-in produces no state upload; after completion, the state upload resumes
and contains no authorization data. Scrollback and native transcript checks also pass.

Additional safeguards from scanner review:

- Scrollback export uses a private, unique temporary directory and mode 0600, avoiding a
  predictable shared temporary file that another process could replace with a symlink.
- Native archive scans open without following symlinks and compare the opened inode with the
  inspected inode. A real rename replacement between inspection and open is rejected.
- An authorization revision taken before tar must still match after tar and before upload.
  A new flow while the archive is running withholds that snapshot and removes it.
- Transcript export waits for pending terminal observation before reading the native file.

Final local validation: 755 runtime tests, 856 frontend tests, production frontend build, all
five real CLIs in Chromium, mobile resize settling, expired fake credential recovery, real
clipboard copying, and actual tar/gzip upload inspection. Sanitized desktop and mobile cards
are retained in `docs/proofs/242/`; terminal content and device codes are masked in captures.
The mock prints a public CA-file path; provide it as `SIGNIN_TLS_CA` to the browser hub for the
optional fake-identity recovery test. Keep the proxy running on its default local port.

Codex's real device-auth success path was also measured against a local fake identity: it exits
0 and prints `Successfully logged in`. The fake identity has no external account privileges.
The full browser check requires the local mock proxy first, plus its CA path in `SIGNIN_TLS_CA`
when starting the hub. External account authorization remains outside this credential-free
proof.
