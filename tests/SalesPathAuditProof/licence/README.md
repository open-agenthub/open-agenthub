# Licence path, end to end without payment (#480)

Proves grant → activation → heartbeat renewal → offline behaviour → expiry with the real
licence service and the real backend (1.18.0), locally, without Stripe, docker, Kubernetes or
any owner key. The licence service is a private repository: these scripts only run a checkout
of it you already have; nothing of its source is copied here.

## Why the backend has to be rebuilt

The backend verifies licence tokens against a public key compiled into
`backend/Licensing/EnterpriseLicense.cs` (`PublicKeyPem`). It is deliberately not a setting, so
no Helm value or environment variable can swap it, and a token from any other key — including a
local licence service — is rejected as "License token is invalid.". The test seam (a constructor
taking the key) is not used by dependency injection. The least invasive way is therefore a
throwaway build:

1. `git worktree add --detach <scratch> origin/main` (never a branch, never pushed).
2. Replace the two base64 lines of `PublicKeyPem` with those of the throwaway `pub.pem`
   (`start-license-service.sh` generates it). A two-line diff, nothing else.
3. `dotnet build backend/AgentHub.Api.csproj -c Release -o <rig>/backend-bin`, then remove the
   worktree.

## Scripts

| Script | What it does |
|---|---|
| `start-license-service.sh` | Licence service on `127.0.0.1:18500`, SQLite, throwaway ES256 key and admin key, no Stripe key. `TOKEN_LIFETIME_DAYS` overrides the 45-day token lifetime (whole days only). |
| `start-backend.sh` | The rebuilt backend on `127.0.0.1:18090` with API-key login, a local Postgres, `Ee:License:ServiceUrl` at the local service. A private `HOME` with a kubeconfig pointing at `127.0.0.1:1` satisfies the start-up check without a cluster. `REPORT_HOURS=1` keeps the first heartbeat (30 s after start) and the next one an hour later. |
| `sign-token.py` | Signs a token in the service's format with the throwaway key and a validity in **seconds**, to watch expiry happen; the service itself only issues whole days. |

Postgres: see the `rootless-postgres-backend-tests` skill (port 54329, database `agenthub_lic`).

## Steps

```bash
./start-license-service.sh &
curl -X POST http://127.0.0.1:18500/admin/licenses -H "X-Admin-Key: $(cat ~/rig/lic/admin.key)" \
  -H 'content-type: application/json' -d '{"email":"audit@example.com","org":"Audit Example","seats":3}'
REPORT_HOURS=1 ./start-backend.sh &
K="Authorization: Bearer $(cat ~/rig/lic/api.key)"
curl -H "$K" http://127.0.0.1:18090/api/admin/overview          # unlicensed
curl -H "$K" http://127.0.0.1:18090/api/ee/admin/limits         # 402
curl -X POST -H "$K" -H 'content-type: application/json' \
  -d '{"token":"<token>"}' http://127.0.0.1:18090/api/admin/license
```

Results are recorded in `docs/design/sales-path-audit.md`; logs and tokens are not committed.
