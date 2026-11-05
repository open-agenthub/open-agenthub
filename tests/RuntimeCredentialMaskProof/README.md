# Runtime credential masking proof

Run from the repository root with the installed runtime dependency directory and .NET 10 SDK:

```sh
NODE_PATH=/opt/session-agent/node_modules \
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 PROOF_DOTNET=/tmp/434-dotnet/dotnet \
node tests/RuntimeCredentialMaskProof/transport.cjs
```

The script runs the real `common/server.js` with a synthetic agent command in a real PTY. The
command prints environment values, the effective MCP config, and ordinary text, and writes a
native transcript. A stand-in hub records real HTTP/curl uploads and serves Kubernetes Secret
objects to the backend SDK. No external notification is sent.

Assertions cover:

- Callback token, file and environment secrets, custom MCP headers and environment values,
  authorization token suffixes, complete URLs, standalone URL userinfo and query values, including
  encoded and decoded forms, are masked in live transcript reads and pre-persistence replay.
- The source-byte follow cursor, ordinary output, and native resume transcript remain unchanged.
- Owner terminal input echo still contains exactly the typed token.
- All four transcript/scrollback callback and object uploads contain masked output.
- A real callback controller, concrete session service, Kubernetes HTTP SDK and recording notifier
  mask the same values at the hook relay boundary.

For a negative control, extract the base revision's `common/secret-mask.js` to a scratch file and
set `PROOF_SECRET_MASK_SOURCE` to it. The live HTTP assertion fails with `raw credential in output`.
Do not edit the shared checkout or stash work for this comparison.

Captured bodies and other generated output remain in the temporary directory printed by the
script. Only these scripts belong in Git. This proves local transport and notifier behavior;
it does not prove production storage, a particular agent CLI, kubelet projection timing, or
external chat delivery. A live terminal remains an owner/editor interaction, with its existing
input/output behavior; history/replay uses the source masker.
