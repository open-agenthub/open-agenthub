# Updating session runtime images

## Decision

Compare the live agent container's image reference with the image selected by the same pod
factory that starts a session. Custom images remain pinned. Use immutable release tags or
configured digests: a mutable tag changing in a registry cannot be detected by comparing
references, and a registry lookup would introduce credentials and availability dependencies.
The alternative, comparing a CLI version, misses fixes in the transport and base image.

Automatic updates are opt-in (`agent.autoUpdateRuntimeImages`, default false), with a nullable
per-session override. The initial manual update attempt uses the same safety gate as the sweep.
An outdated-image badge describes the live pod rather than a stored desired version. For a
schedule without an active pod, compare its future-job template instead.

A runtime must positively admit a drain before any running pod is replaced. Absence of user
activity, output, or a pending question is insufficient: a tool may be working silently. The
runtime closes input admission atomically with checking turn completion. The hub releases the
drain on failure when the original pod is still alive and not terminating. A hub crash leaves
input blocked until an explicit retry or ordinary pause/resume, avoiding a timeout reopening
input during pod deletion. The first implementation supports interactive chat, whose structured
result event proves completion. Terminal runtimes and old images without admission support
fail closed for automatic updates. The manual fallback below uses ordinary pause/resume.
Adding terminal support requires reliable start/stop hooks, including inbox delivery, before enabling admission.

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
until a safe update is possible. The manual fallback never restarts an autonomous task or
changes a custom image.

## Confirmed manual pause and resume

When Update now cannot confirm idle (terminal mode, an older runtime without drain admission,
a busy turn, an unreachable runtime, or a pending question), the API returns a specific
confirmation-required conflict with a plain explanation. The owner can cancel or choose
“Pause and resume now”. The confirmation warns that the hub cannot tell whether the agent is
mid-turn and pausing may interrupt work or a pending question. An ordinary maintenance conflict
or missing storage does not offer this action. Viewers and collaborators cannot use either
update endpoint: both resolve the session through the authenticated owner's store lookup.

The confirmed endpoint `POST /api/sessions/{id}/pause-and-resume-runtime` acquires the same
maintenance lock as drain, pause, resume, delete and edits. It checks that the session is still
running and interactive, uses a managed image, and has state storage before pausing. Provider
availability and usage checks happen before interruption. It invokes the existing manual pause,
whose SIGTERM path uploads state, waits for the old pod to disappear, checks that a conversation
archive exists, then invokes the existing resume with the current configured image. The session
and agent conversation IDs are retained. If the archive is absent, the session remains paused.
If resume fails after pause, ordinary Resume can retry.

This explicit choice does not infer idle from silence, bypass drain in the automatic sweep, or
introduce another upload or launch mechanism. Reusing the manual pause path was chosen over
forcing admission on an old runtime: the hub cannot prove that old or terminal transports have
finished a turn. The confirmation accepts the same interruption risk as ordinary manual pause;
it cannot guarantee that a mid-turn tool completed or that a failed final upload replaced an
older archive. Waiting for pod disappearance prevents the final upload racing the new download.

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

For #181, real HTTP tests host the concrete sessions controller and Kubernetes session service.
They cover terminal mode, a runtime returning 404, busy admission, pending questions, foreign-owner
404, maintenance contention, configured pause grace, old-pod disappearance before create, the
current image and resume environment, missing archives, and restart restrictions. Cluster responses
and storage are fixtures in these tests; they prove backend ordering rather than actual scheduling.
The backend suite passed 1,967 tests; 148 database-dependent tests were skipped without Postgres.
The full frontend suite passed (767 tests) and its production build passed.

The pinned CLI 2.1.287 also passed the real common transport proof again. An additional run held a
model turn open in both chat and terminal modes, confirmed drain rejection, sent SIGTERM through ordinary manual pause, and restored
the actual uploaded archive in a fresh home. The resumed CLI used the same conversation ID and its
next model request contained both prompts. This establishes the runtime's manual shutdown/archive
behavior separately from the backend's cluster transport ordering.
