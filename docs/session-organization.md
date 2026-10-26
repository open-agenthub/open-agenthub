# Session order and project changes

Sidebar order is a per-owner server preference, independent of session creation times and project
order. A separate table avoids rewriting session records and racing the workload reconciler.
Replacing the order is transactional and serialized per owner; every submitted session must
belong to that owner or the whole replacement rolls back. Local storage was rejected because
it cannot preserve the order across devices. Shared sessions remain in their existing group;
only owned sessions can be reordered or moved. Newly created sessions follow the saved order.

Drag a row before another row to reorder, or onto a project header to move. The Organize session
button provides the same operations using native buttons and a native dialog for keyboard and
touch. Project changes require confirmation explaining secret selection and file access.
Individual grants and the session's own files remain; narrowed secret selections stay narrowed.
Project file access changes immediately through the existing project access resolver.

An active session must pause before its project changes. Keeping it running was rejected:
environment variables cannot be removed from a running process, and the old project's secrets
would remain usable after the new project's files became accessible. The move dialog offers
Pause and move; the session remains paused for an explicit resume under the new configuration.
The service enforces this rule for other update clients too, under its existing maintenance lock.
A failed move after a successful pause leaves the session paused and reports recovery guidance.

Scheduled sessions currently use immutable workload templates and cannot pause. The dialog
explains that they must be recreated in the destination project rather than silently moving
only the registry and leaving the old template and its secrets active. Scheduled template
replacement is tracked separately in issue #295.
