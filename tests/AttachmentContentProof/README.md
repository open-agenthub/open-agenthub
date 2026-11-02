# Attachment content proof

Message attachments use the authenticated backend content route already used by `read_file`.
Opening additional pod egress ports was rejected: attachments should work with the default
policy and should not need the pod to know the storage network. Download URLs in materialize
metadata remain for compatibility; the new runtime ignores them and constructs its own
session-scoped route. Redirects are refused so a backend/proxy response cannot send the callback
token or attachment request to another endpoint. Content streams into the bounded local store,
with an exact size check before the path is delivered.

This proof reuses `tests/FileTypesProof`'s real HTTP host: shipped controllers, callback
authorizer, file service, Postgres stores, and storage SDK against real object storage. No fetch,
file-service, registry or storage fake participates. The provider CLI is outside this proof;
it invokes the shipped attachment materializer directly, the same code used by the session agent.

The coordinator accepted a closed public storage port instead of constructing a cluster:
**no real NetworkPolicy is tested**. This proves the attachment code has no dependency on the
presigned endpoint, while the backend continues to access storage over its internal endpoint.
It does not prove CNI enforcement or chart policy behavior.

1. Start disposable Postgres on `127.0.0.1:54329` and storage on `127.0.0.1:3910`, as described in
   `tests/FileTypesProof/README.md`. Leave port 3919 closed. Use bucket `file-proof`, region `local`.
2. Build `dotnet build tests/FileTypesProof/Proof.csproj`.
3. From the repository root, run
   `PROOF_S3_KEY_FILE=<private storage key file> DOTNET=<SDK executable> node tests/AttachmentContentProof/start.mjs`.
   Credentials remain outside the repository. The host listens only on loopback.
4. Extract the unchanged runtime to scratch, retaining the relative layout:
   `git archive d588453 agent-runtime/files | tar -x -C <baseline directory>`.
5. Run
   `PROOF_BASELINE=<baseline directory>/agent-runtime/files/materialize.js node tests/AttachmentContentProof/proof.mjs`.
   It uploads 2 MiB + 17 bytes through the real backend, asserts direct public storage and the old
   materializer both fail with connection refused, then verifies the fixed attachment's size,
   SHA-256 and byte equality. Both sides use fresh local stores. Foreign/missing/invalid tokens
   fail with 401, and a session's own token cannot read another session's file (404).
6. Run `node --test agent-runtime/session-agent/test/files-materialize.test.js` for the real HTTP
   redirect refusal and streamed oversize/truncation checks. Run
   `dotnet test tests/AgentHub.Api.Tests --filter FullyQualifiedName~InternalSessionFilesTests`
   for real callback-authorizer and nonredirecting streaming-controller regression coverage.

Generated logs, storage data, local SDK, database and extracted baseline belong in scratch,
never in Git. The proof retains its synthetic upload in these disposable services.
