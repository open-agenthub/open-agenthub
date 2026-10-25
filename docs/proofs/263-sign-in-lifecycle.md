# Interactive sign-in and state persistence

## Evidence before changing exports

The Workspace sign-in field sends directly to the terminal, not to the conversation composer.
A sign-in value retained by a CLI is a different concern from a user message in chat. A previous
constructed retained-value regression is not evidence that normal login writes codes into chat.
We therefore measure the real login wrappers before adding file scrubbing.

Run `scripts/proofs/263/probe-lifecycle.cjs codex` with the local fake-identity proxy from #242
and `SIGNIN_TLS_CA` set to its public test CA file; run the same probe with `opencode` for its
credential flow. Each probe starts the actual common server, actual driver and its actual
login-to-TUI wrapper, with a fresh isolated home. A real terminal emulator answers terminal
queries. It observes the normal TUI and waits across the real 30-second persistence tick.
Authorization values, terminal frames and uploaded archives stay in memory; output is counts
and booleans only. Isolated native homes are deleted after the runtime exits.

Initial unchanged-main Codex result: the device screen appeared and the ordinary TUI became
ready, but persistence was still withheld and no state archive was uploaded. None of the observed
values appeared in non-excluded native state or text exports. This proves an end-of-sign-in
recognition problem, not authorization data entering chat. File scrubbing is not the remedy for
this result. OpenCode reproduced the same withholding with its native ready screen. Its entered
credential appeared only in the deliberately excluded auth file, not exported native state.

Codex retained "Successfully logged in" above its new TUI, so the old success outcome overrode
the native composer. OpenCode's framed "Ask anything" composer was not recognized by the old
privacy prompt heuristic. The runtime and frontend now share a structural completion check:
a native composer must follow the visible authentication prompts. A newer login dialog below
an old composer stays active; blank redraws do not end login.

| Actual wrapper | Before: archives / withheld | After: archives / withheld | Auth in native export files or uploaded archive/text |
| --- | --- | --- | --- |
| Codex, local fake device identity | 0 / true | 1 / false | none observed |
| OpenCode, synthetic credential accepted by CLI | 0 / true | 1 / false | none observed; credential exists only in excluded auth.json |

The actual common server, actual drivers and actual login scripts are used in both runs.
This probe establishes post-login persistence, not a real model response or deployed pod resume.
The latter are explicit owner checks below.

## Owner checks requiring real accounts

No local fake identity proves account policy, entitlement or a real provider accepting a model
request. These checks require the owner's own disposable test sessions and intended accounts.
Never post links, device codes, pasted codes or credentials in chat, logs, screenshots or reports.
Report only the stage and outcome; the private export audit must compare observed values in
memory and return booleans.

| CLI | Real-account check still needed |
| --- | --- |
| Claude | In a fresh Workspace session choose the subscription login method, complete the browser authorization and paste the requested code into the sign-in field. Confirm the card ends and a real model answers. Revoke/expire that disposable login, verify `/login` recovery, and repeat the model request. |
| Codex | With device authorization allowed by the intended account, enter the displayed code on the linked page. Confirm the normal TUI and a real model answer using that newly stored login. Repeat after the disposable account's login is revoked/expired. |
| OpenCode | Select the intended provider and enter its real credential through the sign-in field. Saving a key is not validation: confirm an actual model answer from that provider and useful failure/recovery with an invalid or revoked disposable key. Repeat separately for any browser/OAuth provider the owner intends to support. |
| Cursor | Finish the actual browser login while the CLI polls, confirm a real model answer, then repeat after revoking the disposable login. Local polling success only proves the CLI branch. |
| OpenClaw | Enter a setup token minted for the intended account and confirm a real model answer. A syntactically accepted token is not provider validation; repeat with a revoked/invalid disposable token and replace it through the sign-in flow. |

For each real-account run, also verify:

1. Open a fresh session with no previously stored login. Complete sign-in through its card;
   the code must not become a conversation message or composer draft.
2. Wait through a persistence tick. A sign-in warning must clear after an actual successful state
   save. Privately audit the actual scrollback/transcript/state exports against the observed
   values without printing or writing those values into the audit report.
3. Exchange a harmless identifiable message, pause/stop and resume into a new pod. Confirm the
   conversation resumes and the intended login still permits a new model response. This is the
   deployed backend/credential-watcher/storage proof that a local fake service cannot establish.
4. Repeat the appropriate expired/revoked-login recovery above. Confirm the next archive saves
   and another pause/resume works. Preserve only sanitized outcomes, version and timestamps.
