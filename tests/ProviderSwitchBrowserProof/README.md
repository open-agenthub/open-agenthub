# Owner provider-switch review browser proof

Install the frontend's locked dependencies. Start its Vite binary from the repository root:

```sh
./frontend/node_modules/.bin/vite --config tests/ProviderSwitchBrowserProof/vite.config.mjs
```

With the session's integrated browser running, set `PROOF_CDP` to its returned endpoint,
`PROOF_URL` to this session's reachable port 5173 origin, and `PROOF_OUT` to an absolute
scratch output directory. Run `node tests/ProviderSwitchBrowserProof/run.mjs`.

The maintained page mounts the actual product component with isolated API fixtures. Real
Chromium checks default-off admission refusal, repository readiness, ignored/outside-file acknowledgement,
a reviewed commit/push request and explicit recheck, a clear starting reason with no review
form until availability succeeds, exact handover review, review
invalidation after edits, request-ID generation over an insecure HTTP origin, pending input,
cancellation before source stop, retry afterwards, and desktop/phone wrapping. Screenshots
stay in scratch space or Files, never Git. The proof uses the managed browser's default
context; a separately created context may have its blank page moved/closed by browser policy.
It closes only its own proof page and disconnects.

These fixtures establish UI behavior, not backend authorization, native delivery, storage
persistence or provider login. Pair them with `ProviderSwitchHttpTests`, the real CLI target
capture and the real kubelet workspace proof. Owner visibility is enforced by the actual
Workspace parent and all mutation routes still look up the owner-scoped registry row.
