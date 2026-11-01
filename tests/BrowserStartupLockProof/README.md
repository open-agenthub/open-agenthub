# Browser startup lock proof

From the repository root, set `AGENTHUB_TEST_POSTGRES_CONNECTION_STRING` to a running disposable
database and run `bash tests/BrowserStartupLockProof/run.sh`. Each database test creates and drops
an isolated schema. The real lease store and advisory lock use separate shared and lock pools.

The 24-session test uses three service instances, two lock connections and a two-second pool
acquire timeout. All pods must be created before any readiness response succeeds; readiness is
held beyond that timeout. The concurrency test verifies six service instances adopt one pod.
The stop test holds an old readiness read while a second instance stops and a third restarts;
the old waiter must fail clearly and leave the new pod and Running lease intact.

To demonstrate the regression, run the same test files with the original service implementation:
the 24-session test cannot create all pods, and Stop cannot pass the blocked readiness read.
Generated logs belong outside the repository. These tests simulate the cluster API; they do not
claim a live cluster proof.
