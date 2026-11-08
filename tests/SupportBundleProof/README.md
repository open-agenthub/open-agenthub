# Support bundle proof

Run `python3 tests/SupportBundleProof/projections.py` for adversarial projection checks and the
negative control proving that adding a new sensitive value makes the canary checker fail.

For real I/O proof, use the disposable quickstart k3s VM in `tests/SalesPathAuditProof/`.
Build this branch's backend and mount it into the deployment at `/app` (test-only hostPath),
then run the real collector in the guest with its installed kubectl and Helm. Never run against
production. Configure distinct synthetic values for API/admin credentials, callback key,
database password, OIDC client secret, test licence token, session prompt, session secret,
user email, event message and unknown Helm field. Keep their exact values in a local JSON array.

Collect both default and opt-in-log bundles. Verify each with:

```sh
python3 tests/SupportBundleProof/check.py bundle.json canaries.json
```

Also run literal `grep -F -f canaries.txt bundle.json` and require status 1. Neither canary
files nor generated bundles/logs belong in Git. Confirm JSON validity, owner-only permissions,
no backendLogs key in the default bundle, real migration rows, licence and OIDC presence flags.
Send a real OIDC administrator request to `/api/admin/diagnostics` and require 200; send a
non-admin request and require 403, unauthenticated request and require 401. Repeat collection
with the OIDC admin access token through stdin and scan for that credential too.
