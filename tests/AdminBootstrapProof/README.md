# Explicit admin designation — proof (#492)

`proof.mjs` runs the real backend in OIDC mode. Nothing on the request path is mocked:

| Piece | What runs |
|---|---|
| Backend | the real `AgentHub.Api` dll, on 127.0.0.1:8492, restarted per scenario with a different `Ee:Admins` |
| Login | real JWT bearer validation: the backend fetches discovery and JWKS from a local issuer in the script (127.0.0.1:9492, `Oidc:RequireHttpsMetadata=false`), which signs RS256 tokens with a key that exists only in memory |
| Database | real Postgres; the scenarios share one database, so the per-user role set in one survives into the next restart |
| Kubernetes | none; the backend gets its own `HOME` with a kubeconfig that points nowhere |

Setup: Postgres as in the `rootless-postgres-backend-tests` skill, an **empty** database (the
script expects nobody to be designated at the start), `openssl` on PATH for a throwaway
data-protection certificate.

```bash
createdb -h 127.0.0.1 -p 54329 -U postgres proof492
dotnet build backend                     # branch; and main in a detached worktree for the baseline
PROOF_DB='Host=127.0.0.1;Port=54329;Username=postgres;Database=proof492' \
  node tests/AdminBootstrapProof/proof.mjs <branch>/backend/bin/Debug/net10.0/AgentHub.Api.dll \
                                           <main>/backend/bin/Debug/net10.0/AgentHub.Api.dll
```

Scenarios:

1. **Baseline (main), nobody designated:** an OIDC user who just signed in gets `isAdmin: true`
   and 200 on `GET /api/admin/roles` — the defect.
2. **Branch, nobody designated:** two OIDC users get `isAdmin: false`, `adminDesignated: false`,
   403 on the roles list and 403 when making themselves admin. The backend logs the startup warning.
3. **Branch, `Ee:Admins=alice`:** alice is admin; bob is not and gets `adminDesignated: true` (no
   notice); alice makes bob an admin in the admin area.
4. **Branch, `Ee:Admins` removed again** (an existing install with an admin designated in the
   database): bob stays admin, alice and a new user are users, and bob cannot demote himself as the
   last admin (409).

5. **Branch, API-key mode** with owner `keyholder`: the key creates a project.
6. **Branch, switched to OIDC**, `Ee:Admins=bob`: the startup log warns that data is under
   `keyholder`; bob's `/api/admin/access` lists `{owner: keyholder, sessions: 0, projects: 1}`; the
   provider user `keyholder` is not admin and sees no counts; bob promoting `keyholder` gets 409.

Group mappings are Enterprise and licence-gated; they are covered by `AdminRolesPostgresTests`
against the real stores, not here.
