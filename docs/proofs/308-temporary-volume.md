# Sticky temporary directories

The pod's fsGroup makes the emptyDir root-owned and mode 2777. Image permissions
are hidden by that mount, and an unprivileged entrypoint cannot chmod its root.
Claude Code 2.1.287 still warns about /tmp even when TMPDIR is private.

A restricted init container creates an owned child directory and sets it to 1777.
Consumers mount that child through subPath at /tmp. The init runs as uid 1000,
with a read-only root, no capabilities and no privilege escalation. Each image
also sets its unmounted /tmp to 1777 for standalone use.

A root init was rejected because it violates restricted admission. Changing only
TMPDIR was rejected because the actual CLI startup still emits the warning.
A CLI-specific socket override would leave the shared temporary mount unsafe.

Proof in progress: scripts/proofs/308 preserves the disposable worker setup.
The real isolated CLI reproduced the reported warning; final running-pod results
will be added after the worker is ready.
