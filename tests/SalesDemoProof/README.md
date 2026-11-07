# Sales milestone 2 package (#481)

Setup-time measurement, a scripted demo of the target customer's workflow (issue → agent on a
repository → pushed branch, a colleague taking over), and the pipeline that records it as a
narrated film from real Chromium, on release 1.19.0. Builds on the VM rig in
`../SalesPathAuditProof/` (QEMU in TCG mode inside a session pod, published `install.sh`); read
its README first. Results and timings are in the PR and in the prospect checklist on the website,
not here; generated frames, logs and videos are never committed.

## What is real and what is a stand-in

| Part | Here |
|---|---|
| Hub, chart, images | Real: published quickstart, chart and images 1.19.0 |
| Browser | Real Chromium (Playwright's build 141), in the session pod, reaching the guest through an SSH SOCKS proxy |
| Agent | Real Claude Code from the 1.19.0 runtime image, in a real session pod |
| Model | **Stand-in**: `demo/standin-model.mjs`, a scripted Messages endpoint used as "Own endpoint". Every reply starts with `[stand-in model <id>]` |
| Git host | **Stand-in**: a git daemon with the synthetic `invoice-service` repository; it has no issues or pull requests, so the demo ends at "pushed branch" |
| Identity provider | **Stand-in**: Keycloak at `https://id.example.com`, users alice/bob/carol, groups platform/developers/reviewers |
| TLS | **Stand-in CA** (`team/make-ca.sh`); Chromium trusts exactly the two leaf keys (SPKI pins) |
| Enterprise | **Test-key build**: the 1.19.0 backend with only the licence verification key replaced, licence heartbeat off (`team/enterprise-testkey.sh`). The UI footer then reads "dev" |

## Order

1. VM and quickstart: `../SalesPathAuditProof/bootstrap-vm.sh`, `start-vm.sh` (keep running),
   `run-quickstart.sh`.
2. Team setup, as getting-started describes it: `team/dns.sh`, `team/make-ca.sh`, copy the
   certificates and `team/idp.yaml` to the guest and apply it, then `team/run-team-setup.sh`
   (timed `helm upgrade` with `team/team-values.yaml`). On 1.19.0 this needed two additions the
   docs did not have (#508, #509): `team/trust-ca-workaround.sh` and
   `EXTRA="--set ingress.className=traefik"`.
3. Enterprise for the demo: build the test-key backend (`../SalesPathAuditProof/licence/README.md`),
   then `team/enterprise-testkey.sh`.
4. Stand-ins: `demo/standins.yaml` plus the `standin-model` ConfigMap from
   `demo/standin-model.mjs` (namespace `demo`). Pre-pull the runtime image
   (`sudo k3s ctr images pull ghcr.io/open-agenthub/open-agenthub/agent-runtime-claude:1.19.0`)
   so the film does not wait 2–3 minutes for it.
5. Browsers: `ssh.sh -N -D 127.0.0.1:11080` (SOCKS), `demo/chromes.sh`;
   `node demo/first-sign-in.mjs` times the first sign-in.
6. Each take: `demo/reset-demo.sh`, then `node demo/demo.mjs <take-dir>`.
7. Film: `video/voice.mjs` (Kokoro, from a directory with `kokoro-js@1.2.1`), then
   `node video/edit.mjs <take-dir> <voice-dir> video/narration.json <out-dir>`
   (ffmpeg and ffprobe on `PATH`).

`demo/cdp.mjs <user> '<js>' [shot.png]` runs a snippet in one user's browser; it is how the
selectors in `demo.mjs` were found.

## Rig pitfalls

- k3s CoreDNS: a second `hosts` block in a `coredns-custom` `*.override` crash-loops CoreDNS
  and takes all cluster DNS down; `team/dns.sh` uses `rewrite` to the traefik Service instead.
- Keycloak's JVM crashed in a loop under TCG until limited to one C1 compiler thread
  (`JAVA_OPTS_APPEND` in `team/idp.yaml`). `-XX:+UseSerialGC` stops it from starting at all
  (Keycloak already selects G1).
- With two backend replicas and three live hub tabs the guest's load reached 30 on 4 vCPUs; the
  hub's approval then timed out and Claude Code fell back to its own "Do you want to proceed?"
  menu. The takes ran with `kubectl -n agenthub scale deploy/agenthub-backend --replicas=1`, and
  `demo.mjs` keeps bob's and carol's tabs on `about:blank` until their scenes.
- Clicking "Sign out" leaves the identity provider's session, so the next "Sign in" returns
  without a form; `demo.mjs` clears cookies and storage of both origins instead.
- Claude Code sends side requests to the endpoint (a title without tools, a "SUGGESTION MODE"
  prompt) and the hub's Stop hook asks about the skill library; the stand-in answers each
  explicitly, or it would replay the scripted task.
- Person and link share one role picker in the Share dialog (1.19.0): set the link role after
  adding a Collaborator, or the link grants input too.
- Tool approvals are owner-only by design; the owner answers the first one with
  "Allow (don't ask again)" so the collaborator's turn runs without the owner.
- Screencast frames arrive only on repaint; the edit holds each frame until the next one, plays
  marked waits faster with an on-screen "sped up ×N", and drops the marked cut.
