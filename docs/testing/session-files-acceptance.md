# Session files acceptance

Last verified: 2026-08-01 on Windows, PowerShell 7, .NET 10, Node 22, Helm, and Docker Desktop.

## Automated matrix

| Area | Command | Result |
|---|---|---|
| Backend contracts, authorization, storage, lifecycle | `dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --no-restore` | 610 passed, 52 PostgreSQL-dependent tests skipped |
| Renderer isolation/conversion unit tests | `dotnet test tests/ArtifactRenderer.Tests/ArtifactRenderer.Tests.csproj --no-restore` | 7 passed |
| Shared agent runtime, attachments, Files MCP | `npm test` in `agent-runtime/session-agent` | 193 passed |
| Chat attachments, previews, Files/Browser workspace | `npm test` in `frontend` | 280 passed |
| Frontend production bundle | `npm run build` in `frontend` | passed |
| Helm defaults, overrides, security context, no-egress | `pwsh -File tests/helm/files-values.ps1` | passed |
| Static/Helm deployment parity | `pwsh -File tests/helm/deployment-parity.ps1` | passed |
| Renderer container and LibreOffice executable | `pwsh -File artifact-renderer/smoke.ps1` | passed, LibreOffice 24.2.7.2 |

PostgreSQL integration cases are skipped automatically when their dedicated test connection
is not configured. The .NET restore emitted `NU1900` because the package advisory feed was
unreachable; builds and tests themselves succeeded.

## Live Docker Desktop smoke

Run:

```powershell
pwsh -File tests/files-browser-smoke.ps1 -RequireCluster
```

The script generates deterministic PNG, Markdown, text, HTML, and SVG fixtures in a temporary
directory, creates a real session, exercises reserve/upload/complete/list/content, checks direct
versus download-only capabilities, presents the image through the token-authenticated managed
Files callback, and verifies the shared presentation revision. Temporary material is removed in
`finally`.

The current verification host had the `docker-desktop` context selected, but its Kubernetes API
was unavailable. Therefore live provider image analysis, collaborator/viewer/share-link HTTP
roles, pod-expiry behavior, and the MinIO pause/resume persistence matrix were not executed here.
The script reports this as a skip by default and fails with `-RequireCluster`; it must be rerun on
the documented local cluster before a release candidate is promoted.

## Covered security boundaries

- Five-file and byte limits, MIME mismatch rejection, and traversal/symlink rejection are unit-tested.
- Owner, collaborator, viewer, and share-link permissions are controller-tested.
- Callback tokens are session-scoped and never placed in URLs.
- HTML and SVG are download-only; PDF responses are sandboxed.
- Office conversion is opt-in, non-root, read-only-root, temporary-volume bounded, and no-egress.
- S3 configuration selects persistent storage; its failures do not silently fall back to pod storage.
- Without S3, capabilities explicitly report temporary pod storage.
