# Development log

Findings worth not rediscovering. Each entry says what broke, why it was hard to see, and what
to do differently. Append at the top; keep entries short enough that the file stays readable.

The rule behind most of these: **prove a fix against a reproduction before pushing it.** Every
push runs the full image matrix and a deploy, so a guess is expensive. A green suite is not proof
when the fake in the test is more forgiving than production.

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
