# Codex Docker Desktop acceptance

Date: 2026-07-19 through 2026-07-21
Base commit: `2b3bf299b70f6c0f4e9ce63f410b6d297a440a91`
Kubernetes context: `docker-desktop`

## Result

Codex support passed the full local regression suite, a four-image Docker Desktop deployment, an isolated API/Kubernetes matrix, pinned-container contracts, and a visible browser checkpoint. Four acceptance or review defects were found with focused failing tests, fixed minimally, rebuilt where applicable, and covered by the final acceptance rerun. Later full-branch hardening passed the local and container checks recorded below, but was not redeployed; the Docker Desktop claims remain limited to the acceptance matrix that actually ran.

No real Claude or Codex account data, host credential storage, browser storage, token, or Kubernetes Secret value was read. All provider material used for matrix testing was unmistakably synthetic and intentionally invalid. Real account-bound Codex device authorization remains a user action and was never completed.

## Environment and final local gates

| Tool | Version |
| --- | --- |
| .NET SDK | 10.0.301 |
| Node.js | 24.16.0 |
| npm | 11.13.0 |
| Helm | 3.19.1 |
| kubectl client | 1.34.1 |
| Docker client/server | 28.5.2 / 28.5.2 |

The final local-only rerun after full-branch hardening produced:

- Backend: 236 passed, 0 failed, 8 skipped PostgreSQL integration tests when no test connection string was supplied.
- Session runtime: 73 passed, 0 failed.
- Frontend: 16 files and 122 tests passed; the production build transformed 80 modules.
- `git diff --check` passed.

The following evidence is from the recorded Docker Desktop acceptance and was not rerun after the latest local-only hardening:

- Helm: default, development, fallback, and explicit-override runtime renders passed; deployment parity passed; lint reported 1 chart and 0 failures; representative development template passed.
- PowerShell parsing passed.
- Focused evidence included 32 policy matcher tests, 17 MCP/common-transport tests, and the Codex pinned-container smoke.
- Disposable PostgreSQL evidence: `dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --no-restore --filter 'FullyQualifiedName=AgentHub.Api.Tests.SessionShareStorePostgresTests.OwnerAccess_MapsProviderNeutralSessionId_WhenLegacyClaudeIdIsNull'` passed 1/1 against PostgreSQL 16 bound only to loopback.

## Images and deployment

The supported `setup-dev.ps1 -NoPortForward` path built and deployed all four images after the fail-fast defect below was fixed. `agenthub-dev` remained healthy throughout acceptance.

| Tag | Local image ID |
| --- | --- |
| `open-agenthub-dev/backend:local` | `sha256:0118a2ad6b705141ba9a969e33de081705cde9fae71f9d908b2d512ce70a89fb` |
| `open-agenthub-dev/frontend:local` | `sha256:4b5f2356b8d0c8c41b6d9fe1052e59182e3f4c45aa693d35341cce081ccd6563` |
| `open-agenthub-dev/agent-runtime-claude:local` | `sha256:5a46ef90e99174d6f7ba26fc01d486079b3fdd1f860bf3562bcabfca88eda6a7` |
| `open-agenthub-dev/agent-runtime-codex:local` | Acceptance: `sha256:fc8439684863ed853eafd20d8f4bc345e945672ddccb681382f5ebceeaf85f66`; post-review rebuild: `sha256:edce1661eba4cdd00855f21525bc84995e8f3f3c9d059c187f93bea163b05de3` |

The post-review Codex image was rebuilt locally and passed the pinned-container smoke; it was not redeployed after acceptance cleanup. The isolated release was `agenthub-codex-acceptance`; its control and session namespaces were `agenthub-codex-acceptance` and `agenthub-codex-acceptance-sessions`.

## Acceptance matrix

| Evidence surface | Case | Outcome |
| --- | --- | --- |
| Kubernetes + WebSocket | Codex Interactive / Subscription without stored auth | Running/Ready session exposed the device-authorization instructions through the user terminal. A reconnect replayed the same 490-character buffered prompt markers. Authorization was not completed. |
| Kubernetes | Missing Codex Subscription for Autonomous | Failed with the selected-credential diagnostic; no Pod or session-scoped Secret was created. |
| Kubernetes | Missing Codex API key for Scheduled | Failed with the selected-credential diagnostic; no CronJob, Pod, or session-scoped Secret was created. |
| Kubernetes | Claude and Codex, Subscription and API key, Interactive and Autonomous | All primary Pod specs used the selected provider image/auth mode and policy. Interactive synthetic fixtures reached Running/Ready. Autonomous fixtures terminated non-zero with transcripts after expected synthetic provider rejection; this is not claimed as real-model execution. |
| Kubernetes | Selected-only credential projection | Subscription Pods mounted only the selected provider state and no provider API-key environment/file projection. API-key Pods mounted no subscription state and referenced only the selected provider key. No service-account token was mounted. |
| Kubernetes | Codex auth watcher | Creation and later change of synthetic `auth.json` updated only the Codex owner Secret key metadata after the real 30-second watcher interval. API-key mode did not update the provider subscription Secret. Values were never inspected. |
| Kubernetes | Codex Scheduled / API key | CronJob used `Forbid`, a valid suspended acceptance schedule, selected Codex image/auth/policy, and a manually triggered Job with `Never` restart policy. Synthetic provider rejection was expected. |
| Kubernetes/API | Edit and duplicate | PATCH round-tripped agent, auth, and structured policy. Duplicate overrides round-tripped Codex/API-key selection and preserved an explicit empty default-deny policy. |
| Pinned container | Codex runtime smoke | Pinned Codex CLI 0.144.5, subscription/API-key setup, MCP configuration, policy deny hook, and missing-key behavior passed. |
| Automated test | Policy | Allowed commands and unsafe compound commands were distinguished; configured failures deny closed. |
| Automated test | MCP | Deterministic stdio and HTTP conversion, safe authorization indirection, filters, and unsupported/ambiguous/secret-bearing rejection passed. |
| Kubernetes + automated test | Terminal/state lifecycle | Live WebSocket reconnect replay passed. Common transport caps replay, archives provider state without auth, scopes API keys to the child, and retries missing resume state only once before fresh launch. |

## Acceptance-criteria mapping

| Criterion | Evidence and outcome |
| --- | --- |
| 1. New and migrated sessions retain provider/auth semantics | Backend model/migration tests passed; API edit/duplicate round-trips passed; the provider-neutral sharing regression passed 1/1 against disposable PostgreSQL. |
| 2. All three modes work with both agents | Codex Interactive, Autonomous, and Scheduled were exercised on Kubernetes. Claude Interactive and Autonomous were exercised on Kubernetes. Claude Scheduled was not instantiated live; its shared scheduled resource path, provider-specific image/auth selection, and Claude scheduled command are covered by backend, Helm, and runtime contract tests. No real-model execution is claimed. |
| 3. Billing is selectable per session for both agents | API and browser flows covered Claude/Codex with Subscription/API key, including edit and duplicate persistence. |
| 4. Codex subscription login/refresh persist without token leakage | Live device login was visible in the terminal; the real watcher interval updated only Codex Secret key metadata. Values and real credentials were never read. |
| 5. Pods receive only selected credentials | The Kubernetes matrix verified selected-only mounts/key references and absence of the unselected provider/auth projection. |
| 6. Autonomous/Scheduled policies deny unmatched actions | Backend matcher, provider hooks, MCP tests, and the pinned Codex deny smoke passed, including unsafe compound-command and failure-closed cases. |
| 7. Claude and legacy behavior remain functional | Claude Kubernetes/runtime regressions passed; legacy database/default/backfill compatibility remains tested. The only limitation is the explicit live Claude-Scheduled gap above. |
| 8. Unit/component/runtime/build/Helm/Docker Desktop acceptance | The recorded four-image deployment, isolated matrix, browser checkpoint, focused PostgreSQL regression, and acceptance-time pinned-container evidence passed. The later local-only full gates passed 236 backend tests with 8 skipped, 73 runtime tests, 122 frontend tests, and the frontend production build. |

Primary synthetic matrix session IDs were retained only during the ephemeral test: Codex `fbc9dfc90f57`, `c1fea83fdb31`, `fe16a60e478a`, `4496e5735f1f`; Claude `296b1b4436a0`, `c8d2ee6c0629`, `4cd702fcfb78`, `5ed7fe1302e0`. Device-login evidence used `31e3949ced70` initially and fresh checkpoint session `90c37fb02525` after the first uncompleted flow timed out naturally.

## Browser checkpoint

The controller inspected the isolated frontend at 720x900 and the normal viewport:

- New Session grouped Agent/Billing controls selected Codex + API key + Autonomous correctly.
- Missing Codex subscription showed the intended device-login hint; stored OpenAI API-key status was visible.
- Advanced policy described empty/default-deny correctly and exposed built-ins, full MCP names/patterns, shell prefixes, and MCP JSON.
- An unsaved MCP value survived Autonomous to Interactive to Autonomous mode changes.
- Credentials displayed the OpenAI key as write-only: empty password input, stored placeholder/chip, and explicit remove action.
- Session detail identified Codex / Subscription and its terminal visibly showed the Codex welcome and device-authorization instructions.
- At 720x900, document and body widths matched the 710-pixel client width; no horizontal overflow was present.

No browser form was submitted and no billable session was started.

## Defects found and fixed with TDD

1. `setup-dev.ps1` continued after a failed native Docker build because PowerShell's stop preference does not turn a native non-zero exit into an exception. The enhanced deployment-parity test first failed; `Assert-NativeSuccess` checks were then added after all four builds, Helm deployment, and rollouts. The supported setup path subsequently completed and rolled out successfully.
2. sharing owner access still selected nullable legacy `claude_session_id`, causing HTTP 500 for a provider-neutral Codex session. A disposable-PostgreSQL integration test first reproduced the null mapping failure; the query and mapper now use `agent_session_id` / `AgentSessionId`.
3. Codex device login ran in the entrypoint before the shared terminal server existed. A runtime test first failed by requiring login inside the PTY while preserving resume arguments; a provider-owned wrapper now performs device auth and then executes Codex under the shared PTY.
4. Review found that the wrapped `bash device-login.sh resume --last` command was not recognized as a resume, so the common one-time fresh fallback was skipped. Focused tests failed first; wrapper-aware recognition now triggers exactly one fresh retry, and the wrapper checks for `auth.json` before device login so that retry does not initiate authorization twice.

The focused disposable-PostgreSQL mapping command above is the durable GREEN evidence for defect 2. An exploratory run of the entire existing PostgreSQL sharing class passed 3/8; five older tests exposed unrelated reader-lifetime and JSON-normalization failures and are not represented as green by this change.

## Post-acceptance full-branch hardening

A combined integration and security review after the recorded Docker Desktop acceptance added the following protections:

- Provider state archives now use separate Claude and Codex object keys, while the legacy state-key overload remains Claude-compatible.
- Provider API keys are environment-only Secret references, scoped to the selected provider child and its descendants; the shared `/shell` and `git-clone` init container receive neither provider API keys nor provider subscription state.
- Codex managed requirements are projected into a read-only system configuration. Custom images receive the managed runtime through an agent read-only mount; only `copy-runtime` can write it.
- Claude built-in, MCP, and shell policy categories cross the pod boundary as separate structured JSON arrays. MCP selectors are validated, shell entries become exact native `Bash(...)` rules, and comma/delimiter injection is rejected.
- The Codex credential watcher establishes a restored-state baseline without re-uploading it, detects creation and refresh races, and retries unchanged valid content after a failed upload without logging credentials.

These latest fixes passed the final local suites and applicable container tests, but were not redeployed after the current Kubernetes context changed externally from `docker-desktop` to `test-gro01`. No context switch or further cluster call was made, so this section does not extend the earlier Docker Desktop deployment claims.

## Compatibility and security review

Remaining legacy names are intentional compatibility surfaces: database migration/backfill fields, the Claude-specific driver session variable, the legacy generic image fallback, and tests that prove provider-neutral public models do not expose the legacy property. The sharing path no longer reads the legacy session ID.

Pod/CronJob inspection used only names, key-presence metadata, environment references, and volume references. Synthetic fixture and added-line scans found no credential content. The main checkout was not modified.

## Cleanup and advisories

After the browser checkpoint, both port-forward PIDs were stopped, Helm release `agenthub-codex-acceptance` was uninstalled, and both exact acceptance namespaces were removed. This also removed every synthetic Secret and session resource. Read-only verification confirmed both namespaces and the release absent, both forwards absent, and all three `agenthub-dev` Pods still Running/Ready.

That cleanup verification was performed while the exact current context was `docker-desktop`. On 2026-07-21 the current context had externally changed to `test-gro01`; the agent did not switch it and made no further cluster call, so no fresh post-resume Docker Desktop recheck is claimed.

Non-blocking existing advisories were kept separate from acceptance failures:

- .NET package vulnerability metadata endpoint was intentionally unreachable in the local gate (`NU1900`).
- The backend container restore reported the existing Kubernetes client moderate advisory (`NU1902`).
- The exploratory full PostgreSQL sharing-class run exposed the five older failures described above; the new provider-neutral regression itself passed 1/1.
- Frontend `npm ci` reported 6 dependency findings: 3 moderate, 1 high, and 2 critical.
- Vite reported the existing JavaScript chunk-size advisory above 500 kB.
- Helm recommended adding a chart icon.
