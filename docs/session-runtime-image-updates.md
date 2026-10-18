# Updating session runtime images

## Decision

Compare the live agent container's image reference with the image selected by the same pod
factory that starts a session. Custom images remain pinned. Use immutable release tags or
configured digests: a mutable tag changing in a registry cannot be detected by comparing
references, and a registry lookup would introduce credentials and availability dependencies.
The alternative, comparing a CLI version, misses fixes in the transport and base image.

Automatic updates are opt-in (`agent.autoUpdateRuntimeImages`, default false), with a nullable
per-session override. An explicit manual update uses the same safety gate as the sweep.
An outdated-image badge describes the live pod rather than a stored desired version. For a
schedule without an active pod, compare its future-job template instead.

A runtime must positively admit a drain before any running pod is replaced. Absence of user
activity, output, or a pending question is insufficient: a tool may be working silently. The
runtime closes input admission atomically with checking turn completion. The hub releases the
drain on failure when the original pod is still alive and not terminating. A hub crash leaves
input blocked until an explicit retry or ordinary pause/resume, avoiding a timeout reopening
input during pod deletion. The first implementation supports interactive chat, whose structured
result event proves completion. Terminal runtimes and old images without admission support
fail closed; their existing manual pause/resume remains available. Adding terminal support
requires reliable start/stop hooks, including inbox delivery, before enabling admission.

The runtime uploads a fresh archive through its existing persistence path and refuses admission
if that upload fails. Manual pause/resume/delete, edits, expiry, and image updates share a database
advisory lock so another replica cannot replace the pod or change its agent during admission.
Check provider availability and usage limits before draining; a later resume failure leaves the
session paused for an ordinary manual resume rather than starting a fresh conversation.

After admission, recheck pending questions and use the existing pause followed by resume.
Wait for the old pod to disappear before resume so its final state upload cannot race the new
pod's state download. Retain the session ID, agent conversation ID, state archive and scrollback.
No second state-transfer or agent-launch mechanism is introduced. Require configured object
storage; without it there is no durable conversation to restore.

Autonomous tasks are never restarted automatically: resuming their prompt may repeat external
side effects. A scheduled session updates only the CronJob template for future runs; active jobs
finish on their original image. Recreating a CronJob was rejected because it loses scheduler
history and risks duplicate executions. Unsupported agents and active turns remain outdated
until a safe update is possible. Manual update never overrides these restrictions.

## Verification boundaries

Verified with the pinned CLI (2.1.287) inside this branch's real common transport, a local model
endpoint and real HTTP/WebSockets: busy admission returned 409, incorrect authentication returned
401, completed admission uploaded an actual archive, and a new input during drain never reached
the model. A second runtime restored that uploaded archive into a fresh home and resumed the same
conversation ID; the next model request contained both turns. No real login was used.

All 1,996 backend tests passed against rootless Postgres, including the nullable preference
round trip. The frontend suite and production build passed; focused tests cover the badge,
owner-only control and busy feedback. Runtime tests cover failed uploads and initial prompts.
Five unrelated runtime assertions also fail on unchanged main because they read this session's
real saved thread ID while expecting an empty home; this was reproduced from a clean main archive.

A real cluster is still required
to verify graceful deletion, final archive upload/download ordering, image pulling, and CronJob
job creation across a rollout. A mocked cluster API cannot establish those guarantees.
