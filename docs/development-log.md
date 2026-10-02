# Development log

Findings worth not rediscovering. Each entry says what broke, why it was hard to see, and what
to do differently. Append at the top; keep entries short enough that the file stays readable.

The rule behind most of these: **prove a fix against a reproduction before pushing it.** Every
push runs the full image matrix and a deploy, so a guess is expensive. A green suite is not proof
when the fake in the test is more forgiving than production.

---

## 2026-10-02 — Auto-approve was switched on, stored, defaulted, tested — and never asked

An autonomous session was told to research something on the web. It came back having fetched
nothing, with three refusals: `WebFetch` → "Claude requested permissions to use WebFetch, but you
haven't granted it yet", `WebSearch` → the same, `curl` through Bash → "This command requires
approval". Auto-approve was on. It had been on by default since the day unattended modes got the
flag.

**The flag was never reachable from an unattended session.** `AutoApprove` is answered in one
place, `InternalController.RequestPermission`, and exactly one caller reaches that endpoint:
`agent-runtime/claude/hooks/pretooluse-hook.sh`. The settings renderer in `mcp-policy-hook.sh`
registered that hook only in its interactive branch — the unattended branch registered the
`mcp__.*` matcher alone, and even that path returned `{}` for anything it did not deny. So an
autonomous run leaned on `--permission-mode acceptEdits` by itself, which auto-approves file edits
and nothing else. Every non-edit tool fell back to the CLI's own permission flow, where a `-p` run
with no terminal is simply a refusal.

**Why no test caught it.** `AutoApproveDefaultTests` asserts the default is `true` per mode, which
it is. `claude-driver.test.js` asserts the autonomous command contains `acceptEdits`, which it
does. `mcp-policy-hook.test.js` asserted that unattended modes register *only* the MCP matcher —
the bug was written down as the expectation. Three green tests around a feature that did not work:
none of them crossed the seam between the backend flag and the runtime that had to ask about it.

The fix is to register both matchers in every mode and let `continue_flow` delegate to the approval
hook unconditionally. The MCP matcher's timeout goes from 5s to 1900s because it now has to outlast
the poll window of the hook it delegates to. With auto-approve on, `/permission` answers "allow" on
the first POST and nothing ever polls — the cost is one request per tool call.

**The alternative was `--permission-mode bypassPermissions`** in the driver, which is one line.
Rejected: it bakes the decision into argv at launch, so toggling auto-approve on a running session
would not take effect, an explicit `AutoApprove = false` would need a second code path anyway, and
it would hand an unattended session a blanket bypass that also skips the MCP sharing policy. The
hook keeps one decision point for every mode.

**What proving it took.** Two things had to be true, and neither followed from the other: that the
hook chain reaches `/permission` in unattended mode, and that the CLI honours an `allow` from a
hook in `-p` mode *despite* `acceptEdits`. The first is a node test against a stub backend. The
second needed the real CLI: `claude -p` on `example.com` reproduced the exact refusal text with no
hook registered, and fetched the page with an always-allow hook registered — the hook's own log
proving it ran and saw `WebFetch`. Worth keeping: the negative-lookahead matcher `^(?!mcp__).*`
does match built-in tools, which is the assumption the whole fix rests on.

### The same flag was broken three more ways, each differently

Checking the other runtimes turned up four different behaviours behind one checkbox. Claude was the
only one that failed closed; the rest failed open, and none of them actually read the flag.

| Runtime | What an unattended session did | `AutoApprove` honoured? |
|---|---|---|
| Claude | `acceptEdits` only — stalled on the first non-edit call | neither value |
| Codex | `/agent-policy` answered `deny` for anything outside the allow list, never `ask` | neither value |
| Cursor | `-p --force` — "force allow commands unless explicitly denied", unconditionally | only `true`, by accident |
| OpenClaw | nothing configured; the documented baseline for an unconfigured host is `full` / `off` | neither value |

**The decision underneath.** `AgentPolicyMatcher` only ever returned `allow` or `deny`, so for an
unattended session the allow list *was* the boundary and `/permission` — the only place the flag is
read — was unreachable. Two readings of the product were both written down in this repo: CLAUDE.md
said unattended sessions auto-approve, the Cursor design doc said automation stays default-deny.
They cannot both hold. The owner chose auto-approve as the boundary, so an uncovered tool now
returns `ask` *when the session auto-approves* and the approval endpoint answers it. With
auto-approve off, every runtime keeps exactly today's strict allow list — the flag is the switch
between the two, so nothing widens for anyone who turned it off. Malformed input is never softened:
a shell string the parser cannot take apart stays a hard deny, as does the MCP sharing policy.

**Codex** needed the `ask` verdict to stop being a local `deny`, and every no-decision path to fail
closed instead — an unanswered `PermissionRequest` in an unattended session is Codex's own approval
policy deciding, not a safe no-op.

**Cursor** needed one line: `--force` is auto-approve expressed as a flag (the CLI says so), so it
belongs to the flag rather than to the mode. Hooks were the alternative and were not needed.

**OpenClaw could not use a hook at all.** Its `hooks` are plugin packs for lifecycle events, not a
command asked about each tool call, so there is nowhere to put a callback. What it has is an exec
policy, and `agent-runtime/openclaw/exec-policy.js` writes it: `tools.exec` in `openclaw.json` plus
`defaults`/`agents.<id>.allowlist` in `exec-approvals.json`. Three things learned the hard way and
worth not rediscovering:

- **Both files are consulted, and the stricter wins.** The docs put it as "approvals can only
  tighten config-derived security/ask, never loosen them", so writing one and not the other
  silently does nothing.
- **The approvals file is written whole, never merged.** `~/.openclaw` is the state directory and
  comes back from the session's own archive, so a merge would let an entry an earlier incarnation
  accumulated — including one the agent added itself with `openclaw approvals allowlist add` —
  grant a permission the hub never did.
- **The two allow lists are not the same shape.** AgentHub stores command prefixes, OpenClaw
  matches a glob against the resolved binary, so `git status` can only become `**/git`. The
  argument half has nowhere to go; narrowing it would need `argPattern`, which AgentHub does not
  store. The test says this out loud because it means a policy allowing one careful `rm` grants
  every `rm`.

**Verification, and what is still open.** Neither the Cursor nor the OpenClaw CLI is installed
locally, so both were probed in containers at the pinned versions. Cursor's `--force` semantics
come from the CLI's own `--help`. For OpenClaw, `openclaw config validate` accepts the generated
config, `openclaw exec-policy show` reports the intended effective policy for all three cases, and
`openclaw approvals get` ingests the approvals file and assigns ids to its entries — so the shapes
are right, not merely plausible JSON. What a container cannot show is a tool call being matched at
exec time, which needs a model turn and credentials: **whether a `**/git` pattern matches a
resolved `/usr/bin/git` is the one piece still to confirm in a pod.** The docs note that "bare
executable names ... still require a human", which reads as being about remote node dispatch rather
than the pattern form, but that is an interpretation and not a test.

---

## 2026-10-01 — The skill library's two silent failures were both about context

Two things went wrong in the same test, and neither looked like a bug.

**Files travelled as text, so large ones never travelled at all.** `upload_skill` took file
content inline, which meant an 18 KB helper script was quoted into a tool call on the way up and
quoted back out on the way down — paid for twice, in a context the agent needs for the task. The
agent's response to that cost was rational: it stopped uploading scripts. Nothing failed, nothing
was logged, the library just quietly only ever held prose.

The fix is a filesystem path on both ends, and the reason it needed a new component is worth
remembering: **the skill-library MCP server runs in the backend, not in the pod.** It cannot read
or write the agent's disk. `agent-runtime/skills/` is now a local stdio proxy that takes over the
hub's injected `skill-library` entry, adds `path` to `upload_skill` and `out_dir` to `get_skill`,
and forwards everything else untouched — so a tool added to the hub later works through it without
being mentioned in it. For the curl case the hub serves the files itself, at a plain
token-authenticated route rather than a presigned storage url: those expire while the agent is
still working with them, which is the same trap `InternalSessionFilesController` documents.

**The server instruction to upload gotchas was read, then forgotten.** It arrives at
`initialize` — tens of thousands of tokens before the end of the task that produced something
worth saving. An instruction at the start cannot survive a long turn. `common/skill-reminder-hook.mjs`
asks at the end instead, through each agent's own end-of-turn hook (Claude `Stop`, Codex
`[[hooks.Stop]]`, Cursor `stop`), all three of which take the same "block with a reason" shape.

Two details that the obvious implementation gets wrong:

- **Fire only after a turn that changed something.** A reminder after a plain question is pure
  noise, and noise is what gets hooks switched off. There is no transcript to inspect in Cursor's
  payload and no tool name in it either, so a `PostToolUse` / `afterFileEdit` marker file is what
  separates work from an answer. No marker, no reminder — being quiet when unsure is the cheaper
  mistake.
- **Honour the loop guard.** `stop_hook_active` (Claude, Codex) and `loop_count` (Cursor) exist
  because the reminder's own continuation ends in another stop.

**Also: JSON results cost six tokens per umlaut.** `System.Text.Json` escapes `ö` to `ö`,
and the library is full of German runbooks. The tool results are plain text now; the service test
asserts the result is *not* parseable as JSON, because that is the only way the escapes cannot
creep back in.

**Verifying it needs a pod.** `agent-runtime/skills/probe.sh` runs the whole path from inside a
session — upload from a path, download to a directory, both download urls with `curl`, and the
same urls without the token. Unit tests against fakes cannot see an egress policy or a service
that only resolves in the control namespace.

Windows aside: `tar` was given `-C <absolute path>` and MSYS tar mangled it (`C\:\\Users\\…`),
while bsdtar on the same machine was fine. The archive now goes in on stdin and the destination
is the child's working directory — no path argument for any tar implementation to reinterpret.

---

## 2026-10-01 — Chrome renders no PDF in a sandboxed frame, under any token

The preview pane showed Chrome's grey blocked-content placeholder for every PDF. The frame carried
a bare `sandbox`, and the obvious reading — that the PDF viewer needs `allow-scripts` — is wrong.
Measured across Chrome 154: bare, `allow-scripts`, `allow-same-origin`, `allow-scripts
allow-same-origin`, `allow-downloads allow-scripts` all produce the placeholder. **No combination
renders.** Without the attribute it renders; so does `<embed>`.

That attribute was never the control it looked like, either. `allow-scripts` with
`allow-same-origin` would have let the frame drop its own sandbox, because a `blob:` URL inherits
the creating document's origin — the one combination that could have worked is the one that must
not be used.

What holds the line instead is the **blob's MIME label**. A `blob:` URL is served with the blob's
own type, so `blob.slice(0, blob.size, 'application/pdf')` before `createObjectURL` decides what the
frame can become: a PDF-viewer document, cross-origin to us and out-of-process, never an HTML
document on our origin. Verified: a blob typed `application/pdf` whose bytes are HTML-with-script
fails to parse and runs nothing; a PDF carrying `/OpenAction /URI` and a `/Names /JavaScript`
action produced no request, no navigation, no popup.

`<object type="application/pdf">` was rejected for the opposite reason — given HTML bytes it
ignores the declared type and executes them. And pdf.js was rejected because it would parse
untrusted bytes *in our own renderer*, turning a parser bug into XSS on the app origin; the
browser's viewer is the stronger isolation here, not the weaker one.

**Left behind:** the 2026-08-01 plan and design records still describe the `sandbox` attribute as
the protection, and credit an application-level CSP that does not exist anywhere in the repo.

---

## 2026-10-01 — The agent content route redirected the pod to object storage

The route added so a pod with no path to object storage could still read its files answered a
**redirect** to a presigned url whenever `CanServeBrowsersDirectly` was set — the one configuration
where it mattered. Two reasons that is wrong, neither visible in a test:

- The pod's `fetch` follows redirects, so it looked like it worked wherever storage happened to be
  reachable from the pod. Where it was not, the route failed for the exact case it was built for.
- A followed redirect carries custom headers, so `X-Agent-Token` — the session's callback token —
  went to the storage host.

`OpenContentAsync` now takes `allowRedirect`; the agent route passes `false` and always streams.
The browser route still redirects, which is the point of having it.

**Why it was hard to see:** the route had no test at all. The fake in `InternalSessionFilesTests`
threw `NotSupportedException` for `OpenContentAsync`, so every test passed without ever calling it.
A fake that throws is not coverage — it hides the absence of it.

**Also found, same path:** `safeError`'s allowlist in `agent-runtime/files/server.mjs` is what
decides whether a code reaches the agent intact. `file_too_large` was missing, so the one condition
worth retrying arrived as `files_operation_failed`. Any new throw needs an entry there, or it
degrades silently.

And `read_file` refused a file for being large — `file_text_too_large`, with no path — while the
comment three lines below claimed large text was reported by path. The bytes are on disk by then,
so there was nothing to gain by failing. Over-limit and undecodable files now return the path, and
every reply carries `localPath` alongside any inline content: without it the agent can look at an
image but cannot put it in its working directory, which is usually what it was asked to do.
---

## 2026-10-01 — The upload queue deleted the files it had just uploaded

Reported as "uploaded files are not remembered; after a session restart they are gone". The listing
was right and object storage was fine — the rows really were `Deleted`, and the `DELETE` came from
the browser.

`createAttachmentQueue` has one teardown path, `cancelAll()`, and it called `remove()` on every
row. For a row still uploading that is correct: it owns a reservation nobody else will clean up.
For a finished row it deleted a completed session file. `cancelAll()` runs on unmount and on a
session change, and in the Files pane closing the pane clears `filesOpen`/`selectedId`, which makes
`companionVisible` false and unmounts the whole workspace shell — so uploading a file and then
closing the pane destroyed it. `clearReady()` had the distinction right all along; the teardown
path did not.

A second defect sat in the same four lines: `cancelAll()` mapped over the live `items` array while
`remove()` spliced from it, so every second row was skipped and its reservation never cancelled.

**Why the suite missed it:** the existing test removed a *ready* item and asserted the delete — the
behaviour was pinned as intended, because nothing distinguished "the user withdrew this attachment"
from "the component went away". Tests now cover both meanings, including the full chain through
`SessionWorkspace`.

**Diagnosis note:** three plausible server-side theories (storage kind falling back to pod-backed
because of a Helm key mismatch, the pause-time pod-file expiry, a stale `expires_at`) were all
wrong, and one `SELECT` settled it. Every row was `S3`; one was `Ready` with an `expires_at` a day
in the past and untouched; one was `Deleted` while its session was still running. Read the rows
before theorising about the code that writes them.

---

## 2026-09-30 — File reads: four bugs stacked in one path

A user uploaded a PDF, the agent said `file_not_found`, and the first three "fixes" were each
real but insufficient. In order of discovery:

1. **`Content-Length` missing on the S3 put.** The AWS SDK signs the body and needs the length up
   front; a Kestrel request body reports neither `Length` nor `CanSeek`, and the size-limit
   wrapper hid it too. Resolve the wire length *before* wrapping.
2. **Object storage was simply gone.** MinIO's community images stopped being publicly pullable
   (`quay.io` answers 401), so the pod could not restart. Replaced with Garage. Anything pinned to
   `:latest` is one upstream decision away from this.
3. **Synchronous read on the request body.** `SizeLimitedReadStream` overrode
   `ReadAsync(Memory<byte>)` but not `ReadAsync(byte[],int,int,ct)`. The AWS SDK calls the array
   overload, whose default implementation routes to the synchronous `Read` — which Kestrel
   rejects. Override both.
4. **Presigning used SigV2 over https.** Only the global `AWSConfigsS3.UseSignatureVersion4`
   switches presigning to V4; `AmazonS3Config.SignatureVersion` does nothing. And the scheme has
   to be forced back to the endpoint's own: an `http://` endpoint gets signed as `https`, and the
   pod then fails the TLS handshake against a plain-HTTP port (`curl: (35) wrong version number`).

Then two more in the same area:

5. **`CompleteAsync` accepted only `Reserved` for S3**, but a proxied upload leaves the record
   `Uploading`. Proxying is the only path available when storage is not reachable from the
   browser, i.e. the normal cluster-internal setup — so completion always answered 409 after the
   bytes were already stored.
6. **`expires_at` survived the transition to Ready.** It is the deadline for *finishing an
   upload*, not a lifetime. A permanent file looked time-limited, and an agent reading the listing
   concluded the file had expired. The sweeper only ever expires `Reserved`/`Uploading`, so the
   file was always still there.

**Why the suite missed all of it:** the test doubles were more forgiving than production. The fake
request body allowed synchronous reads; the artifact double copied with `CopyToAsync` (the
`Memory` overload, never the array one) and reported a null content type, which skipped the
content-type comparison entirely. See [`test-doubles-must-match-production`](#) — make the double
refuse what the real thing refuses, and confirm a new test fails without the fix.

**Design correction from the owner:** do not hand file *contents* to the model. It should get a
path or link and use its own tools — grep, ranged reads — so a large document does not consume
context. Inline content only where it is both small and directly useful.

---

## 2026-09-29 — An MCP client caches the tool schema at connect time

A parameter added to a tool after a client connected arrives as a **string**, because the client
still validates against the schema it fetched. Declaring it `bool` made the whole call fail with
`JsonException: The JSON value could not be converted to System.Nullable\`1[System.Boolean]` —
an error that tells the caller nothing, for something they got right.

Boolean tool arguments are therefore declared as `string` and parsed leniently. Unrecognised input
means "not specified" rather than `false`, so a typo cannot quietly disable an unattended session's
auto-approve.

---

## 2026-09-28 — Unattended sessions stalled on permission prompts

An autonomous session asked for approval, nobody was there to answer, and it exited 0 — so the
backend recorded **Succeeded** for a session that did nothing. Its own words: *"This session can't
prompt for approval, so I never saw a single alert."* A session that looks finished and produced
nothing is worse than a visible failure.

`AutoApprove` is now nullable and defaults from the mode: on for Autonomous and Scheduled, off for
Interactive, where a human is present. An explicit value always wins.

**Also learned:** an agent that clones only this repository sees no `CLAUDE.md` unless the file
lives *in* the repository. It used to sit one directory above.

**And:** the agent runtime ships `node` and `npm` only — no `dotnet`, `docker`, `trivy` or `pwsh`.
A prompt asking a session to run the .NET suite is unsatisfiable unless it starts with
`runAsRoot` so it can install what it needs, or with a custom image. State the environment's
limits in the prompt; agents plan around them well when told.

---

## 2026-09-28 — OpenIddict development certificates cannot be used here

`AddDevelopmentEncryptionCertificate()` writes the generated certificate into the user's X509
store — a path on disk — and the backend container has `readOnlyRootFilesystem: true`. The app
starts, then throws on every request that reaches an OAuth endpoint, `/healthz` returns 500, and
the liveness probe crashloops the pod.

Certificates now live in Postgres and load with `X509KeyStorageFlags.EphemeralKeySet`. Two
benefits beyond the fix: issued tokens survive a restart, and all replicas sign with the same key.

**The lesson that generalises:** a hardened container is its own test surface. `dotnet run` has a
writable home directory, so this reproduced only when the built image was run with
`--read-only --user 1000:1000`.

---

## 2026-09-28 — Routing has to know about new well-known paths

`/mcp`, `/connect` and the `/.well-known` documents fell through to the SPA, which answered
discovery requests with `index.html` and POSTs with 405. The backend was serving them correctly
the whole time; nothing could reach it. `/.well-known/jwks` was missed on the first attempt
because the discovery document advertises it — check every url a discovery document names.

Do not route all of `/.well-known` to the backend: that swallows cert-manager's acme-challenge
path.

---

## 2026-09-28 — Session teardown needs `deletecollection`

Deleting a session removes its network policies by label selector in one call. The Role granted
`delete` but not `deletecollection`, so every delete failed with a 403 — the pod went away, the
record stayed, and the caller saw a generic failure. The same rule was gated on `browser.enabled`
alone, although runtime port requests also create policies.

---

## Tooling notes

- **PowerShell strips a bare `--`** when a function takes `ValueFromRemainingArguments`. That
  broke a `git filter-branch … -- --all` invocation, which git then rejected wholesale. Pass an
  explicit array instead.
- **PowerShell pipes CRLF.** `git update-ref --stdin` rejects the trailing CR with
  `fatal: … expected SP but got: ?`. It failed silently once and left 34 refs behind, which made a
  history rewrite look successful when it was not. Delete refs one at a time, and verify the count
  afterwards.
- **`git filter-branch` over `--all`** takes roughly eight minutes for this repo on Windows (426
  refs). Run it in the background, and note it refuses to start with a dirty working tree.
