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

Cursor's refused local polling identity exits 1 with `Login failed or timed out. Please try
again.` The final merged frontend suite passes 857 tests. The merged runtime suite passes 755;
one confirmation run hit the existing timestamp-sensitive state-signature chmod assertion,
then its isolated 9-test file and the full 755-test rerun both passed without changes to the
state-signature implementation or test.

## Archive withholding visibility

A withheld archive sets a separate persistence control status for subscribed terminal viewers. The owner sees a warning above the Workspace, including after reconnect; older viewers still receive only terminal output. A fixed log message appears on the transition, without authorization data or paths. Only a successful state upload clears the warning and logs recovery. Merely leaving sign-in or passing a scan is insufficient: the subsequent upload may still fail. Silent retry was rejected because a pod restart could otherwise lose newer resume state without the owner knowing.

## Releasing remembered values

Remembered values have no time-only expiry: a native transcript can retain a value long after a login screen closes. On a persistence tick after sign-in and transcript catch-up, scan every archived state file, the configured native session directory, current and previous transcripts, preserved conversation history, the sanitized export buffer, pending input and the current terminal screen. Drop each value independently only when the entire scan completed and none of those sources contains it. Failed reads, inode replacement and files changing during a read keep all remembered values. Missing optional sources are absent, not scan failures. Credential/cache exclusions apply to the state archive scan; explicitly exported transcript/history files are scanned even when outside that tree.

A longer fake-login proof exposed an overbroad match: the public route word "login" wrapped onto its own line and was remembered as a secret. Ordinary /login command metadata then blocked every archive. The continuation collector ignores alphabetic fragments of at most six characters, including during partial redraws. Full URLs, sensitive query values, actual device codes (including wrapped codes), and submitted codes are captured independently without that fragment filter; opaque longer fragments remain protected. No route-word allow-list is used. A real prompt also ends stale introductory text, while blank redraw frames remain quarantined.

Per-file archive scrubbing and the complete Codex/OpenCode post-login archive lifecycle are tracked in #263. No external account authorization is claimed by the fake identity tests.

The extended real-CLI/Chromium run waits through an active-login persistence tick, verifies the owner warning, completes the local fake identity login, then waits through the next tick. The warning clears only after the real tar/gzip state PUT succeeds. In-memory inspection found no observed authorization values in that archive or transcript exports; the full scan released ten absent remembered values. Local validation after merging main: 763 runtime tests, 882 frontend tests and the production build passed. The broader five-CLI rerun reached the device-code/browser flows but timed out opening the separate credential CLI's screen; this is not a completed post-login proof for that CLI.

The credential-screen timeout was a proof ordering error: the browser pressed Enter before the native provider selector was drawn. The pinned CLI reached both selector and credential screen in a fresh PTY; its driver has no diff against main. The browser proof now waits for the actual selector text before sending Enter.

Final stronger validation also distinguishes the authorization screen's public code=true mode flag from returned code values: that boolean flag must not quarantine every JSON true. State/challenge values and actual submitted codes remain masked independently. Eight-letter device codes without a hyphen, on one line or split across rows, have explicit runtime/frontend parity and privacy coverage. The credential comparison helper proved selector → credential through both origin/main and branch common servers, each with a fresh isolated home and the actual pinned CLI.

The native scan opens with no-follow/nonblocking flags before inspecting metadata, then validates the open descriptor and pathname identity after reading. The earlier lstat-before-open shape was rejected because it leaves a path replacement window for analysis even with a later inode check. A real-filesystem regression replaces the pathname while its descriptor is being read and verifies that the scan refuses the snapshot.
