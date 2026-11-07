# Persistent workspace proof

Run `Quickstart install` with `workflow_dispatch`, the issue branch ref and
`prove_workspace_pvc=true`. This uses the checkout backend, the published installer,
a real k3s kubelet and local-path provisioner. The explicit proof changes the disposable
installation's runtime image to a small Git/httpd image and enables 64Mi session workspaces.
It keeps the real production pod factory, bootstrap init, database, API authentication,
owner credentials, claim admission, lifecycle locks, pause/resume and central deletion.

The Node script reads the generated API key directly into memory, never prints it or
passes it as a process argument. It creates two local Git repositories, changes tracked
and untracked files, and compares all file hashes including Git metadata across pod
replacement. It checks a changed pod UID, unchanged claim UID, retention on pause,
reuse after global admission is disabled, and claim/registry cleanup on deletion.

This does not exercise provider CLIs, source reservation, handover delivery, provider
archives, configured remote cloning or crash recovery. Run `ProviderRuntimeProof` and
the complete provider-switch proof separately. A real storage proof is needed because
an API server without a kubelet cannot establish process death or mounted-file survival.
Generated logs and images stay in workflow scratch space, never in Git.
