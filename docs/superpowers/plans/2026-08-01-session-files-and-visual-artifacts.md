# Session Files and Visual Artifacts Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add provider-neutral session files, true visual image prompts in structured chat, an `agenthub_files` MCP, temporary pod storage without S3, persistent S3 storage, and a Files workspace with optional Office previews.

**Architecture:** Postgres stores file metadata and shared presentation revisions while binary content is either persisted in S3 or kept below a managed directory in the live agent pod. A backend service owns validation, authorization, quotas, and storage selection; every runtime receives the same managed files MCP and attachment contract; the Vue workspace presents supported content beside chat or terminal. The approved design is `docs/superpowers/specs/2026-08-01-session-files-and-visual-artifacts-design.md`.

**Tech Stack:** .NET 10 / ASP.NET Core, Npgsql 10, AWSSDK.S3, KubernetesClient, Node.js CommonJS and ESM, MCP SDK 1.29.0, Vue 3.4, Vitest 2.1, Helm, Kubernetes, LibreOffice headless.

## Global Constraints

- Never introduce the forbidden company name or any derived internal company identifiers; use only Maik Boltze, `open-agenthub`, and project addresses from `AGENTS.md`.
- Preserve unrelated staged and unstaged user changes. Stage and commit only the files named by the active task.
- File IDs, never presigned URLs, pod IPs, object keys, or uncontrolled paths, cross user-facing trust boundaries or enter transcripts.
- S3 mode is persistent; pod mode is temporary. An S3 failure must not silently fall back to pod storage.
- Defaults: 5 attachments/message, 20 MiB/image, 50 MiB/document, 50 MiB/message, 200 files/session, 1 GiB/session.
- Accepted images: PNG, JPEG, WebP, GIF. Accepted documents: PDF, Markdown, bounded text, DOCX, PPTX, XLSX. HTML and SVG are download-only.
- Owner/collaborator may upload and present; only owner/manager may delete; viewer/share link may list, preview, and download only.
- The Office renderer is shipped but disabled by default and must run non-root, without egress, with resource and wall-clock limits.
- Every behavior change follows Red-Green-Refactor. A production change is written only after its focused test failed for the expected missing behavior.
- Before each commit, run the focused tests plus the nearest affected suite. Before final completion, run all backend, runtime, frontend, Helm, and renderer verification commands in Task 13.

## File Responsibility Map

- `backend/Files/SessionFileModels.cs`: enums, immutable records, DTO-independent domain values.
- `backend/Files/SessionFileOptions.cs`: validated limits, TTLs, renderer and polling configuration.
- `backend/Files/SessionFileValidator.cs`: filename, extension, MIME, magic-byte, batch, and quota decisions.
- `backend/Files/SessionFileRegistry.cs`: registry interface and Postgres implementation for file and presentation rows.
- `backend/Files/SessionFileStorage.cs`: S3/pod storage selection and object/pod adapters.
- `backend/Files/SessionFileService.cs`: reservation, completion, reads, deletion, presentation, materialization, and cleanup orchestration.
- `backend/Files/SessionFilesController.cs`: authenticated user routes.
- `ee/backend/Sharing/SharedSessionFilesController.cs`: token-share read routes only.
- `backend/Files/InternalSessionFilesController.cs`: callback-token routes used by the runtime MCP.
- `backend/Files/SessionFileSweepService.cs`: abandoned reservation and stale pod-file reconciliation.
- `backend/Files/OfficePreviewClient.cs` and `SessionFilePreviewWorker.cs`: async renderer queue and PDF derivative lifecycle.
- `agent-runtime/files/local-store.js`: bounded, canonical managed-root file operations.
- `agent-runtime/files/client.mjs`: internal backend client with bounded responses and stable errors.
- `agent-runtime/files/server.mjs`: `agenthub_files` MCP tools.
- `agent-runtime/files/configure.mjs`: collision-safe managed MCP registration.
- `agent-runtime/files/materialize.js`: attachment metadata fetch, download, and prompt-safe local representation.
- `frontend/src/lib/attachments.js`: browser-side type/size/batch validation and paste/drop deduplication.
- `frontend/src/components/ChatAttachments.vue`: pending and transcript attachment cards.
- `frontend/src/components/FilesPane.vue`: file list, presentation polling, and preview routing.
- `frontend/src/components/FilePreview.vue`: direct image/PDF/Markdown/text/Office state rendering.
- `artifact-renderer/`: isolated conversion service and container.

---

### Task 1: Domain contracts, options, and content validation

**Files:**
- Create: `backend/Files/SessionFileModels.cs`
- Create: `backend/Files/SessionFileOptions.cs`
- Create: `backend/Files/SessionFileValidator.cs`
- Create: `tests/AgentHub.Api.Tests/SessionFileValidationTests.cs`
- Modify: `backend/appsettings.json`

**Interfaces:**
- Consumes: no new application interfaces.
- Produces: `SessionFileRecord`, `SessionFilePresentation`, `SessionFileCapabilities`, `SessionFileOptions`, `SessionFileValidator.ValidateReservation`, `SessionFileValidator.DetectAsync`, and `SessionFileValidator.ValidateBatch`.

- [ ] **Step 1: Write failing reservation and batch-limit tests**

```csharp
[Theory]
[InlineData("shot.png", "image/png", 20 * 1024 * 1024, true)]
[InlineData("shot.png", "image/png", 20 * 1024 * 1024 + 1, false)]
[InlineData("payload.svg", "image/svg+xml", 128, false)]
public void Reservation_enforces_the_image_allowlist_and_limit(
    string name, string mime, long size, bool allowed)
{
    var result = SessionFileValidator.ValidateReservation(
        new SessionFileOptions(), name, mime, size);
    Assert.Equal(allowed, result.Allowed);
}

[Fact]
public void Chat_batch_rejects_six_files_even_when_total_bytes_are_small()
{
    var files = Enumerable.Range(0, 6)
        .Select(i => ReadyFile($"f{i}", 10))
        .ToArray();
    Assert.Equal("attachment_count_exceeded",
        SessionFileValidator.ValidateBatch(new SessionFileOptions(), files).Code);
}
```

- [ ] **Step 2: Run the focused tests and verify RED**

Run: `dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --filter FullyQualifiedName~SessionFileValidationTests`

Expected: build fails because `AgentHub.Api.Files.SessionFileValidator` and its domain records do not exist.

- [ ] **Step 3: Add exact domain types and defaults**

```csharp
public enum SessionFileStorageKind { S3, Pod }
public enum SessionFileState { Reserved, Uploading, Ready, Failed, Expired, Deleted }
public enum SessionFilePreviewState { None, Queued, Converting, Ready, Failed }

public sealed record SessionFileRecord(
    string Id, string SessionId, string Owner, string Name, string Extension,
    string DeclaredMimeType, string? DetectedMimeType, long Size,
    SessionFileStorageKind StorageKind, string StorageLocator,
    SessionFileState State, SessionFilePreviewState PreviewState,
    string? PreviewFileId, string Creator, string Source,
    DateTime CreatedAt, DateTime? CompletedAt, DateTime? ExpiresAt);
public sealed record SessionFilePresentation(
    string SessionId, string? FileId, long Revision, string Presenter, DateTime UpdatedAt);
public sealed record SessionFileUsage(int Count, long Bytes);
public sealed record SessionFileCapabilities(
    string StorageMode, bool UploadAvailable,
    IReadOnlyList<string> DirectPreviewMimeTypes,
    IReadOnlyDictionary<string, string> UnavailableReasons,
    bool OfficePreviewEnabled, string OfficePreviewStatus, SessionFileOptions Limits);

public sealed class SessionFileOptions
{
    public long MaxImageBytes { get; init; } = 20L * 1024 * 1024;
    public long MaxDocumentBytes { get; init; } = 50L * 1024 * 1024;
    public long MaxMessageBytes { get; init; } = 50L * 1024 * 1024;
    public int MaxMessageFiles { get; init; } = 5;
    public int MaxSessionFiles { get; init; } = 200;
    public long MaxSessionBytes { get; init; } = 1024L * 1024 * 1024;
    public int PresignMinutes { get; init; } = 10;
    public int ReservationMinutes { get; init; } = 15;
    public int PresentationPollMilliseconds { get; init; } = 1500;
}
```

Implement literal extension/MIME maps and magic detection for PNG signature, JPEG SOI, GIF87a/GIF89a, WebP RIFF+WEBP, `%PDF-`, UTF-8 text, and ZIP-based Office packages by reading `[Content_Types].xml` with `ZipArchive`. Return stable codes `unsupported_file_type`, `file_too_large`, `attachment_count_exceeded`, `attachment_bytes_exceeded`, and `content_type_mismatch`.

- [ ] **Step 4: Run focused and backend suites**

Run: `dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --filter FullyQualifiedName~SessionFileValidationTests`

Expected: all validation tests pass, including hand-built magic-byte fixtures for every allowed type and mismatch cases.

- [ ] **Step 5: Commit the domain slice**

```powershell
git add backend/Files/SessionFileModels.cs backend/Files/SessionFileOptions.cs backend/Files/SessionFileValidator.cs backend/appsettings.json tests/AgentHub.Api.Tests/SessionFileValidationTests.cs
git commit -m "feat(files): define session file contracts and validation"
```

---

### Task 2: Postgres registry and presentation revisions

**Files:**
- Create: `backend/Files/SessionFileRegistry.cs`
- Create: `tests/AgentHub.Api.Tests/SessionFileRegistryPostgresTests.cs`
- Modify: `backend/Program.cs`

**Interfaces:**
- Consumes: `SessionFileRecord`, `SessionFileState`, `SessionFilePreviewState` from Task 1.
- Produces: `ISessionFileRegistry` and `PostgresSessionFileRegistry` with initialization, reservation, state transition, quota, list, presentation, preview-claim, expiry, and deletion methods.

- [ ] **Step 1: Write failing registry integration tests**

```csharp
[Fact]
public async Task Presentation_revision_increases_and_never_returns_a_stale_selection()
{
    await using var db = await SessionFilePostgresFixture.CreateAsync();
    await db.Registry.InsertAsync(File("f1", "s1"));
    var first = await db.Registry.SetPresentationAsync("s1", "f1", "alice");
    var second = await db.Registry.SetPresentationAsync("s1", null, "alice");
    Assert.Equal(1, first.Revision);
    Assert.Equal(2, second.Revision);
    Assert.Null((await db.Registry.GetPresentationAsync("s1"))!.FileId);
}

[Fact]
public async Task Ready_usage_excludes_failed_expired_and_deleted_rows()
{
    await using var db = await SessionFilePostgresFixture.CreateAsync();
    await db.SeedAsync(
        File("ready", "s1", SessionFileState.Ready, 11),
        File("failed", "s1", SessionFileState.Failed, 13),
        File("gone", "s1", SessionFileState.Deleted, 17));
    Assert.Equal(new SessionFileUsage(1, 11), await db.Registry.GetUsageAsync("s1"));
}
```

- [ ] **Step 2: Run the Postgres tests and verify RED**

Run: `dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --filter FullyQualifiedName~SessionFileRegistryPostgresTests`

Expected: build fails because `ISessionFileRegistry` is missing. The test fixture uses `AGENTHUB_TEST_POSTGRES`; when absent it reports a skipped integration test rather than passing a fake registry.

- [ ] **Step 3: Implement the registry contract and SQL**

```csharp
public interface ISessionFileRegistry
{
    Task InitializeAsync(CancellationToken ct = default);
    Task InsertAsync(SessionFileRecord file, CancellationToken ct = default);
    Task<SessionFileRecord?> GetAsync(string sessionId, string fileId, CancellationToken ct = default);
    Task<IReadOnlyList<SessionFileRecord>> ListAsync(string sessionId, CancellationToken ct = default);
    Task<SessionFileUsage> GetUsageAsync(string sessionId, CancellationToken ct = default);
    Task<bool> TransitionAsync(string sessionId, string fileId, SessionFileState expected,
        SessionFileState next, string? detectedMime, long? actualSize, CancellationToken ct = default);
    Task<SessionFilePresentation?> GetPresentationAsync(string sessionId, CancellationToken ct = default);
    Task<SessionFilePresentation> SetPresentationAsync(string sessionId, string? fileId,
        string presenter, CancellationToken ct = default);
    Task<SessionFileRecord?> ClaimPreviewAsync(CancellationToken ct = default);
    Task LinkPreviewAsync(string sourceId, string previewId, bool succeeded, CancellationToken ct = default);
    Task<IReadOnlyList<SessionFileRecord>> ExpireReservationsAsync(DateTime cutoff, CancellationToken ct = default);
    Task MarkSessionDeletedAsync(string sessionId, CancellationToken ct = default);
}
```

Create both tables and indexes idempotently. Use `INSERT ... ON CONFLICT (session_id) DO UPDATE SET revision = session_file_presentations.revision + 1` for presentation changes and `FOR UPDATE SKIP LOCKED` for preview claims. Map every enum as its exact name and every timestamp as UTC.

- [ ] **Step 4: Register and initialize the real registry**

Register `ISessionFileRegistry` as `PostgresSessionFileRegistry` in `backend/Program.cs` and call `InitializeAsync()` in the existing startup schema block directly after `ISessionStore.InitializeAsync()`.

- [ ] **Step 5: Run registry and complete backend tests**

Run: `dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj`

Expected: registry tests pass with Postgres configured; all other backend tests remain green.

- [ ] **Step 6: Commit the registry slice**

```powershell
git add backend/Files/SessionFileRegistry.cs backend/Program.cs tests/AgentHub.Api.Tests/SessionFileRegistryPostgresTests.cs
git commit -m "feat(files): persist session file metadata and presentations"
```

---

### Task 3: S3 and pod storage orchestration

**Files:**
- Create: `backend/Files/SessionFileStorage.cs`
- Create: `backend/Files/SessionFileService.cs`
- Create: `tests/AgentHub.Api.Tests/SessionFileServiceTests.cs`
- Modify: `backend/Storage/S3ArtifactStore.cs`
- Modify: `backend/Services/ISessionService.cs`
- Modify: `backend/Program.cs`

**Interfaces:**
- Consumes: `ISessionFileRegistry`, validator, options, existing `IArtifactStore`, and live session lookup.
- Produces: `ISessionFileService`, `IAgentFileClient`, `ReserveFileResult`, `FileUploadDescriptor`, `FileContentResult`, and an expanded read/head contract on `IArtifactStore`.

```csharp
public sealed record SessionFileActor(
    string SessionId, string Owner, string Principal, bool CanWrite, bool CanManage);
public sealed record ReserveSessionFileCommand(
    string Name, string MimeType, long Size, string? BatchId, string Source);
public sealed record FileUploadDescriptor(
    string Kind, string Url, IReadOnlyDictionary<string, string> Headers);
public sealed record ReserveFileResult(
    SessionFileRecord File, FileUploadDescriptor Upload);
public sealed record FileContentResult(
    Stream? Content, string? RedirectUrl, string MimeType,
    string Name, long Size);

public interface ISessionFileService
{
    Task<ReserveFileResult> ReserveAsync(SessionFileActor actor,
        ReserveSessionFileCommand request, CancellationToken ct = default);
    Task PutPodContentAsync(SessionFileActor actor, string fileId,
        Stream content, CancellationToken ct = default);
    Task<SessionFileRecord> CompleteAsync(SessionFileActor actor,
        string fileId, CancellationToken ct = default);
    Task<IReadOnlyList<SessionFileRecord>> ListAsync(SessionFileActor actor,
        CancellationToken ct = default);
    Task<FileContentResult> OpenContentAsync(SessionFileActor actor,
        string fileId, CancellationToken ct = default);
    Task DeleteAsync(SessionFileActor actor, string fileId,
        CancellationToken ct = default);
    Task<SessionFilePresentation?> GetPresentationAsync(SessionFileActor actor,
        CancellationToken ct = default);
    Task<SessionFilePresentation> SetPresentationAsync(SessionFileActor actor,
        string? fileId, CancellationToken ct = default);
    Task DeleteSessionFilesAsync(string sessionId, CancellationToken ct = default);
}
```

- [ ] **Step 1: Write failing storage-selection and no-fallback tests**

```csharp
[Fact]
public async Task Reserve_uses_pod_only_when_S3_is_not_configured_and_pod_is_live()
{
    var service = Harness(s3Configured: false, podPhase: "Running");
    var actor = new SessionFileActor("s1", "alice", "alice", CanWrite: true, CanManage: true);
    var request = new ReserveSessionFileCommand("shot.png", "image/png", 10, "batch-1", "user");
    var result = await service.ReserveAsync(actor, request);
    Assert.Equal(SessionFileStorageKind.Pod, result.File.StorageKind);
    Assert.Equal("proxy", result.Upload.Kind);
}

[Fact]
public async Task S3_completion_failure_does_not_switch_the_record_to_pod()
{
    var service = Harness(s3Configured: true, s3Head: null, podPhase: "Running");
    var actor = new SessionFileActor("s1", "alice", "alice", CanWrite: true, CanManage: true);
    var error = await Assert.ThrowsAsync<SessionFileException>(
        () => service.CompleteAsync(actor, "f1"));
    Assert.Equal("storage_verification_failed", error.Code);
    Assert.Empty(service.Pod.Uploads);
}
```

- [ ] **Step 2: Run the service tests and verify RED**

Run: `dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --filter FullyQualifiedName~SessionFileServiceTests`

Expected: build fails because `ISessionFileService` and the storage result types do not exist.

- [ ] **Step 3: Extend the artifact-store boundary**

```csharp
public sealed record ArtifactObjectInfo(long Size, string? ContentType);

public interface IArtifactStore
{
    bool IsConfigured { get; }
    string PresignPut(string key, TimeSpan ttl);
    string PresignGet(string key, TimeSpan ttl);
    Task<ArtifactObjectInfo?> HeadAsync(string key, CancellationToken ct = default);
    Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
}
```

`NullArtifactStore.IsConfigured` is false and returns no object. `S3ArtifactStore` implements HEAD, bounded read streams, and exact-key delete without exposing its client or credentials.

- [ ] **Step 4: Implement the pod client and service state machine**

```csharp
public interface IAgentFileClient
{
    Task PutAsync(SessionInfo session, SessionFileRecord file, Stream content, CancellationToken ct);
    Task<Stream?> OpenReadAsync(SessionInfo session, SessionFileRecord file, CancellationToken ct);
    Task<bool> ExistsAsync(SessionInfo session, SessionFileRecord file, CancellationToken ct);
    Task DeleteAsync(SessionInfo session, SessionFileRecord file, CancellationToken ct);
}
```

Use `http://{PodIp}:{AgentPort}/agenthub/files/{fileId}` with `X-Agent-Token`, `ResponseHeadersRead`, a non-buffering stream, and the configured byte ceiling. `SessionFileService` owns reservation IDs, generated storage locators, quota checks, S3 descriptors, pod proxy operations, final MIME detection, ready-state transitions, authorized reads, deletion, and presentation updates.
When S3 is absent and the session has no live pod, throw `SessionFileException("temporary_storage_unavailable")`; reserve no row and return HTTP 409 through Task 4.

- [ ] **Step 5: Run service and artifact-store tests**

Run: `dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --filter "FullyQualifiedName~SessionFileServiceTests|FullyQualifiedName~ArtifactStoreKeyTests"`

Expected: both storage modes, missing pod, quota, mismatched content, failed completion, read, delete, and presentation tests pass.

- [ ] **Step 6: Commit the storage slice**

```powershell
git add backend/Files/SessionFileStorage.cs backend/Files/SessionFileService.cs backend/Storage/S3ArtifactStore.cs backend/Services/ISessionService.cs backend/Program.cs tests/AgentHub.Api.Tests/SessionFileServiceTests.cs
git commit -m "feat(files): store session files in S3 or live pods"
```

---

### Task 4: Authorized public and shared file APIs

**Files:**
- Create: `backend/Files/SessionFilesController.cs`
- Create: `ee/backend/Sharing/SharedSessionFilesController.cs`
- Create: `tests/AgentHub.Api.Tests/SessionFileAccessTests.cs`
- Create: `frontend/src/api.test.js`
- Modify: `ee/backend/Sharing/ShareModels.cs`
- Modify: `frontend/src/api.js`

**Interfaces:**
- Consumes: `ISessionAccessService`, `SessionAccessResult`, `ISessionFileService`.
- Produces: the public and share-link routes defined in the design, plus `SessionAccessRules.CanReadFiles`, `CanWriteFiles`, and `CanManageFiles`.

- [ ] **Step 1: Write failing role-matrix controller tests**

```csharp
[Theory]
[InlineData(SessionAccessLevel.Owner, true, true, true)]
[InlineData(SessionAccessLevel.Collaborator, true, true, false)]
[InlineData(SessionAccessLevel.Viewer, true, false, false)]
[InlineData(SessionAccessLevel.None, false, false, false)]
public void File_permissions_follow_the_session_role(
    SessionAccessLevel level, bool read, bool write, bool manage)
{
    Assert.Equal(read, SessionAccessRules.CanReadFiles(level));
    Assert.Equal(write, SessionAccessRules.CanWriteFiles(level));
    Assert.Equal(manage, SessionAccessRules.CanManageFiles(level));
}

[Fact]
public async Task Viewer_share_lists_ready_files_through_the_read_only_service_path()
{
    var api = SharedController(ShareRole.Viewer);
    var result = Assert.IsType<OkObjectResult>(await api.List("token", default));
    var files = Assert.IsAssignableFrom<IReadOnlyList<SessionFileResponse>>(result.Value);
    Assert.Single(files);
    Assert.False(SessionAccessRules.CanWriteFiles(SessionAccessLevel.Viewer));
}
```

- [ ] **Step 2: Run access tests and verify RED**

Run: `dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --filter FullyQualifiedName~SessionFileAccessTests`

Expected: build fails because the file permission rules and controllers are missing.

- [ ] **Step 3: Implement the authenticated API**

Implement exact routes under `/api/sessions/{id}/files`: capabilities, reserve, proxy content PUT, complete, list, authorized content GET, delete, presentation GET, and presentation PUT. Resolve the signed-in principal with `ISessionAccessService.ResolveUserAsync`; return 404 for absent access, 403 for insufficient role, 409 with stable codes for quota/storage conflicts, 410 for expired content, and 413 for byte limits.

```csharp
public sealed record ReserveSessionFileRequest(
    string Name, string MimeType, long Size, string? BatchId);
public sealed record SetFilePresentationRequest(string? FileId);
public sealed record FileApiError(string Code, string Message);
public sealed record SessionFileResponse(
    string Id, string Name, string MimeType, long Size,
    string State, string PreviewState, string Source,
    DateTime? ExpiresAt, string? PreviewFileId);
```

Stream content results; never read an entire document into a controller byte array.
Set `X-Content-Type-Options: nosniff` on every content response. Use `Content-Disposition: inline` only for allowed image/PDF/text previews and `attachment` for every other type. PDF responses also receive `Content-Security-Policy: sandbox; default-src 'none'`; the frontend frame carries a matching `sandbox` attribute.

- [ ] **Step 4: Implement token-share read routes**

Under `/api/shared/{token}/files`, implement capabilities, list, content GET, and presentation GET only. Resolve every request with `ResolveTokenReadOnlyAsync`. Do not register reserve, content PUT, complete, delete, or presentation PUT actions on the shared controller.

- [ ] **Step 5: Add frontend API calls and run both suites**

Add `sessionFileCapabilities`, `reserveSessionFile`, `uploadSessionFile`, `completeSessionFile`, `listSessionFiles`, `sessionFileContentUrl`, `deleteSessionFile`, `getFilePresentation`, and `setFilePresentation`; add shared read variants that use the token routes.

Run: `dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --filter FullyQualifiedName~SessionFileAccessTests`

Run: `npm test -- src/api.test.js` from `frontend/`.

Expected: role matrix, streaming responses, stable status codes, URL encoding, authorization headers, S3 PUT headers, and share-route tests pass.

- [ ] **Step 6: Commit the API slice**

```powershell
git add backend/Files/SessionFilesController.cs ee/backend/Sharing/SharedSessionFilesController.cs ee/backend/Sharing/ShareModels.cs tests/AgentHub.Api.Tests/SessionFileAccessTests.cs frontend/src/api.js frontend/src/api.test.js
git commit -m "feat(files): expose authorized session file APIs"
```

---

### Task 5: Agent-local managed file server

**Files:**
- Create: `agent-runtime/files/local-store.js`
- Create: `agent-runtime/session-agent/test/files-local-store.test.js`
- Modify: `agent-runtime/common/server.js`
- Modify: `agent-runtime/session-agent/test/common-server.test.js`

**Interfaces:**
- Consumes: `AGENTHUB_CALLBACK_TOKEN` and Task 1 limits injected as environment variables.
- Produces: `LocalFileStore` and authenticated `PUT`, `GET`, `HEAD`, `DELETE /agenthub/files/{fileId}` routes on the existing agent port.

- [ ] **Step 1: Write failing canonical-path and streaming tests**

```javascript
test('local store rejects traversal and escaping symlinks', async () => {
  const store = harness({ realpaths: { '/workspace/.agenthub/files/link': '/secrets/key' } })
  await assert.rejects(() => store.open('../secret'), /invalid_file_id/)
  await assert.rejects(() => store.open('link'), /managed_root_escape/)
})

test('PUT removes a partial file when the stream exceeds its declared ceiling', async () => {
  const store = harness({ maxBytes: 4 })
  await assert.rejects(() => store.put('abc123', streamOf('12345')), /file_too_large/)
  assert.equal(store.exists('abc123'), false)
})
```

- [ ] **Step 2: Run Node tests and verify RED**

Run: `node --test agent-runtime/session-agent/test/files-local-store.test.js`

Expected: module resolution fails for `../../files/local-store.js`.

- [ ] **Step 3: Implement `LocalFileStore`**

Expose `put(id, name, readable, maxBytes)`, `open(id)`, `head(id)`, and `remove(id)`. Accept file IDs matching `^[a-f0-9]{32}$`, normalize names to display-only basenames, create directories with mode `0700`, files with mode `0600`, write to a sibling `.partial`, fsync and rename only after success, reject symlink/reparse escapes using `realpath`, and remove partial content on every failure.

- [ ] **Step 4: Refactor the common server onto one HTTP server**

Create a Node `http.createServer` for file routes and attach `WebSocketServer({ server })`. Require an exact constant-time-safe comparison of `X-Agent-Token` for file routes. Preserve WebSocket paths `/`, `/shell`, and their existing tests. Return stable JSON errors and never include filesystem paths.

- [ ] **Step 5: Run complete session-agent tests**

Run: `npm test` from `agent-runtime/session-agent/`.

Expected: new route tests and all existing transport/provider tests pass.

- [ ] **Step 6: Commit the local-store slice**

```powershell
git add agent-runtime/files/local-store.js agent-runtime/common/server.js agent-runtime/session-agent/test/files-local-store.test.js agent-runtime/session-agent/test/common-server.test.js
git commit -m "feat(runtime): serve bounded session files inside agent pods"
```

---

### Task 6: Managed `agenthub_files` MCP

**Files:**
- Create: `agent-runtime/files/client.mjs`
- Create: `agent-runtime/files/paths.mjs`
- Create: `agent-runtime/files/server.mjs`
- Create: `agent-runtime/files/configure.mjs`
- Create: `agent-runtime/session-agent/test/files-client.test.js`
- Create: `agent-runtime/session-agent/test/files-paths.test.js`
- Create: `agent-runtime/session-agent/test/files-server.test.js`
- Create: `agent-runtime/session-agent/test/files-mcp-config.test.js`
- Modify: `agent-runtime/common/entrypoint-common.sh`
- Modify: `agent-runtime/codex/entrypoint.sh`
- Modify: `agent-runtime/cursor/entrypoint.sh`
- Modify: `agent-runtime/session-agent/test/codex-mcp-config.test.js`
- Modify: `agent-runtime/session-agent/test/cursor-mcp-config.test.js`

**Interfaces:**
- Consumes: internal callback URL/token and the managed local file root.
- Produces: `list_display_capabilities`, `list_files`, `read_file`, `upload_file`, `present_file`, `dismiss_presentation` and collision-safe registration for every provider.

- [ ] **Step 1: Write failing backend-client and root-boundary tests**

```javascript
test('client never accepts an oversized JSON response', async () => {
  const client = new FilesBackendClient({ fetch: async () => response('x'.repeat(1_000_001)) })
  await assert.rejects(() => client.list(), /files_backend_response_too_large/)
})

test('upload source must remain below workspace or managed output', async () => {
  assert.equal(await allowedSource('/workspace/out/report.pdf', deps), '/workspace/out/report.pdf')
  await assert.rejects(() => allowedSource('/home/agent/.codex/auth.json', deps), /file_source_not_allowed/)
})
```

- [ ] **Step 2: Run new MCP tests and verify RED**

Run: `node --test agent-runtime/session-agent/test/files-*.test.js`

Expected: imports from `../../files/` fail.

- [ ] **Step 3: Implement the client, path policy, and tool results**

Use `zod` input schemas with no passthrough fields. Return JSON text alongside `structuredContent` for metadata tools. `read_file` returns `{ type: 'image', data, mimeType }` for allowed images, bounded UTF-8 text for Markdown/text, and metadata for PDF/Office. `upload_file` canonicalizes then copies into the managed root before reservation. `present_file` accepts exactly one of `fileId` or `path` and uploads a path before updating presentation.

```javascript
const uploadSchema = z.object({
  path: z.string().min(1).max(4096),
  displayName: z.string().min(1).max(255).optional()
}).strict()
```

- [ ] **Step 4: Register the managed server for all runtimes**

`mergeFilesMcp` must overwrite a user-supplied `agenthub_files` definition. Update the common Claude merge chain, Codex TOML generation, Cursor JSON conversion/exclusion list, and OpenClaw's common configuration path. Always set `AGENTHUB_FILES_MCP_ENABLED=1`; this MCP does not depend on S3 or user MCP configuration.

- [ ] **Step 5: Run MCP and complete runtime tests**

Run: `npm test` from `agent-runtime/session-agent/`.

Expected: tool schemas, stable errors, image content, bounded text, source roots, upload/present/dismiss calls, spoofed-name replacement, and all existing MCP merge tests pass.

- [ ] **Step 6: Commit the MCP slice**

```powershell
git add agent-runtime/files agent-runtime/common/entrypoint-common.sh agent-runtime/codex/entrypoint.sh agent-runtime/cursor/entrypoint.sh agent-runtime/session-agent/test/files-*.test.js agent-runtime/session-agent/test/codex-mcp-config.test.js agent-runtime/session-agent/test/cursor-mcp-config.test.js
git commit -m "feat(mcp): add managed session file tools"
```

---

### Task 7: Provider-neutral chat attachment delivery

**Files:**
- Create: `agent-runtime/files/materialize.js`
- Create: `agent-runtime/session-agent/test/files-materialize.test.js`
- Modify: `agent-runtime/common/driver-contract.js`
- Modify: `agent-runtime/common/server.js`
- Modify: `agent-runtime/claude/driver.js`
- Modify: `agent-runtime/codex/driver.js`
- Modify: `agent-runtime/cursor/driver.js`
- Modify: `agent-runtime/openclaw/driver.js`
- Modify: `agent-runtime/session-agent/test/common-server.test.js`
- Modify: `agent-runtime/session-agent/test/claude-driver.test.js`
- Modify: `agent-runtime/session-agent/test/codex-driver.test.js`
- Modify: `agent-runtime/session-agent/test/cursor-driver.test.js`
- Modify: `agent-runtime/session-agent/test/openclaw-driver.test.js`

**Interfaces:**
- Consumes: ready file IDs and internal materialization API.
- Produces: required `attachmentCapabilities` on every driver and WebSocket `{type:"chat", text, attachments:[id]}` handling.

- [ ] **Step 1: Write failing contract and chat-echo tests**

```javascript
test('driver contract requires three explicit attachment capabilities', () => {
  assert.throws(() => validateDriver(baseDriver), /attachmentCapabilities/)
  assert.doesNotThrow(() => validateDriver({
    ...baseDriver,
    attachmentCapabilities: { nativeImages: false, localImagePaths: true, mcpImages: true }
  }))
})

test('chat materializes ready IDs and echoes metadata without local paths', async () => {
  const harness = createChatHarness({}, attachmentDriver)
  harness.files.materializeResult = [{ id: 'a'.repeat(32), name: 'shot.png', mimeType: 'image/png', size: 12, localPath: '/workspace/.agenthub/files/a/shot.png' }]
  harness.socket.emit('message', Buffer.from(JSON.stringify({ type: 'chat', text: 'inspect', attachments: ['a'.repeat(32)] })))
  await tick()
  assert.doesNotMatch(harness.socket.sent.join(''), /workspace/)
  assert.match(harness.children[0].stdinWrites[0], /inspect each attached image/i)
})
```

- [ ] **Step 2: Run transport tests and verify RED**

Run: `node --test agent-runtime/session-agent/test/common-server.test.js agent-runtime/session-agent/test/files-materialize.test.js`

Expected: the driver contract does not yet require capabilities and attachment messages are ignored.

- [ ] **Step 3: Implement materialization and authoritative batch checks**

Fetch metadata for all IDs in one internal request, verify ready state/session ownership/count/aggregate bytes, download S3 content or reuse the pod path, and return immutable attachment records. Reject the entire turn on one invalid attachment. Do not clear the client draft; send an `agenthub` error event with `attachment_delivery_failed`.

- [ ] **Step 4: Implement driver declarations and Claude fallback prompt**

Every driver exports exactly:

```javascript
attachmentCapabilities: Object.freeze({
  nativeImages: false,
  localImagePaths: true,
  mcpImages: true
})
```

Codex may declare `nativeImages: true`, but native image arguments are used only by a protocol that can attach them to the current turn. Claude structured chat prepends a compact attachment manifest and the instruction to inspect every image with its visual Read/MCP path before answering. If all three capabilities are false, reject before stdin write.

- [ ] **Step 5: Store safe echo metadata and extend replay tests**

Echo `attachments: [{id,name,mimeType,size,visualDelivery}]` beside the text content. Never echo `localPath`. Preserve old text-only events byte-for-byte.

Run: `npm test` from `agent-runtime/session-agent/`.

Expected: all provider declarations, fallback selection, rejection, safe echo, resume, and text-only regressions pass.

- [ ] **Step 6: Commit the transport slice**

```powershell
git add agent-runtime/files/materialize.js agent-runtime/common/driver-contract.js agent-runtime/common/server.js agent-runtime/claude/driver.js agent-runtime/codex/driver.js agent-runtime/cursor/driver.js agent-runtime/openclaw/driver.js agent-runtime/session-agent/test
git commit -m "feat(chat): deliver provider-neutral image attachments"
```

---

### Task 8: Chat composer uploads and attachment transcript cards

**Files:**
- Create: `frontend/src/lib/attachments.js`
- Create: `frontend/src/lib/attachments.test.js`
- Create: `frontend/src/components/ChatAttachments.vue`
- Create: `frontend/src/components/chat-attachments.test.js`
- Modify: `frontend/src/components/ChatPane.vue`
- Modify: `frontend/src/components/chat-pane.test.js`
- Modify: `frontend/src/lib/chat.js`
- Modify: `frontend/src/lib/chat.test.js`

**Interfaces:**
- Consumes: Task 4 frontend API calls and WebSocket attachment IDs.
- Produces: browser validation/deduplication, upload queue, progress cards, and replayable attachment items.

- [ ] **Step 1: Write failing paste/drop and send-gating tests**

```javascript
it('pastes one image, uploads it, and sends its ready file id', async () => {
  const wrapper = mountChat()
  await wrapper.get('[data-chat-input]').trigger('paste', {
    clipboardData: { files: [pngFile('shot.png', 12)] }
  })
  await flushPromises()
  expect(wrapper.get('[data-attachment="file-1"]').text()).toContain('shot.png')
  await wrapper.get('[data-chat-send]').trigger('click')
  expect(socket.sent.at(-1)).toEqual({ type: 'chat', text: '', attachments: ['file-1'] })
})

it('keeps the draft when one upload fails', async () => {
  api.completeSessionFile.mockRejectedValueOnce(Object.assign(new Error('failed'), { code: 'content_type_mismatch' }))
  const wrapper = mountChat()
  await addFile(wrapper, pngFile('bad.png', 12))
  expect(wrapper.get('[data-chat-send]').attributes('disabled')).toBeDefined()
  expect(wrapper.get('[data-attachment-error]').text()).toContain('File content does not match its type')
})
```

- [ ] **Step 2: Run frontend tests and verify RED**

Run: `npm test -- src/lib/attachments.test.js src/components/chat-attachments.test.js src/components/chat-pane.test.js` from `frontend/`.

Expected: attachment modules/selectors do not exist.

- [ ] **Step 3: Implement browser validation and upload queue**

Use one literal type/extension map matching Task 1, enforce 5/20 MiB/50 MiB/50 MiB before reserving, fingerprint clipboard/drop duplicates as `name:size:lastModified:type`, and maintain states `queued`, `uploading`, `ready`, `failed`, `cancelled`. Use `AbortController`; cancellation calls DELETE for a reserved file. Map stable backend codes to concise user messages.

- [ ] **Step 4: Implement composer and transcript cards**

Add paperclip button, hidden file input, drop target, paste handler, progress, retry, and remove. Allow attachment-only messages. Send only ready IDs and clear text/cards only after `WebSocket.send` succeeds. Extend `createChatLog.handleUser` to retain safe attachment metadata on user items.

- [ ] **Step 5: Run focused and full frontend tests**

Run: `npm test` from `frontend/`.

Expected: upload, retry, cancellation, duplicate paste, attachment-only, text-plus-attachment, replay, expired-card, read-only, and existing chat tests pass.

- [ ] **Step 6: Commit the composer slice**

```powershell
git add frontend/src/lib/attachments.js frontend/src/lib/attachments.test.js frontend/src/components/ChatAttachments.vue frontend/src/components/chat-attachments.test.js frontend/src/components/ChatPane.vue frontend/src/components/chat-pane.test.js frontend/src/lib/chat.js frontend/src/lib/chat.test.js
git commit -m "feat(ui): attach images and documents in chat"
```

---

### Task 9: Files/Browser companion workspace and direct previews

**Files:**
- Create: `frontend/src/lib/files.js`
- Create: `frontend/src/lib/files.test.js`
- Create: `frontend/src/components/FilesPane.vue`
- Create: `frontend/src/components/FilePreview.vue`
- Create: `frontend/src/components/files-pane.test.js`
- Create: `frontend/src/components/file-preview.test.js`
- Modify: `frontend/src/components/SessionWorkspace.vue`
- Modify: `frontend/src/components/browser-workspace.test.js`
- Modify: `frontend/src/components/TerminalView.vue`

**Interfaces:**
- Consumes: list/content/capabilities/presentation API and `sessionCapabilities`.
- Produces: companion tabs `Files | Browser`, presentation revision polling, and safe image/PDF/Markdown/text/Office-state previews.

- [ ] **Step 1: Write failing workspace and preview tests**

```javascript
it('opens Files when a newer presentation revision selects a file', async () => {
  api.getFilePresentation.mockResolvedValueOnce({ revision: 7, fileId: 'f1' })
  api.listSessionFiles.mockResolvedValueOnce([{ id: 'f1', name: 'shot.png', mimeType: 'image/png', state: 'Ready' }])
  const wrapper = mountWorkspace()
  await flushPromises()
  expect(wrapper.get('[data-workspace-tab="files"]').classes()).toContain('active')
  expect(wrapper.get('[data-file-preview="image"]').attributes('src')).toContain('/files/f1/content')
})

it('renders HTML and SVG as download-only', () => {
  const wrapper = mount(FilePreview, { props: { file: { id: 'f1', name: 'x.svg', mimeType: 'image/svg+xml', state: 'Ready' } } })
  expect(wrapper.find('iframe').exists()).toBe(false)
  expect(wrapper.get('[data-preview-unsupported]').text()).toContain('Download')
})
```

- [ ] **Step 2: Run component tests and verify RED**

Run: `npm test -- src/components/files-pane.test.js src/components/file-preview.test.js src/components/browser-workspace.test.js` from `frontend/`.

Expected: Files components and tabs are missing.

- [ ] **Step 3: Implement file state and preview routing**

`previewKind(file, capabilities)` returns exactly `image`, `pdf`, `markdown`, `text`, `office-pdf`, `pending`, `expired`, or `download`. Fetch Markdown/text with bearer authorization and cap display at 1 MiB while reporting truncation. Render Markdown with `renderMarkdown`; use `<img>` for images and a sandboxed frame for PDF. Never place uploaded HTML into `v-html`.

- [ ] **Step 4: Generalize `SessionWorkspace`**

Show the companion when browser phase is not Stopped or a file is selected/presented. Preserve one 25–75 percent separator, browser component instance/state, and active desktop tab. On mobile expose `Files`, `Browser`, `Agent`; hide unavailable tabs. Local close hides Files without changing shared presentation; an explicit dismiss button calls the writable API.

- [ ] **Step 5: Run accessibility and full frontend tests**

Run: `npm test` from `frontend/`.

Expected: presentation polling, stale revision suppression, Files auto-open, Browser preservation, direct preview kinds, expired states, read-only downloads, keyboard resizing, and mobile tabs pass.

- [ ] **Step 6: Commit the workspace slice**

```powershell
git add frontend/src/lib/files.js frontend/src/lib/files.test.js frontend/src/components/FilesPane.vue frontend/src/components/FilePreview.vue frontend/src/components/files-pane.test.js frontend/src/components/file-preview.test.js frontend/src/components/SessionWorkspace.vue frontend/src/components/browser-workspace.test.js frontend/src/components/TerminalView.vue
git commit -m "feat(ui): add files companion workspace and previews"
```

---

### Task 10: Optional LibreOffice renderer and asynchronous preview worker

**Files:**
- Create: `artifact-renderer/ArtifactRenderer.csproj`
- Create: `artifact-renderer/Program.cs`
- Create: `artifact-renderer/LibreOfficeConverter.cs`
- Create: `artifact-renderer/Dockerfile`
- Create: `tests/ArtifactRenderer.Tests/ArtifactRenderer.Tests.csproj`
- Create: `tests/ArtifactRenderer.Tests/LibreOfficeConverterTests.cs`
- Create: `backend/Files/OfficePreviewClient.cs`
- Create: `backend/Files/SessionFilePreviewWorker.cs`
- Create: `tests/AgentHub.Api.Tests/SessionFilePreviewWorkerTests.cs`
- Modify: `backend/Program.cs`
- Modify: `.github/workflows/build-images.yml`
- Modify: `.github/workflows/test.yml`

**Interfaces:**
- Consumes: queued Office source records and authorized content streams.
- Produces: `POST /render` returning PDF, `IOfficePreviewClient`, and a multi-replica-safe background worker.

- [ ] **Step 1: Write failing converter boundary tests**

```csharp
[Fact]
public void Command_uses_an_isolated_profile_and_pdf_output_directory()
{
    var command = LibreOfficeConverter.BuildCommand("/work/in/report.docx", "/work/out", "/work/profile");
    Assert.Equal("soffice", command.FileName);
    Assert.Equal(["--headless", "-env:UserInstallation=file:///work/profile", "--convert-to", "pdf", "--outdir", "/work/out", "/work/in/report.docx"], command.Arguments);
}

[Fact]
public async Task Conversion_timeout_kills_the_process_and_removes_work_files()
{
    var harness = ConverterHarness.NeverExits();
    await Assert.ThrowsAsync<ConversionException>(() => harness.ConvertAsync(DocxBytes(), TimeSpan.FromMilliseconds(10)));
    Assert.True(harness.ProcessKilled);
    Assert.Empty(harness.RemainingWorkFiles);
}
```

- [ ] **Step 2: Run renderer tests and verify RED**

Run: `dotnet test tests/ArtifactRenderer.Tests/ArtifactRenderer.Tests.csproj`

Expected: referenced renderer project and converter do not exist.

- [ ] **Step 3: Implement the isolated renderer service**

Accept one multipart file up to 50 MiB and only DOCX/PPTX/XLSX. Write into a per-request random directory, invoke `soffice --headless` with an isolated user profile, require exit code 0 and one PDF no larger than the configured output ceiling, stream `application/pdf`, and recursively remove only that verified request directory in `finally`. The Dockerfile installs pinned distribution LibreOffice packages, uses a non-root UID, declares `/tmp/render` as its only writable path, and has no shell-facing public route other than `/render` and `/healthz`.

- [ ] **Step 4: Write and run failing backend worker tests**

Test that two workers cannot claim the same source, success creates a linked PDF record with inherited storage lifetime, failure marks only preview state failed, disabled renderer leaves the source download-ready, and retry requeues a failed preview.

Run: `dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --filter FullyQualifiedName~SessionFilePreviewWorkerTests`

Expected: build fails because the client and worker are missing.

- [ ] **Step 5: Implement renderer client and worker**

Use `IHttpClientFactory`, `ResponseHeadersRead`, input/output limits, and configured timeout. `SessionFilePreviewWorker` claims with `SKIP LOCKED`, opens the source through `ISessionFileService`, renders, stores a generated `application/pdf` file through the same active storage mode, links the derivative, and never changes the source from Ready on failure.

- [ ] **Step 6: Run renderer, backend, and container smoke tests**

Run: `dotnet test tests/ArtifactRenderer.Tests/ArtifactRenderer.Tests.csproj`

Run: `dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --filter FullyQualifiedName~SessionFilePreviewWorkerTests`

Run: `docker build -f artifact-renderer/Dockerfile -t agenthub-artifact-renderer:test .`

Expected: unit tests pass and the image converts one fixture each for DOCX, PPTX, and XLSX to a non-empty PDF within the configured timeout.

- [ ] **Step 7: Commit the renderer slice**

```powershell
git add artifact-renderer tests/ArtifactRenderer.Tests backend/Files/OfficePreviewClient.cs backend/Files/SessionFilePreviewWorker.cs tests/AgentHub.Api.Tests/SessionFilePreviewWorkerTests.cs backend/Program.cs .github/workflows/build-images.yml .github/workflows/test.yml
git commit -m "feat(files): render Office previews as isolated PDFs"
```

---

### Task 11: Internal file API, lifecycle sweeps, and session deletion

**Files:**
- Create: `backend/Files/InternalSessionFilesController.cs`
- Create: `backend/Files/SessionFileSweepService.cs`
- Create: `tests/AgentHub.Api.Tests/InternalSessionFilesTests.cs`
- Create: `tests/AgentHub.Api.Tests/SessionFileSweepServiceTests.cs`
- Modify: `backend/Controllers/InternalController.cs`
- Modify: `backend/Services/KubernetesSessionService.cs`
- Modify: `backend/Program.cs`

**Interfaces:**
- Consumes: callback-token lookup, registry, storage service, and session lifecycle.
- Produces: MCP-facing capabilities/list/materialize/reserve/complete/present/dismiss routes and deterministic cleanup.

- [ ] **Step 1: Write failing callback-token isolation tests**

```csharp
[Fact]
public async Task Callback_token_cannot_materialize_a_file_from_another_session()
{
    var controller = Harness(tokenSession: "s1", requestedSession: "s1", fileSession: "s2");
    Assert.IsType<NotFoundResult>(await controller.Materialize("s1", "f2", default));
}

[Fact]
public async Task Agent_registration_rejects_a_locator_outside_the_managed_root()
{
    var controller = Harness();
    var result = await controller.RegisterPodFile("s1",
        new RegisterPodFileRequest("/home/agent/.codex/auth.json", "auth.json", "application/json", 10), default);
    Assert.Equal("file_source_not_allowed", ErrorCode(result));
}
```

```csharp
[Fact]
public async Task Internal_errors_and_logs_do_not_expose_paths_or_presigned_urls()
{
    var log = await Harness().CauseMaterializationFailure("/workspace/.agenthub/files/f1/a.png", "https://s3.invalid/signed?secret=x");
    Assert.DoesNotContain("/workspace", log);
    Assert.DoesNotContain("secret=x", log);
}
```

- [ ] **Step 2: Run internal API tests and verify RED**

Run: `dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --filter FullyQualifiedName~InternalSessionFilesTests`

Expected: controller and request records are missing.

- [ ] **Step 3: Implement callback-token routes**

Move reusable token/session resolution from `InternalController` into an internal `IAgentCallbackAuthorizer`. Implement capabilities, list, batch materialize, agent reservation/completion, presentation, and dismissal. Accept pod locators only as `{fileId}/{safeName}` relative to the managed root; never accept an absolute path from HTTP.

- [ ] **Step 4: Write failing sweep and deletion tests**

Test that reservations older than 15 minutes become failed and are removed, missing pod content becomes expired, S3 content survives pod completion, session deletion clears presentation and marks/removes every binary, and one failed object delete does not prevent remaining cleanup.

- [ ] **Step 5: Implement the hosted sweep and deletion hook**

Run the sweep at a bounded configurable interval with cancellation. In `KubernetesSessionService.DeleteSessionAsync`, call `ISessionFileService.DeleteSessionFilesAsync` before deleting the session record and after the pod stop attempt; cleanup is idempotent. On terminal status `Succeeded` or `Failed`, reconcile pod-backed files to expired only after the pod is actually unavailable.

- [ ] **Step 6: Run internal, lifecycle, and full backend tests**

Run: `dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj`

Expected: all internal auth, sweep, deletion, status, sharing, browser, and existing session tests pass.

- [ ] **Step 7: Commit the lifecycle slice**

```powershell
git add backend/Files/InternalSessionFilesController.cs backend/Files/SessionFileSweepService.cs backend/Controllers/InternalController.cs backend/Services/KubernetesSessionService.cs backend/Program.cs tests/AgentHub.Api.Tests/InternalSessionFilesTests.cs tests/AgentHub.Api.Tests/SessionFileSweepServiceTests.cs
git commit -m "feat(files): secure agent callbacks and clean file lifecycles"
```

---

### Task 12: Helm, static manifests, ingress size, and renderer isolation

**Files:**
- Create: `helm/open-agenthub/templates/artifact-renderer.yaml`
- Create: `tests/helm/files-values.ps1`
- Create: `artifact-renderer/smoke.ps1`
- Modify: `helm/open-agenthub/values.yaml`
- Modify: `helm/open-agenthub/values-dev.yaml`
- Modify: `helm/open-agenthub/templates/configmap.yaml`
- Modify: `helm/open-agenthub/templates/ingress.yaml`
- Modify: `helm/open-agenthub/templates/networkpolicy.yaml`
- Modify: `helm/open-agenthub/templates/backend.yaml`
- Modify: `k8s/20-backend.yaml`
- Modify: `k8s/30-networkpolicy.yaml`
- Modify: `tests/helm/deployment-parity.ps1`
- Modify: `README.md`

**Interfaces:**
- Consumes: Task 1 configuration names and Task 10 renderer `/render`/`healthz` routes.
- Produces: deployable configuration with a 55m NGINX body ceiling and an optional no-egress renderer.

- [ ] **Step 1: Write failing Helm behavior tests**

```powershell
$default = helm template test helm/open-agenthub --set ingress.enabled=true --set ingress.host=example.test
if ($default -notmatch 'nginx.ingress.kubernetes.io/proxy-body-size: "55m"') { throw 'missing 55m ingress limit' }
if ($default -match 'name: agenthub-artifact-renderer') { throw 'renderer must be disabled by default' }

$enabled = helm template test helm/open-agenthub --set files.officePreview.enabled=true --set ingress.host=example.test
if ($enabled -notmatch 'name: agenthub-artifact-renderer') { throw 'enabled renderer missing' }
if ($enabled -notmatch 'runAsNonRoot: true') { throw 'renderer must run non-root' }
if ($enabled -notmatch 'Files__OfficePreview__Enabled: "true"') { throw 'renderer config missing' }
```

- [ ] **Step 2: Run Helm tests and verify RED**

Run: `pwsh -File tests/helm/files-values.ps1`

Expected: missing body-size annotation, values, and renderer resources cause assertions to fail.

- [ ] **Step 3: Add exact values and backend configuration**

Add `files` values for all approved limits, TTLs, polling, and `officePreview.enabled: false`, image, pull policy, CPU/memory, timeout, and output bytes. Emit `Files__*` ConfigMap entries. Configure Kestrel multipart/body limit to 55 MiB. Add `ingress.annotations` merge support and default the NGINX body annotation to `55m` without removing the existing WebSocket timeouts.

- [ ] **Step 4: Add renderer workload and network policy**

Create one renderer Deployment/Service only when enabled. Enforce non-root, RuntimeDefault seccomp, read-only root, dropped capabilities, bounded `emptyDir`, readiness/liveness, and resource limits. Add ingress only from backend pods to renderer port and no egress rule. Existing backend-to-agent port 7681 already carries the new file HTTP routes; do not widen it.

- [ ] **Step 5: Maintain static-manifest parity and docs**

Add equivalent environment keys, NGINX guidance, optional renderer manifest section, and network rules to `k8s/`. Document S3-persistent versus pod-temporary behavior, upload limits, Office opt-in, and MCP tool names in `README.md`. Do not include any internal or unrelated organization identifiers.

- [ ] **Step 6: Run Helm, parity, and renderer smoke tests**

Run: `pwsh -File tests/helm/files-values.ps1`

Run: `pwsh -File tests/helm/deployment-parity.ps1`

Run: `pwsh -File artifact-renderer/smoke.ps1`

Expected: default/override renderer modes, 55m annotation, ConfigMap values, security context, no-egress policy, static parity, and three Office conversions pass.

- [ ] **Step 7: Commit the deployment slice**

```powershell
git add helm/open-agenthub k8s/20-backend.yaml k8s/30-networkpolicy.yaml tests/helm/files-values.ps1 tests/helm/deployment-parity.ps1 artifact-renderer/smoke.ps1 README.md
git commit -m "feat(deploy): configure session files and Office previews"
```

---

### Task 13: End-to-end acceptance and full verification

**Files:**
- Create: `tests/files-browser-smoke.ps1`
- Create: `docs/testing/session-files-acceptance.md`
- Modify: `tests/browser-smoke.ps1`
- Modify: `README.md`

**Interfaces:**
- Consumes: complete backend, runtime, UI, MCP, renderer, MinIO, and Kubernetes behavior.
- Produces: repeatable acceptance evidence for persistent and temporary modes.

- [ ] **Step 1: Write the failing acceptance script around observable behavior**

The script must perform API/UI/MCP operations rather than grep source. It creates a session, uploads a fixture image, sends a prompt that requires reading a literal pixel/label fact, waits for the answer, asks `agenthub_files.present_file` to present a generated image, verifies presentation revision and authorized content bytes, stops the pod in temporary mode and expects `410 Gone`, then repeats with MinIO and expects content after resume.

```powershell
$answer = Invoke-ChatTurn -SessionId $session.id -Text 'Read the attached image and return the exact label.' -FileId $image.id
if ($answer -notmatch 'BLUE-47') { throw 'Agent did not analyze the attached image.' }
$presentation = Wait-FilePresentation -SessionId $session.id -AfterRevision 0
if ($presentation.fileId -ne $generated.id) { throw 'MCP presentation did not reach the UI state.' }
```

- [ ] **Step 2: Run the new smoke before its fixtures/helpers exist and verify RED**

Run: `pwsh -File tests/files-browser-smoke.ps1`

Expected: it fails at the first missing session-file API or fixture helper, not because of a PowerShell syntax error.

- [ ] **Step 3: Complete acceptance coverage**

Add deterministic PNG, PDF, Markdown, DOCX, PPTX, and XLSX fixtures generated inside the test's temporary directory. Cover owner, collaborator, viewer, and share-link roles; oversize and mismatched types; traversal attempt; renderer enabled/disabled; Browser-to-Files switching; and HTML/SVG download-only behavior. Record exact commands, environment, pass counts, and any intentionally unavailable live-provider matrix entries in `docs/testing/session-files-acceptance.md`.

- [ ] **Step 4: Run all automated verification**

Run: `dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj`

Run: `dotnet test tests/ArtifactRenderer.Tests/ArtifactRenderer.Tests.csproj`

Run: `npm test` from `agent-runtime/session-agent/`.

Run: `npm test` from `frontend/`.

Run: `npm run build` from `frontend/`.

Run: `pwsh -File tests/helm/files-values.ps1`.

Run: `pwsh -File tests/helm/deployment-parity.ps1`.

Run: `pwsh -File artifact-renderer/smoke.ps1`.

Run: `pwsh -File tests/files-browser-smoke.ps1` against the documented local Kubernetes/MinIO environment.

Expected: every command exits 0 with no new warning attributable to this feature.

- [ ] **Step 5: Run the mutation-oriented review**

Confirm a test fails for each deliberate local mutation: allow six files, remove MIME mismatch detection, return a cross-session file, let viewer upload, expose `localPath` in echo, skip visual read instruction, accept `/home/agent`, reuse a stale presentation revision, render SVG inline, enable renderer egress, remove the 55m ingress annotation, and fall back from failed S3 to pod storage. Revert each mutation immediately after observing the expected failure.

- [ ] **Step 6: Update user documentation and commit acceptance evidence**

```powershell
git add tests/files-browser-smoke.ps1 tests/browser-smoke.ps1 docs/testing/session-files-acceptance.md README.md
git commit -m "test(files): verify visual attachments and file presentations"
```

- [ ] **Step 7: Request final code review before branch integration**

Use `superpowers:requesting-code-review` against the approved design and this plan. Resolve every correctness or security finding with a new failing test before changing production code, rerun Step 4, then use `superpowers:verification-before-completion` and `superpowers:finishing-a-development-branch`.
