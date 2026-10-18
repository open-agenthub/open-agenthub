# Stopped sessions in the Workspace

A persistent bar above the conversation and terminal identifies paused, finished, failed,
stopped and scheduled sessions. A modal overlay was rejected because it obstructs saved work,
especially on phones. Running and starting sessions retain existing queue behavior.

Resume follows management permission and the backend canResume flag. Shared-link viewers never
receive the action. Collaborators can write, but the resume endpoint resolves sessions by owner;
offering them Resume would fail. Broader lifecycle permissions need a separate backend decision.

No pause-cause metadata is persisted, so the notice does not infer owner or automatic pause
from timestamps. Startup failures retain the diagnostic panel; autonomous runs retain the
interactive conversion card. The composer keeps drafts, disables stopped-session input, and
explains that resuming is required rather than silently queueing new messages.
