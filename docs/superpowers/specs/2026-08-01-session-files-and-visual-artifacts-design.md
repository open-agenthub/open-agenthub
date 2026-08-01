# Session Files and Visual Artifacts Design

**Date:** 2026-08-01  
**Status:** Approved design  
**Scope:** Open AgentHub core, agent runtimes, frontend, Helm/Kubernetes deployment, and an optional document-renderer image

## Summary

Open AgentHub will support session-scoped files as a provider-neutral capability. A user can paste, drop, or select images and documents in the structured web chat. Images become genuine visual context for the agent rather than decorative chat thumbnails. Agents can also upload and present files through a built-in `agenthub_files` MCP server. The web UI displays supported files in a resizable Files workspace beside the chat or terminal.

The feature works with two storage modes:

- When S3-compatible storage is configured, files persist with the session.
- Without S3, files are stored in the running agent pod and expire with that pod.

The file protocol, metadata, authorization, presentation state, and MCP surface are provider-neutral. Individual agent drivers may use a native image-input path when available and otherwise use a managed local-file/MCP path. The terminal itself does not need to render inline media.

## Goals

1. Let users attach images to structured web-chat messages by paste, drag-and-drop, or file selection.
2. Ensure attached images are actually inspected as visual input by the selected agent.
3. Give every supported agent runtime the same session-file and MCP contract.
4. Let an agent upload and present generated files in a Files workspace beside the chat or terminal.
5. Preview images, PDF, Markdown, text, and optionally DOCX, PPTX, and XLSX.
6. Work without S3 by using explicitly temporary pod storage.
7. Preserve existing session sharing and role semantics for file access.
8. Clearly report which file types are graphically displayable in the current deployment.

## Non-goals

- Inline media rendering inside the terminal emulator.
- General-purpose user file storage outside a session.
- Editing Office documents in the browser.
- Rendering arbitrary HTML or SVG as active content.
- Audio or video understanding in the first release.
- Permanent storage without an S3-compatible store.
- Replacing provider-specific image features; native provider support remains an optimization behind the common contract.

## Design Principles

- File IDs, not URLs or paths, cross user-facing trust boundaries.
- Authorization is evaluated on every metadata or content request.
- Storage behavior is explicit; S3 failure never silently changes a persistent upload into a temporary one.
- The neutral fallback must work even when an agent CLI has no native multimodal chat-message format.
- Binary content is loaded only when needed. Lists and presentation polling exchange metadata only.
- Unsupported or expired files remain understandable in the transcript instead of becoming broken links.

## Architecture

### Backend metadata and presentation state

The backend adds a `SessionFileRegistry` responsible only for metadata, lifecycle, authorization lookups, quotas, and presentation state. It stores records in two Postgres tables.

`session_files` contains:

- opaque file ID;
- session ID and owner;
- original display name and normalized extension;
- declared and detected MIME type;
- byte size;
- storage kind: `s3` or `pod`;
- storage locator controlled by the backend;
- state: `reserved`, `uploading`, `ready`, `failed`, `expired`, or `deleted`;
- preview state and optional rendered-derivative file ID;
- creator identity and source: `user` or `agent`;
- creation, completion, expiry, and deletion timestamps.

`session_file_presentations` contains one row per session:

- session ID;
- selected file ID or null;
- monotonically increasing revision;
- presenter identity;
- update timestamp.

The presentation revision lets clients ignore stale polling responses and react only to changes.

### Storage abstraction

`ISessionFileStore` has persistent S3 and temporary pod implementations.

The S3 implementation stores files below a key controlled by the backend, conceptually:

```text
sessions/{sanitized-owner}/{session-id}/files/{file-id}/{safe-display-name}
```

It issues short-lived PUT and GET URLs and verifies a completed upload with object metadata before marking the record ready. The agent pod never receives S3 credentials.

The pod implementation stores content below:

```text
/workspace/.agenthub/files/{file-id}/{safe-display-name}
```

The authenticated public upload endpoint streams bytes through the backend to an internal HTTP endpoint on the live session agent. Content reads are similarly authorized by the backend and proxied from the pod. If the pod is unavailable, creation or content access returns an explicit temporary-storage error. Pod-backed records are marked expired when the pod ends or a content check reports the file missing.

### Agent runtime

The common session runtime gains a small authenticated HTTP file surface alongside its existing WebSocket service. Only the backend and same-pod processes may use it. It supports bounded upload, bounded download, existence checks, and deletion below the managed file root. It rejects traversal, symlinks that escape the root, unexpected file IDs, and oversized streams.

All agent runtime images include the managed `agenthub_files` stdio MCP server. Runtime-specific MCP configuration merges it with user configuration in the same way as the managed browser MCP. User configuration cannot replace the managed server definition.

### Frontend workspace

The existing `SessionWorkspace` becomes a general companion workspace. Its left side has `Files` and `Browser` tabs; the right side continues to host the chat or terminal. The companion side appears when either a browser is active or a file is presented. The resizable desktop split and accessible separator remain. Mobile uses `Files`, `Browser`, and `Agent` tabs.

## Upload and Chat Flow

### User upload

The structured chat composer accepts:

- clipboard paste;
- drag-and-drop;
- a file-picker button.

Pending attachments appear as removable cards with thumbnail or type icon, name, size, upload progress, and error state. Sending is disabled while an attachment is incomplete. A text-only or attachment-only message is valid.

The upload sequence is:

1. The frontend requests a file reservation with name, size, and browser-reported MIME type.
2. The backend checks session write access, extension, reported MIME type, per-file limits, per-message limits, and session quota.
3. The backend returns an upload descriptor:
   - S3 mode: a short-lived presigned PUT URL and required headers;
   - pod mode: an authenticated same-origin backend upload URL.
4. The frontend uploads the bytes.
5. The frontend calls the completion endpoint.
6. The backend validates final size, detected type, and storage existence, then changes the record to `ready`.
7. The chat WebSocket message sends text plus ready file IDs.

An interrupted or abandoned reservation is reaped after a short expiry and never appears as a usable file.

### Provider-neutral prompt delivery

The common chat protocol accepts:

```text
sendUser({
  text,
  attachments: [{ id, name, mimeType, localPath }]
})
```

Before invoking the driver, the runtime materializes every ready attachment in the managed local directory. Each driver declares these capabilities:

- `nativeImages`: can attach local image files directly to a turn;
- `localImagePaths`: can reliably inspect a named local image through a built-in tool;
- `mcpImages`: can consume image content returned by the managed MCP.

Selection order is native image input, local visual-read path, then managed MCP. The fallback prompt identifies attachments by display name and managed local path and requires the agent to inspect each image before answering. The driver records the selected delivery mechanism in the safe AgentHub chat event for diagnostics.

If no supported visual path exists, the runtime rejects the message before sending it and the UI retains the draft with a clear error. It must not pretend that an image was analyzed.

Claude's current streaming JSON input is text-only, so its neutral path uses the managed local file and visual-read/MCP mechanism. Codex may use native local-image input where its active protocol supports it. Cursor and OpenClaw use their declared runtime capabilities and otherwise fall back to MCP. The design does not require every provider to expose the structured chat UI immediately; it guarantees that the file contract does not need to change when another provider gains that UI mode.

### Transcript representation

The user echo event stores only safe attachment metadata:

```json
{
  "id": "opaque-id",
  "name": "chart.png",
  "mimeType": "image/png",
  "size": 183420,
  "visualDelivery": "localImagePaths"
}
```

No presigned URL, object key, pod IP, or uncontrolled local path is stored in the transcript. On replay, the frontend resolves metadata and content through authorized APIs. An expired pod file renders as an explicit unavailable attachment card.

## File Limits and Types

Defaults are configurable but have these initial values:

- at most 5 attachments per chat message;
- at most 20 MiB per image;
- at most 50 MiB per document;
- at most 50 MiB total per chat message;
- at most 200 non-deleted files per session;
- at most 1 GiB of ready, non-deleted file content per session.

Accepted image types are PNG, JPEG, WebP, and GIF. Accepted preview/document types are PDF, Markdown, plain text, safe structured text, DOCX, PPTX, and XLSX. Extension, declared MIME type, detected MIME type, and magic bytes must agree with the allowlist. SVG and HTML are not actively rendered.

The supplied NGINX Ingress configuration sets:

```yaml
nginx.ingress.kubernetes.io/proxy-body-size: "55m"
```

The Helm value remains overridable through the existing ingress-annotation mechanism. The backend request-body limit is slightly above 50 MiB so protocol overhead does not reject an otherwise valid maximum-size upload. Direct S3 uploads bypass the ingress body path but retain the same application limits.

## Public File API

All public endpoints require the existing session access checks.

- `GET /api/sessions/{id}/files/capabilities` returns storage mode, upload limits, directly previewable types, Office-renderer state, and per-type reasons when unavailable.
- `POST /api/sessions/{id}/files` reserves a file and returns its upload descriptor.
- `PUT /api/sessions/{id}/files/{fileId}/content` accepts pod-mode content only.
- `POST /api/sessions/{id}/files/{fileId}/complete` validates and finalizes an upload.
- `GET /api/sessions/{id}/files` lists authorized file metadata without content URLs.
- `GET /api/sessions/{id}/files/{fileId}/content` redirects to a short S3 GET URL or proxies pod content.
- `DELETE /api/sessions/{id}/files/{fileId}` removes a file when the caller has management permission and the file is not already deleted.
- `GET /api/sessions/{id}/files/presentation` returns the selected file ID and revision.
- `PUT /api/sessions/{id}/files/presentation` selects or dismisses a file for writable callers.

Share-link variants follow the existing shared-session routing and role checks. A valid read-only share can list and read existing ready files but cannot reserve, upload, delete, or present them.

## Internal Agent API

Internal endpoints use `X-Agent-Token` and require the token to resolve to the route's session.

- capabilities and ready-file metadata;
- materialization information for a file ID;
- agent-file reservation and completion;
- presentation selection and dismissal;
- optional preview-render requests.

S3 uploads return presigned URLs. In pod mode, the MCP copies a source file into the managed root before registering the controlled relative locator. Agent uploads accept sources only from `/workspace` or the explicit AgentHub output directory. Canonical-path validation rejects home, credential, secret, procfs, device, and escaping symlink paths.

## Managed MCP Interface

The MCP server is named `agenthub_files` and exposes:

### `list_display_capabilities`

Returns current storage mode, size/count limits, supported direct-preview MIME types, Office conversion types, renderer health, and a machine-readable reason for every unavailable capability.

### `list_files`

Returns ready session files with ID, safe name, MIME type, size, source, preview status, and expiry state. It never returns object keys, credentials, presigned URLs, or arbitrary paths.

### `read_file`

Accepts a session file ID. It returns image files as MCP image content, bounded text as MCP text content, and metadata plus an instruction for document types that require a specialized reader. Oversized text is truncated with an explicit byte count.

### `upload_file`

Accepts a canonical local path below an allowed root and an optional display name. It validates the source, reserves a record, stores the content through the active storage mode, and returns safe metadata.

### `present_file`

Accepts either an existing file ID or an allowed local path. A local path is uploaded first. The tool updates the session presentation revision and returns the presented file metadata and preview capability.

### `dismiss_presentation`

Clears the current file selection and increments the presentation revision.

The MCP tool descriptions explicitly tell the agent which formats the UI can render. Tool calls remain subject to the existing built-in MCP policy and permission mechanisms.

## Preview Behavior

### Direct previews

- PNG, JPEG, WebP, and GIF render through authorized content URLs.
- PDF renders in a sandboxed embedded viewer.
- Markdown uses the existing sanitized Markdown renderer.
- Plain and structured text render in a bounded monospace view.

Content-Disposition, MIME sniffing protections, sandbox attributes, and the application content-security policy prevent an uploaded document from executing as application-origin content. HTML and SVG remain download-only.

### Office previews

DOCX, PPTX, and XLSX use an optional `artifact-renderer` service. The backend streams the source to the service and receives a PDF derivative. The service:

- uses headless LibreOffice;
- runs as a non-root user with a read-only root filesystem and bounded temporary volume;
- has CPU, memory, input-size, output-size, and wall-clock limits;
- has no outbound network access;
- does not enable macros or interactive content.

The derivative is a separate `session_files` record linked from the source. It inherits the source's authorization and storage lifetime and is reused until the source changes. Conversion is asynchronous. The UI shows queued, converting, ready, or failed state without blocking the agent response.

The renderer is shipped but disabled by default in Helm. When disabled, unhealthy, or timed out, Office originals remain downloadable and capabilities report the exact reason no graphical preview is available.

## UI Behavior

### Composer

- Paste, drop, and selection share one validation and upload path.
- Duplicate clipboard events do not create duplicate attachment cards.
- Upload progress and cancellation are visible.
- A failed attachment can be retried or removed without losing message text.
- Send is enabled only when all retained attachments are ready.
- User bubbles show attachment cards before optional text.

### Files workspace

- The file list shows name, type, size, source, preview state, and expiry state.
- Selecting a file opens its preview without changing the presentation revision.
- An MCP or writable-user presentation selects the file for all viewers of that session.
- A new presentation automatically activates the Files tab but does not stop or destroy an active browser.
- Switching back to Browser preserves browser state.
- Closing Files dismisses only the local panel unless the caller explicitly invokes the shared dismiss action.
- Downloads always use an authorized API request and never embed a durable public URL.

The frontend polls presentation metadata at a short interval and compares revisions. It does not poll binary data. This works with multiple backend replicas and existing shared sessions without coupling presentation events to one terminal WebSocket connection.

## Authorization

- Session owners and collaborators with write permission may reserve, upload, and present files.
- Management permission is required to delete a file.
- Read-only users and valid read-only share links may list, preview, and download ready files.
- The managed MCP uses the session callback token and can access only that session.
- A file ID is never sufficient authorization by itself.
- Presigned URLs have short TTLs and are minted only after an authorized request.

## Validation and Isolation

- Filenames are display metadata; storage paths are generated from file IDs and sanitized names.
- Upload validation combines limits, extension allowlist, declared MIME, detected MIME, and magic bytes.
- Path canonicalization happens before every agent-originated registration.
- Symlinks and reparse-like escapes outside allowed roots are rejected.
- The renderer receives content, not S3 credentials or unrestricted fetch URLs.
- Office rendering has no egress and no access to agent credentials or workspaces.
- Logs include IDs, sizes, states, and detected types but never binary content, credentials, presigned URLs, or uncontrolled file paths.

Malware scanning is not part of the first release. Deployments that require it can add a scanner between upload completion and the transition to `ready`; the state model deliberately preserves that insertion point.

## Error Behavior

- Invalid or oversized reservation: `400 Bad Request` or `413 Payload Too Large` with a stable error code.
- Session quota exceeded: `409 Conflict` with current and maximum usage.
- No live pod in temporary mode: `409 Conflict` with `temporary_storage_unavailable`.
- S3 operation fails: the record remains non-ready and the client receives a retryable storage error; no temporary fallback occurs.
- Upload content does not match reservation: the record becomes failed and content is removed best-effort.
- Missing pod content: the record becomes expired and returns `410 Gone`.
- Unsupported preview: content remains downloadable and capabilities provide the reason.
- Renderer timeout or failure: source remains ready, derivative becomes failed, and retry is available.
- Unsupported visual delivery for the active provider: the chat draft is retained and the turn is not sent.

## Lifecycle and Cleanup

- Reserved and uploading records expire after a configurable short interval.
- Deleting a ready file marks metadata deleted first, then removes S3 or pod content best-effort.
- Session deletion marks all file records deleted, clears presentation state, and removes persistent objects best-effort.
- Pod-backed content expires with the pod. Backend reconciliation marks stale pod records expired.
- S3-backed files and previews survive pause/resume and pod replacement.
- Rendered derivatives are cleaned up with their source or session.

## Configuration and Deployment

Configuration covers:

- file and message size limits;
- session file-count and byte quotas;
- presigned URL TTL;
- reservation expiry;
- presentation polling interval;
- accepted types;
- Office renderer enablement, image, resources, timeout, and output limit;
- ingress annotations and backend request-body limit.

Helm adds the optional renderer Deployment, Service, NetworkPolicy, security context, and resource settings. Static Kubernetes manifests receive equivalent settings. The renderer has ingress only from the backend and no egress. Existing browser and agent NetworkPolicies are extended only for the exact internal file routes they require.

## Compatibility and Rollout

- Existing text chat and terminal WebSocket messages remain valid.
- Attachment fields are optional, so old clients continue sending text-only messages.
- Existing artifacts under `artifacts/` are not migrated into `session_files` automatically.
- The managed file MCP is registered independently of user MCP configuration.
- Office preview is capability-gated and does not prevent the core file feature from starting.
- Deployments without S3 clearly advertise `pod-temporary` storage and its lifecycle.

## Testing Strategy

### Backend tests

- file-key generation and name sanitization;
- reservation, completion, failure, expiry, deletion, and reconciliation transitions;
- extension/MIME/magic-byte validation;
- per-file, per-message, file-count, and session-byte limits;
- S3 presign and completion verification;
- pod upload and authorized content proxy behavior;
- owner, collaborator, read-only, and share-link access;
- presentation revision monotonicity and stale-response handling;
- session deletion cleanup;
- renderer disabled, healthy, failed, and timed-out states.

### Runtime and MCP tests

- common attachment schema and backward-compatible text messages;
- materialization and provider capability selection;
- rejection when no visual delivery path exists;
- managed-root traversal and symlink rejection;
- bounded upload and download;
- every MCP input schema and safe output shape;
- image MCP content, bounded text content, upload, present, and dismiss;
- managed MCP registration for Claude, Codex, Cursor, and OpenClaw.

### Frontend tests

- paste, drag-and-drop, file selection, deduplication, removal, progress, retry, and cancellation;
- attachment-only and text-plus-attachment messages;
- send gating while uploads are incomplete;
- transcript attachment replay and expired cards;
- Files/Browser companion tabs, split resizing, mobile tabs, and state preservation;
- presentation revision polling and automatic Files activation;
- direct previews, Office states, unsupported types, and authorized downloads.

### Deployment tests

- NGINX `55m` body annotation and override behavior;
- backend body-size configuration;
- renderer enabled and disabled Helm output;
- renderer security context, resources, temporary volume, and no-egress policy;
- agent/backend file-route NetworkPolicy rules;
- parity between Helm and static manifests.

### End-to-end acceptance

1. Without S3, paste an image into a live structured chat, send it, and verify the agent analyzes the visual content. End the pod and verify the transcript shows the attachment as expired.
2. With MinIO, upload an image, restart the pod, resume the session, and verify the image and thumbnail remain accessible.
3. Have each available agent runtime upload and present a generated image through `agenthub_files`; verify the Files workspace opens beside chat or terminal.
4. Preview PDF and Markdown directly.
5. With the renderer enabled, preview DOCX, PPTX, and XLSX as cached PDF derivatives.
6. With the renderer disabled, verify capabilities and UI explain that Office files are download-only.
7. Verify writable collaborators can upload and present, while read-only users and share links can only view and download.
8. Attempt oversize, mismatched-type, traversal, cross-session, and expired-file access and verify the documented failures.

## Technical Basis

The provider-neutral layer is required because current agent interfaces differ. Claude Code supports streaming JSON input but documents that user messages on that interface are currently text-only. Codex exposes local-image inputs in its maintained interfaces. Cursor documents MCP support, and OpenClaw documents media and attachment handling. MCP tool results can carry image content, which supplies the common visual fallback.

Primary references:

- Anthropic Claude Code CLI and streaming input: <https://docs.anthropic.com/en/docs/claude-code/cli-usage>
- OpenAI Codex app-server turn input: <https://github.com/openai/codex/blob/main/codex-rs/app-server/README.md>
- OpenAI Codex CLI image arguments: <https://github.com/openai/codex/blob/main/codex-rs/exec/src/cli.rs>
- Cursor CLI MCP support: <https://docs.cursor.com/en/cli/using>
- OpenClaw media understanding: <https://docs.openclaw.ai/nodes/media-understanding>
- Model Context Protocol tool content: <https://modelcontextprotocol.io/specification/2024-11-05/server/tools>

