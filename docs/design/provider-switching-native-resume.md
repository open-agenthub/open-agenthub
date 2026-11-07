# Native state on completed replacement resume

A resumed session continues its current native conversation. Completed replacement
generations require their own saved archive; dirty workspace files and visible history
cannot reconstruct that native context.

Check archive availability before deleting a surviving pod or changing session status.
Require successful restoration before native CLI launch, omit the original task from the
resume environment, and prevent a failed native resume from falling back to a fresh thread.
Runtime maintenance checks the same generation-specific state key that the pod restores.
Generation-zero sessions retain their existing compatibility behavior.

A real Codex negative control sent the original task to the recording model when state was
absent. The corrected runtime exits before native launch with no such request. Positive
controls restore actual Claude/Codex archives into fresh homes, continue the same native
identity/history, and persist a new owner turn. The local fixture preserves the absolute
HOME path while replacing its contents, matching a real pod; native indexes can retain
absolute rollout paths.

Reject using the latest provider archive: returning to a provider can create several
independent generations. Reject a fresh task as fallback: it does not resume the saved
conversation. Restore the current archive and retry instead. Full crash/fault acceptance
remains separate in #529; the persistent-workspace flag stays opt-in/default off.
