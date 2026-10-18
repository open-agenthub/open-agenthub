# Session object-storage deletion

Deleting a session removes both `sessions/{owner-key}/{id}/` and
`sessions/{escaped-owner}/{escaped-id}/`. State archives, scrollback, transcripts,
browser cookies, session files and nested artifacts share these prefixes. The
manual path and expiry sweep use the same deletion implementation. Account-wide
purging remains a separate operation.

## Failure decision

Before storage cleanup, deletion removes the CronJob with foreground propagation,
deletes its Jobs and all session pods, and polls until all are absent. A DELETE
response alone is insufficient: a terminating pod uploads its final state. Cluster
errors or a two-minute termination timeout retain the row and storage for retry.

After producers are gone, the row is marked failed with a retry-delete diagnostic.
A scheduled session remains fetchable even though its CronJob is now absent. The
status write does not reset its idle deadline, so expiry retries remain due.

The registry row is removed **after** both object-storage deletions succeed. A
storage exception (including an object-level error inside a successful bulk
response) propagates. The row remains the durable owner/id mapping: retry the
manual delete, or let the expiry sweep retry on its next pass. Earlier teardown
steps may already have stopped the session; a failed request does not promise the
session is still running. Repeating prefix deletion is safe after partial success.

Without object storage configured, deletion proceeds and there is no prefix to remove:
the hub acts on the storage it is configured with now. We rejected refusing deletion in
that case. A removed credential secret looks the same as an installation that never had
storage, and refusing would make sessions undeletable on every installation without it.
An operator who removes the storage configuration after using it owns what is left in
that bucket; once storage is configured again, the orphan cleanup below removes it. Only
a configured store that fails keeps the session row for a retry.

We rejected swallowing storage errors after deleting the registry row: that loses
the retry target. A separate durable tombstone queue would allow the row to vanish
during an outage, but adds another lifecycle and recovery process. Retaining the
existing row provides durable retries without a new table. We rejected an
unconditional startup sweep because operators must review historical cleanup
before any deletion.

Waiting for producer disappearance closes the normal final-upload race. It does
not revoke already-issued presigned upload URLs held outside those producers; the
historical cleanup also covers prefixes recreated through such URLs. This change
does not add object version/lifecycle management.

## Historical cleanup

An authenticated administrator calls `POST /api/admin/session-storage-cleanup/dry-run`.
It only lists immediate owner/session directories below `sessions/`, checks each
session id against the registry across all owners, and returns the orphan prefixes
and a protected `token`. No objects are changed. The token belongs to that admin,
expires after 15 minutes, and authorizes only the displayed prefixes.

Review the returned prefixes, then send `{"token":"<dry-run token>"}` to
`POST /api/admin/session-storage-cleanup/apply`. A request without a valid preview
token cannot delete anything. Before each deletion, apply takes the session
maintenance lock and rechecks the registry. Any existing session id, even under a
different owner encoding, is protected; busy sessions are skipped. Session ids are
generated at creation and are not reused by a restore endpoint.

Each plan contains at most 500 orphan prefixes. `limitReached` means another dry
run is needed after applying this batch. Repeating apply is idempotent; repeating
dry-run/apply also retries failed prefixes. The response and logs contain scanned,
protected/candidate, deleted, skipped and failed counts. Individual failures do not
stop the remaining candidates. A storage-listing or registry failure aborts the
preview rather than producing a deletion plan from incomplete information.

Only reviewed, structurally valid `sessions/{owner}/{id}/` directories are passed
to prefix deletion. `skills/`, other top-level prefixes, and existing sessions are
never candidates. No whole-bucket deletion is used. Storage listing and bulk
removal follow continuation tokens, including prefixes containing more than one
page of artifacts. Run against a dedicated session bucket, not a bucket shared
with unrelated uses of the `sessions/` namespace.

## Proof

`tests/SessionStorageProof` runs the real backend storage store, registry,
advisory locks, deletion service, expiry sweep and admin controllers. It requires
an **isolated, disposable** object-storage bucket and database configured through
`S3__ServiceUrl`, `S3__Region`, `S3__Bucket`, `S3__AccessKey`, `S3__SecretKey` and
`ConnectionStrings__Postgres`. Then run:

```sh
dotnet run --project tests/SessionStorageProof
```

By default the cluster transport and browser teardown are fixtures. Set
`SessionStorageProof__KubeConfig` to a disposable control plane to use real cluster
objects and garbage collection. The proof creates its own namespace and exercises
both a direct pod and CronJob/Job/pod ownership. Finalizers hold real API objects
during real delayed storage uploads after DELETE acceptance, without a kubelet.
The no-wait implementation reproduces a deleted registry row with recreated
objects; the fixed service waits and removes the final uploads. It also proves
cluster authorization errors and termination timeouts preserve data.
The proof asserts manual/expiry deletion, storage denial with
row retention, restored-storage retries, admin-only HTTP access, a read-only
preview, stale-plan protection, repeated apply, more than 1,000 objects in one
prefix, and preservation of existing sessions, skills and other prefixes.
It removes its data after successful assertions. An interrupted proof can leave
synthetic rows/objects in its disposable services.

Verified for this change with rootless storage-server version 2.1.0 and database
version 15, all running as uid 1000: all proof assertions passed and all 2,093
backend tests passed with zero skips. The rootless control-plane proof also passed
with API-server version 1.34.1 and its real garbage collector. Removing the new cleanup call reproduced
`Object retained after success` against a separate bucket; restoring it passed.
A kubelet, real container signal delivery, external presigned-URL holders, and
versioned buckets were not exercised; delayed final uploads use real API finalizers
and real storage requests.

Plans use the backend data-protection key ring. A restart or a request to a replica
without the issuing key rejects the token safely; issue a new preview on the
backend that will apply it (or configure a shared key ring). The proof covers one
backend process, not cross-replica key distribution.
