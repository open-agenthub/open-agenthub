# Repository checkpoint proof

Run `node tests/ProviderRepositoryProof/inspect.mjs`. It creates actual Git repositories,
a read-only HTTP remote, real clones and local changes, and runs the production inspection
module. Ignored files must not block and their contents must not enter the result.
The other controls exercise saved-commit and checkout refusals. Everything is temporary;
no credentials, model services or production endpoints are used.

This is a repository-inspection proof only. It does not establish target credential
projection, pod replacement, native CLI handover or k3s recovery. Those remain the
combined no-PVC acceptance gate. Generated evidence is never committed.
