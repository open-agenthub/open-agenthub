# Object-storage egress proof

Run `bash tests/ObjectStorageEgressProof/prove.sh` against an installed release with
bundled Garage and NetworkPolicy enforcement enabled. The optional
`RELEASE_NAMESPACE` and `SESSIONS_NAMESPACE` variables default to `agenthub` and
`agenthub-sessions`. The install workflow runs this only with the
`prove_storage_egress` dispatch input.

The probe carries the session component and session identity labels. It must connect
to Garage on TCP 3900 and must fail to connect to an unrelated pod on the same port.
An unrestricted control first connects to that unrelated listener, so the negative
result proves effective policy enforcement rather than a dead destination. Probe pods
are removed on exit. Logs belong in workflow artifacts, never in the repository.

Render regression checks: `pwsh tests/helm/object-storage-values.ps1`.
