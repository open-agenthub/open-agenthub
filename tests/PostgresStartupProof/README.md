# Bundled database startup proof (#416)

Dispatch `install-smoke.yml` on the issue branch with `prove_postgres_startup=true`.
The existing installer creates real k3s and uses this checkout's backend images.
`prove.sh` then starts 15 fresh databases with the chart's original image and
security context, checks TCP readiness, zero restarts and a real CHECKPOINT.
It prints termination state, previous entrypoint output, task confinement labels,
runtime-directory ownership and kernel audit messages before deleting each namespace.
Generated output belongs in the workflow logs or session Files, never in Git.

This first stage gathers evidence before selecting a chart workaround. A failed
signal syscall is different from the entrypoint's tolerated runtime-directory chmod
warning. Do not infer an OOM or blame that directory without termination/audit evidence.
