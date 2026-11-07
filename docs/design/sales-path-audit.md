# Sales-path gap audit, release 1.18.0 (#480)

What a prospective customer meets today, walked for real on 2026-11-07 against the published
artefacts: the website's `install.sh`, the Helm repository chart `open-agenthub-1.18.0`, the
`ghcr.io` images, the live licence service up to the payment page, and the website docs. The
target customer is the hypothesis Sales set for #480: a team of 2–20 developers, self-hosted on
Kubernetes or a Linux host with an OIDC identity provider, Enterprise edition, the job being
"issue → pull request in their own git host", with a second person taking over or reviewing a
running session and long tasks running on while the laptop is shut.

Each gap is rated **works**, **friction** (the prospect gets through, with effort or doubt) or
**blocker** (the prospect stops, or is exposed to a risk they cannot see). Effort: **S** up to a
day, **M** up to a week, **L** more. Security and installation come first, as the issue asks.

## How it was walked

| Part | Environment | Real or stand-in |
|---|---|---|
| Install | Fresh Debian 12 VM, 4 vCPU / 10 GiB, `curl -fsSL https://open-agenthub.github.io/install.sh \| sh` unchanged | Real. The VM is emulated (QEMU without hardware acceleration, because the audit ran inside a session pod), so its durations are upper bounds; the nightly install-smoke run on a hosted runner gives the lower bound |
| Browser | Chromium on the same VM, at `http://localhost:8080` through the port-forward the installer prints | Real |
| Agent account | The session's "Own endpoint" (custom model endpoint) mode, pointed at a scripted model that answers the task with one shell tool call | Stand-in: no provider account was used |
| Git host | A git daemon in the cluster that accepts pushes | Stand-in: no hosted git account was used, so the PR step ran only up to `gh` asking for authentication |
| Identity provider | Keycloak in dev mode, one realm with two users, alice and bob | Stand-in, as #480 allows |
| Enterprise licence on the install | The 1.18.0 backend rebuilt with only the licence public key replaced, mounted over the image's `/app` | Stand-in, labelled: the released binary accepts only tokens signed by the production service, and no owner key was used |
| Licence lifecycle | The licence service run locally with a throwaway key, against the real 1.18.0 backend | Real code, local keys; no payment provider calls |

Scripts: `tests/SalesPathAuditProof/` (VM, install, stand-ins, browser steps) and
`tests/SalesPathAuditProof/licence/` (licence lifecycle). Screenshots and logs are in the
session's Files (`sales-audit-480-evidence.tgz`), not in the repository.

## Top five

| # | Area | Gap | Rating | Effort |
|---|---|---|---|---|
| 1 | Install, security | The 1.18.0 chart defaults to `image.tag: latest` with `pullPolicy: IfNotPresent`. The "1.18.0" quickstart ran backend and frontend built from `main` (`ca6231a`, built 09:24 the same day); the UI footer shows `ca6231a`. Re-running the installer or `helm upgrade` never moves an image, and two nodes can run two different "latest". | blocker | S |
| 2 | Sales | Enterprise, which is what the target customer needs, cannot be evaluated without entering a payment method. The 90-day trial exists only as a subscription checkout started from inside an installed instance; the website's pricing section has no Enterprise offer, price or button, while the README sends readers there for "€6 / user / month, 3-month free trial". | blocker | M |
| 3 | Install | From the quickstart to a team setup (OIDC) is undocumented and fails hard. The quickstart has no hostname or TLS; a plain-HTTP issuer needs `oidc.requireHttpsMetadata=false`, which no user doc mentions, and without it every backend request answers 500, `helm --wait` blocks for its whole timeout and the release is left `pending-upgrade`. The frontend always asks for `offline_access`, which no doc mentions either. | blocker | M |
| 4 | Security | On an OIDC install every user who signs in is an admin until someone is designated (verified: bob, the second user, saw the Admin section). Neither the quickstart's OIDC hint nor the getting-started page sets `ee.admins`. | blocker | S |
| 5 | Licence | Licence token lifecycle hardening, tracked privately. Separately, Admin shows no warning while heartbeats fail; Enterprise features stop 45 days after the last successful report with no notice before. | friction to blocker | S–M |

## 1. Install (quickstart, k3s)

| Step | Observed | Time | Rating | Proposed fix | Effort |
|---|---|---|---|---|---|
| Run the one-liner | Exit 0. k3s pinned (`v1.36.5+k3s1`) and checksum-verified by its installer; Helm installed from the Helm project's install script on its default branch (unpinned); Postgres, Garage and the bucket set up without a question | 6 min 28 s on the emulated VM (k3s 1 min, chart 3 min 38 s, storage init and backend restart 1 min 25 s); 60 s on the hosted runner with images preloaded | works | — | — |
| Which version runs | Chart 1.18.0, images `backend:latest` / `frontend:latest` = `main` at `ca6231a`, not the `1.18.0` images (digests differ: `latest` `sha256:696f…`, `1.18.0` `sha256:9cb4…`). The UI footer shows the commit, not the release | — | **blocker** | Default `image.tag` to the chart's `appVersion` (keep `latest` opt-in), so the chart version is the product version; the installer prints it | S |
| Upgrade by re-running the installer | `upgrade --install` with the same values leaves the pod spec unchanged, and `IfNotPresent` keeps the cached `latest`, so nothing is updated while the command reports success. The chart-values page says "pin a release tag in production"; the quickstart and getting-started do not | — | **blocker** (follows from the row above) | Same fix; the upgrade page leads with `--version` | S |
| Installer output | Prints the port-forward and the command that reads the API key, never the key itself. One stray warning from the Helm installer ("Could not find git. It is required for plugin installation") | — | works | — | — |
| Unpinned remote installers | The macOS path runs the k3d install script from its default branch, and Helm's script likewise. A change upstream changes what a prospect executes as root | — | friction (security) | Pin both scripts to a tag and verify a checksum, as for k3s | S |
| Backend start-up probes | Startup and readiness probes failed for 1–2 minutes before the backend answered; the installer waited correctly. Expected under emulation, kept as data | — | works | — | — |
| Air-gapped or proxied networks | Not tested. The installer needs the k3s, Helm and `ghcr.io` endpoints; the licence checkout needs the licence service (§2) | — | not verified | — | — |

## 2. Edition and licence

### What the prospect sees

| Step | Observed | Rating | Proposed fix | Effort |
|---|---|---|---|---|
| Without a licence | Settings → License: "Enterprise features are locked … new customers get a 3-month free trial", a **Get enterprise license** button and a token field. Core features all work; Enterprise endpoints answer 402. A 402 on `/api/sessions/shared` appears in the browser console on every page load | works | Skip the shared-sessions call when unlicensed | S |
| Getting a trial | Checkout form (billing email, organisation, seats) → the payment provider's page: "Try Open AgentHub Enterprise — 90 days free, then €18.00 per month" for 3 seats, a required payment method (card and three consumer payment methods), terms consent, and the button **"Subscribe with obligation to pay"**. Not continued | **blocker** for evaluation | A trial without a payment method: the checkout's "collect a payment method only if required" option, or a trial token from a sign-up form on the licence service | M |
| Offer is B2B only | The checkout text says business customers only; the "I'm purchasing as a business" box is optional, and consumer payment methods are offered | friction | Require the business box, or remove consumer methods in the provider's dashboard | S |
| Product text at checkout | The product description on the payment page names a third-party product | friction | Review the payment page's product text against the brand rules | S |
| Where Enterprise is offered | Website pricing: one Community card only (the Enterprise card was removed in the website's commit `72489b9`); FAQ: "There is currently no hosted trial". README: "€6 / user / month … 3-month free trial. See pricing". In the app: "3-month free trial". Three statements, and no way to buy outside an installed instance | **blocker** | Enterprise card with price, trial terms and how to get it, linked from the README and the Admin page | S |
| Activation | Paste token → Activate: plan, organisation, seats, valid until. Takes effect at once; a token that does not verify is rolled back | works | — | — |
| Copy in Admin | The token card says Enterprise unlocks "Slack notifications, permission buttons"; the call to action says "session sharing, Slack notifications and more"; the seats card says "monthly heartbeat" while the default interval is 24 h | friction | One feature list (from `ee/README.md`) and "daily" | S |
| Seats after switching to OIDC | The quickstart's API-key user `admin` stays listed and counts as a seat: "3 in use" for two people | friction (billing) | Exclude the API-key user from seats once OIDC is on, or offer to revoke it | S |
| Sessions after switching to OIDC | The four sessions created with the API key belong to owner `admin` and are invisible to the OIDC users | friction | Document it; optionally an admin "transfer owner" for API-key sessions | S |

### Licence lifecycle, end to end without payment (local licence service, real 1.18.0 backend)

| Step | Result | Rating |
|---|---|---|
| Grant (`POST /admin/licenses` with a local admin key) | 201, `plan=granted`, 3 seats, token valid 45 days, licence row valid 1 year | works |
| Activate | 200, plan and seats shown; an Enterprise endpoint switches from 402 to 200; a tampered token is refused and the previous one kept | works |
| Heartbeat | First report 30 s after start, then every 24 h; the service stores one seat-report row and returns a fresh 45-day token; a granted licence renews this way until its row's own end date | works |
| Service unreachable | Backend logs "Seat usage report failed … Connection refused"; Admin still shows the licence as valid with the old "Last check-in"; nothing in the UI warns | friction (Top five #5) |
| Expiry | With a 90-second token the Enterprise endpoint went to 402 exactly at expiry; Admin then shows "Enterprise features are locked" and "License expired." | works |
| Replacing the verification key for a test | Not configurable, by design. A throwaway build with the two key lines replaced is the only way; documented in `tests/SalesPathAuditProof/licence/README.md` | works as designed |
| Trial in the payment provider's test mode | Possible with any self-created test account (secret key, a recurring per-seat price, webhook secret); the account needs a terms-of-service URL or the checkout is refused. Read from the code, not run | not verified |
| Licence token lifecycle | Hardening needed, tracked privately | blocker (tracked privately) |

**When Enterprise features turn off:** when the stored token's `validUntil` passes, which is the
last successful heartbeat plus 45 days. With the 24-hour interval a licence-service outage is
tolerated for about 44 days; verification itself is offline.

## 3. Team setup: OIDC with the team's identity provider

| Step | Observed | Time | Rating | Proposed fix | Effort |
|---|---|---|---|---|---|
| A hostname and TLS | The quickstart sets neither. The issuer must be one URL that both the browser and the backend reach; on a quickstart install that means an ingress, DNS and a certificate, and no page walks through it | — | **blocker** for the target customer | A "from quickstart to team" page: ingress host, TLS issuer, IdP client, `ee.admins`, in one `values.yaml` | M |
| `helm upgrade` as documented, HTTP issuer | The new backend pod answers every request with 500 ("The MetadataAddress or Authority must use HTTPS unless … RequireHttpsMetadata=false") and never becomes ready; `helm --wait` blocks 15 min; after an interrupt the release is `pending-upgrade` and needs a rollback. The old pods kept serving | 10+ min lost | **blocker** | Fail at start-up with that message (or reject the combination in the chart); document `oidc.requireHttpsMetadata` | S |
| Same with `oidc.requireHttpsMetadata=false` | Rolled out cleanly | 1 min 14 s | works | — | — |
| First sign-in | Failed until the users were allowed offline tokens: the frontend always adds `offline_access` (`frontend/src/auth.js`), and the IdP refused the code exchange ("Offline tokens not allowed") | — | friction | Document the requirement per IdP, or fall back without it | S |
| Provider list | getting-started says "Any standard OIDC provider works" and names three; the README lists one of the three as not working ("requires ID-token validation — open issue") | — | **blocker** for prospects on that provider | Remove it from the list, or close the open issue | S |
| Who is admin | alice and bob both became admins on first sign-in (bootstrap). The installer's OIDC hint and the getting-started command leave `ee.admins` empty; restricting who may sign in is left to the IdP and documented only in `docs/admin-roles.md` | — | **blocker** (security) | Put `--set ee.admins=<you>` into both commands, plus a warning to restrict sign-in at the IdP | S |
| IdP stand-in start | Keycloak dev mode ready in 4 min 48 s on the emulated VM; one JVM crash on the emulated CPU, a rig artefact | — | — | — | — |

## 4. First session: the target customer's task

| Step | Observed | Time | Rating | Proposed fix | Effort |
|---|---|---|---|---|---|
| New-session dialog | Mode, five agents, billing "Subscription / API key / Own endpoint", model list, Workspace/Terminal, task, repositories. Clear | — | works | — | — |
| Agent account | "No stored Claude subscription login. You can start now and sign in inside the session." Provider login not tested (no account used) | — | not verified | — | — |
| First start (image pull) | The Claude runtime image (516 MB) is pulled on first use; nothing in the UI says why the first session takes minutes | 2 min 49 s pull on the emulated VM | friction | Pre-pull the runtime images during install | S |
| Repository unreachable | Session failed with "clone: Network error: could not reach repository … after 8 attempts over 130 s. Check the cluster's DNS and outbound access" | 130 s | works (clear message) | — | — |
| Autonomous task | Cloned; the agent's shell call was approved automatically; it committed on a branch and pushed it; the session ended "Done" | 4 min 16 s including two 90 s waits | works | — | — |
| Laptop shut | The task's 90-second step ran with no browser attached, and the session finished on its own. Sessions run server-side | — | works | — | — |
| Open the PR | `gh` is installed in the runtime and answered "please run: gh auth login": the PR needs a git token in Settings → Credentials (it also authorises `gh`/`glab`) or the OAuth app the admin configures in the Helm values. Not run against a real host | — | not verified | A short "issue → PR" guide: token scopes, OAuth app, what the agent needs | S |
| Usage page | "$0.00 API spend" for the endpoint sessions; plausible, not checked further | — | works | — | — |

### Second person takes over (Enterprise sharing, test-key backend on the real install)

| Step | Observed | Rating | Proposed fix | Effort |
|---|---|---|---|---|
| alice shares an Interactive session with bob as Collaborator | Share created; bob's home shows "Shared with me · Running · Interactive · Collaborator · by alice" | works | — | — |
| bob opens it | Sees the live conversation and the composer | works | — | — |
| bob types into a session created the way the UI creates it (terminal-backed, Workspace view) | His message reached the agent | works | — | — |
| bob types into a session created through the API with `uiMode: "chat"` | The owner's message goes out as a chat frame and arrives; bob's view sends it as terminal input instead, and it stays "sent, waiting for the agent to take it" indefinitely. Reproduced twice | friction (bug; the UI no longer creates such sessions, the API still accepts them) | The Collaborator view uses the session's transport, as the owner's does | S |

## 5. Documentation: can a newcomer do this alone?

| Topic | Observed | Rating | Proposed fix | Effort |
|---|---|---|---|---|
| Install | Clear, both paths, re-run safety stated | works | — | — |
| Edition, trial, licence | getting-started does not mention editions, the trial or the licence. The Admin page names the checkout and "Retrieve license" in one sentence each and the heartbeat in another, but not the trial terms, that a payment method is required, or what happens when heartbeats fail and the token expires | **blocker** | A "Get Enterprise" page: trial, checkout, activation, offline behaviour, seats, cancellation | S |
| Team setup (OIDC, TLS) | See §3: `requireHttpsMetadata`, `offline_access`, `ee.admins` and hostname/TLS are missing | **blocker** | See §3 | M |
| Agents listed | getting-started lists four agents; the product has five | friction | Add the fifth | S |
| Stale reference | getting-started points to "the 1.0.0-rc.1 Workspace workaround" | friction | Remove it | S |
| Issue → PR | No guide connects credentials, `gh`, OAuth and an autonomous task into the target customer's workflow | friction | See §4 | S |
| Upgrading | The pin-a-tag advice lives only in chart-values | friction | See §1 | S |

## Data flows: what leaves a customer instance

The only project-operated service the product contacts is the licence service, which the chart
points at by default (`ee.license.serviceUrl`). Searched: `backend`, `ee`, `frontend/src`,
`agent-runtime`, `browser-runtime`, `helm`, `mcp`, `artifact-renderer`. There is no update check
and no telemetry to the project: the agents' OpenTelemetry goes to the instance's own backend
(`AgentPodSpecFactory.cs:400-404`), and the version and docs references are links only.

| What | Destination | When | Fields | Code |
|---|---|---|---|---|
| Heartbeat | `<serviceUrl>/api/usage/report` | 30 s after start, then every 24 h, only while a token is stored | Bearer licence token (licence id, organisation, email, seats, plan, dates); body `{ seats }` | `backend/Licensing/SeatUsageReporter.cs:58-62` |
| Checkout | `<serviceUrl>/api/checkout` | Admin clicks "Continue to checkout" | email, organisation, seats, return URL, instance key (the service keeps only its hash) | `backend/Controllers/AdminController.cs:75-104` |
| Claim | `<serviceUrl>/api/license/claim` | Admin clicks "Retrieve license" | email, instance key | `AdminController.cs:161-190` |
| Billing portal | `<serviceUrl>/api/portal` | Admin clicks "Open billing portal" | licence token, email | `AdminController.cs:128-150` |
| Payment page | The payment provider, in the admin's browser | After checkout | What the customer enters there | — |

An empty `serviceUrl` turns the heartbeat off; checkout, claim and portal then answer 501. The
agent CLIs may contact their own vendors independently of the product; that is outside this audit.

What the licence service stores (its private repository; schema checked on a local run):

| Table | Fields | Retention |
|---|---|---|
| licences | id, customer email, organisation, seats, plan, status, valid until, payment-provider customer and subscription ids, created, cancel-at, instance-key hash | No deletion; cancelling sets a status |
| seat reports | licence id, seats, reported at — one row per heartbeat, also for lapsed licences | No deletion |
| seat billings | licence id, year, month, seats, billed at, subscription item id | No deletion |

The payment provider receives the customer email, the price × seats, and the metadata
`{ org, email, seats, return_url?, instance_key_hash? }` on the checkout and the subscription,
plus what the customer enters (payment method, VAT id, terms consent). Granted licences renew
through the heartbeat and are skipped by seat billing.

## Not verified

- A real provider login, and a real pull request on a hosted git service (both need accounts).
- The released binary with an Enterprise licence: no production-signed token was available, so
  sharing ran on the 1.18.0 backend with only the verification key replaced.
- The trial checkout past the payment page, the webhook, the token e-mail and the post-checkout
  activation redirect.
- The macOS and Windows installers, multi-node clusters, air-gapped installs.

## Proposed order for milestones 2 and 3

Milestone 2 (make the evaluation safe and possible):
1. Pin images to the chart version (§1).
2. OIDC hardening and docs together: fail fast on the HTTPS-metadata mismatch, `ee.admins` in
   every OIDC command, `offline_access`, the provider list corrected (§3).
3. Licence token lifecycle hardening (tracked privately), and an Admin warning while heartbeats fail.
4. An evaluation path for Enterprise without a payment method, and the Enterprise offer on the
   website (§2).

Milestone 3 (shorten the path to the first PR):
5. A "from quickstart to team" page with one `values.yaml` (hostname, TLS, OIDC, admins).
6. "Get Enterprise" and "issue → PR" guides; the Admin copy fixes; seats without the API-key user.
7. Pre-pull the runtime images during install; Collaborator input in `chat`-mode sessions; drop
   the unlicensed 402 call; pin the Helm and k3d install scripts.
