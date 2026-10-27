# Claude sessions without a credential sign in by themselves

A Claude session that has nothing to authenticate with — no mounted subscription login, no API
key, no custom model endpoint — used to start to its normal prompt with "Not logged in · Run
/login". The owner had to type `/login` before anything else worked. Codex in the same state opens
its login by itself (`codex/device-login.sh`), and the Workspace shows the sign-in card at once.
Claude now does the same (#305).

## Who decides: the entrypoint, from what the pod was given

`claude/entrypoint.sh` exports `AGENTHUB_CLAUDE_SIGN_IN=1` only when all of these hold:

- not a custom endpoint (`AGENTHUB_AUTH_MODE` is not `customendpoint`);
- no login was mounted at `/secrets/claude/credentials.json` (the entrypoint's own
  `AUTH_EXPECT_CREATE=1`);
- `ANTHROPIC_API_KEY` is empty or absent — an optional secret key the owner never stored arrives
  as either;
- the session is interactive. An autonomous or scheduled run has nobody to sign in, and a chat-mode
  session runs `claude -p`, where `/login` "isn't available in this environment"
  (`docs/agent-workspace.md`), so the driver only wraps the terminal TUI.

`ANTHROPIC_*` and `CLAUDE_*` are reserved session-secret names (`docs/session-secrets.md`), so no
other credential can reach the CLI's environment behind the runtime's back.

With the flag set, the driver launches `claude/sign-in.sh` with the TUI's own arguments instead of
`claude`. The script checks once more — `~/.claude/.credentials.json`, the three credential
variables the CLI reads, and `claude auth status --json` reporting `"loggedIn": false` — then runs
`claude auth login` and `exec`s the TUI. The second check is what keeps a restart of the CLI after a
successful login (or after the login in a previous start of the same pod) from asking again; `exec`
keeps the pid `server.js` tracks the TUI's, which the transcript lookup relies on.

- *Rejected: decide from the screen* (type `/login` when "Not logged in" appears). That is a guess
  about a redraw, and a wrong guess sends a session that does have a credential into a login — the
  one outcome the issue rules out.
- *Rejected: `claude auth status` alone.* Any failure of that call — a crash, a slow start killed
  early — reads the same as "not logged in". It only confirms what the pod already says; it never
  starts a login on its own.

## How: `claude auth login`, not `/login` in the TUI

Measured with the pinned 2.1.287 in a real PTY:

- `claude "/login"` (the slash command as the initial prompt) opens the TUI's method chooser:
  subscription, Console, third-party platform. The card appears only after a choice. And the
  positional argument is the session's task (`docs/api-created-sessions.md`); spending it on
  `/login` would drop the task of every API-created session without a credential.
- `claude auth login` prints `Opening browser to sign in…`, `If the browser didn't open, visit:`
  with the wrapped address, and `Paste code here if prompted >` at once. It defaults to the
  subscription login. A wrong code prints `Invalid code. Please make sure the full code was
  copied.` on the prompt's line and keeps reading; a good one prints `Login successful.` and
  exits 0. It ignores Esc; Ctrl+C ends it with exit 130.

So the script runs `claude auth login` and then the TUI with the task, already signed in. A
cancelled or failed login still starts the TUI, whose `/login` offers the Console and third-party
routes this one skips. Ctrl+C was measured to leave the script running without a signal trap: the
CLI handles SIGINT itself and exits 130, and bash goes on to the `exec`.

## What the Workspace recognises

The standalone screen differs from the TUI's `/login` dialog, so both copies of the detector
(`agent-runtime/common/cli-sign-in.js`, `frontend/src/lib/cli-sign-in.js`) learned it:

- `If the browser didn't open, visit:` next to `Paste code here if prompted >` is the paste-code
  card, with `cancel: 'interrupt'`: the card's Cancel sends Ctrl+C, labelled so, because Esc does
  nothing there.
- A last row ending in `Invalid code. …` marks the value as not accepted; the field stays.
- `Login successful.` after the prompt is the success outcome. The TUI's prompt drawn below ends it.

The session agent's sign-in privacy filter runs on the same detector. Without it this screen
would not be recognised and the address would reach the uploaded scrollback: with main's detector
the proof below found the sign-in state value in the scrollback and state uploads; with this one
it found none and the scrollback carries `[Sign-in terminal output omitted]`. `Opening browser to
sign in` joined the filter's introductory lines, so the line drawn before the address is held back
too.

## Proof

`scripts/proofs/305/entrypoint-sign-in.cjs` runs the shipped `claude/entrypoint.sh` and
`common/server.js` with the pinned CLI, the paths `/opt/session-agent`, `/tmp` and `/secrets`
relocated into a scratch directory (in a session pod `/secrets` holds that session's real login).
The environment is the one `AgentPodSpecFactory.Build` gives a Claude container. Credentials are
fake or absent; the login completes against the local fake identity of
`scripts/proofs/242/mock-auth.cjs`, which proves the CLI's success branch, not an account.

| Scenario | Launch | Result |
|---|---|---|
| nothing (`auto`, no key, no file) | `bash …/sign-in.sh` | paste-code screen 0.2 s after the socket opened, nothing typed |
| same, on `main` | `claude` | "Not logged in · Run /login" |
| fake code through the socket, as the card sends it | `bash …/sign-in.sh` | `Login successful.`, TUI prompt, `.credentials.json` written and handed to the hub by the auth watcher; no sign-in data in the uploads |
| Ctrl+C | `bash …/sign-in.sh` | TUI prompt 1.3 s later |
| API key | `claude` | no login |
| mounted subscription file | `claude` | no login, TUI prompt |
| custom endpoint | `claude` | no login, TUI prompt |

`scripts/proofs/305/browser-check.mjs` drives the branch's web app in Chromium against the same
rig: the card appears about 1.2 s after the page loads without any input; Cancel (Ctrl+C) removes it
and the TUI starts; a fake code typed into the card shows "Signed in" and the card goes once the TUI
prompt is drawn; with an API key or a mounted login no card appears in 20 s after the CLI drew its
screen.
