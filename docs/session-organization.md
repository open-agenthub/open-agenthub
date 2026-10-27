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

A scheduled session moves without pausing, because no process runs between its runs. Its CronJob
template fixes one project-bound thing: the names of the secret variables, as references into
the session's variable copy. Files and project file access are read when they are used. The move
therefore replaces exactly that block of the template, in the form a new session would get.
Rebuilding the whole template was rejected. It would also re-resolve the image, resources,
provider account and presigned URLs, which the owner did not ask to change. It would also re-run
credential preflight, whose failure marks the session failed. Recreating the CronJob was rejected
because it loses the run history. Moving only the registry, as before #295, was rejected because
the next run would start with the old variable names next to the new project's files.

The order prevents a run that mixes both projects:

1. The CronJob is suspended. Annotations record its previous `suspend` value and the project its
   template was built for. The patch carries the resourceVersion, so a concurrent edit fails it.
2. A run in progress is refused with `scheduled_run_active`, and the dialog then offers Stop run
   and move. Stopping deletes the run's Job and pods and waits until the pods are gone, because a
   terminating pod still holds the old variables and a valid callback token. Letting the run
   continue was rejected: the kubelet would bring the new project's files into the pod, and the
   project resolver would give it the new project's file access, next to the old variables.
3. The record is stored with the destination project. Just before that, the service checks again
   for a run that the controller admitted from its cache before the suspension reached it.
4. The secret copies are rewritten. One JSON patch, tested against the resourceVersion, then
   replaces the variable block, restores `suspend` and removes the annotations.

A failure before step 3 restores the schedule as the owner left it. After step 3 the record and the
template disagree, so unsuspending would start a mixed run. The schedule stays suspended and the
session reports `scheduleMoveUnfinished`. The sidebar shows "Move unfinished · schedule paused",
and the dialog offers Finish move. A retry completes the move or moves the session elsewhere. It
rolls back only while the stored record still names the project the template was built for. A
template created before session secrets existed has no variable block. It is refused before
anything changes and has to be recreated in the destination project.
