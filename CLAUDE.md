# Open AgentHub — working rules

## Identity: this is an independent project

Open AgentHub is a private open-source project by Maik Boltze. Nothing in this repository may
associate it with any employer, past or present.

Concretely:

- **No employer name, brand, or derivative of one** — not in code, comments, commit messages,
  READMEs, configuration, example domains, e-mail addresses, test fixtures, or documentation.
  This rule includes any text stating the rule itself: do not write the forbidden name down in
  order to explain it. If you need to check the repository is clean, ask the owner for the term
  rather than guessing at one and committing the guess.
- **Author and contact are always** Maik Boltze (trading as MB Company), the project address
  `open-agenthub@mail.on-mb.com`, and the GitHub organisation `open-agenthub`.
- **Company-internal environment values** — clusters, registries, hostnames, credentials —
  belong only in the gitignored `deploy/` directory, never in a versioned file. If you find
  such a value in a tracked file, treat it as a bug and say so.

Use `example.com` or `your-org.example` for illustrative hosts.

## Repository layout

| Path | Contents |
|---|---|
| `backend/` | ASP.NET Core (net10.0): REST, WebSocket proxy, Kubernetes orchestration |
| `ee/` | Enterprise Edition sources — source-available under `ee/LICENSE`, **not** AGPL |
| `agent-runtime/` | Per-provider images (claude, codex, cursor, openclaw) over a shared PTY/WS transport |
| `browser-runtime/` | Hardened Chromium with VNC, for the built-in browser MCP |
| `frontend/` | Vue 3 + Vite + xterm.js |
| `helm/open-agenthub/` | The chart |
| `mcp/agenthub/` | stdio MCP server (personal API token); the remote one lives in `backend/Mcp/` |
| `deploy/` | **Gitignored.** Real cluster values and secrets |

Sibling repositories, each with its own checkout: `license-service` (proprietary licence and
shop backend, .NET 8) and `pages` (the landing page, GitHub Pages).

## Before you push

`main` is protected: seven required status checks and a pull request. Work on a branch and open
a PR. Do not push to `main` even if your account is allowed to — the protection does not enforce
against admins, and bypassing it defeats the point.

Run what you touched:

```bash
dotnet test tests/AgentHub.Api.Tests          # 961 pass
cd agent-runtime/session-agent && npm test    # 236 pass
cd frontend && npm test
pwsh ./tests/helm/browser-values.ps1          # chart assertions
```

**Prove a fix before you push it.** Every push runs the full image matrix and a deploy, so a
guessed fix is expensive. Reproduce the failure first — a throwaway console project against the
real SDK, a `curl` against the real endpoint, a container run under the real constraints — then
show the fix changes that reproduction. A green suite is not proof when the fake in the test is
what behaves differently from production; several upload bugs passed for exactly that reason.

For anything touching I/O, verify against a running instance, not only the suite.

## Writing code here

Match the surrounding style. Comments explain **why**, and especially name the failure a choice
prevents:

```csharp
// UseHttp is what decides a presigned url's scheme; it does not follow from ServiceURL and
// defaults to false. Left unset, an http:// endpoint is signed as https:// and the pod
// fetching its state fails the TLS handshake against a plain-HTTP port.
```

Do not add a comment that restates the code. Do not leave commented-out code behind.

## Things that will bite you

- **Session pods run with a read-only root filesystem** and as uid 1000. Anything that writes to
  disk outside `/tmp` or `$HOME` fails at runtime while passing every test.
- **`/tmp` in a session pod is an emptyDir with `fsGroup` set**, so Kubernetes gives it mode 2777
  without the sticky bit. Tools that refuse such a directory disable themselves. `TMPDIR` points
  at a directory the agent owns; leave it that way.
- **The agent runtime ships `node` and `npm` only** — no `dotnet`, `docker`, `trivy` or `pwsh`. A
  session asked to run the .NET suite cannot, unless it is started with `runAsRoot` so it can
  install what it needs, or with a custom image.
- **Autonomous and scheduled sessions auto-approve tool requests**, because nobody is there to
  answer a prompt. An interactive session still asks.
- **Agent CLI versions are pinned per runtime** and verified after install; see
  `docs/agent-runtime-updates.md` before changing one. Cursor is deliberately unpinned and
  OpenClaw deliberately held back — both for reasons documented there.
- **`image.tag` selects every component at once**, so the runtimes have to move together.

## Documentation

`docs/` explains decisions, not features. When you add one, say what the alternative was and
which failure the choice avoids — the existing files do this and are the model to follow.
