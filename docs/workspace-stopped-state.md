# Stopped sessions in the Workspace

The input area becomes a stopped-state bar for paused, finished, failed, stopped and scheduled
sessions. Workspace replaces its composer; Terminal shows the same bar below its replay. The
original bar above the conversation was rejected after owner feedback: it looked like another
notification while the disabled input still invited typing. A modal overlay was also rejected
because it obstructs saved work, especially on phones. Running and starting sessions retain existing queue behavior.

Resume follows management permission and the backend canResume flag. Shared-link viewers never
receive the action. Collaborators can write, but the resume endpoint resolves sessions by owner;
offering them Resume would fail. Broader lifecycle permissions need a separate backend decision.

No pause-cause metadata is persisted, so the notice does not infer owner or automatic pause
from timestamps. Startup failures retain the diagnostic panel; autonomous runs retain the
interactive conversion card. The composer keeps drafts while unmounted; the stopped-state bar replaces input and links to
known startup failure details. The autonomous continuation card lives in the same bottom area.
Resuming remounts the composer with its draft rather than silently queueing new messages.

## Verification

The browser proof uses the real session controller, session service and sharing access service,
with records seeded through the Postgres store and a rootless API server. Chromium at
1440 × 1000 and 390 × 844 verifies paused, failed, successful and startup-failed interactive
sessions, a finished autonomous run, and a paused legacy stream. It checks readable conversation
and terminal replay, input replaced by the bottom notice, the failure panel, conversion card, and owner
versus viewer/collaborator shared-link actions. The Resume button dispatches the existing resume
endpoint; the request is deliberately aborted before pod creation. No kubelet or real agent CLI
runs in this rig, so resumed workload delivery is not established by this proof.
