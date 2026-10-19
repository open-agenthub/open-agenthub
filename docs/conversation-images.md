# Images in the Workspace conversation

The terminal transport uploads a pasted image to session Files, materializes it, and pastes its
absolute path into the CLI (#148). The CLI then replaces the path with an image token. Reading
only the transcript's text parts lost the image after sending (#205).

The conversation carries `{ id, name }` attachment references on the user entry. The id comes
from the materializer's canonical `/.agenthub/files/<32-hex-id>/<name>` path. A basename, image
number, timestamp, repeated prompt text, or position beside another turn is never an identity.
Paths outside this layout remain text; they do not become file reads.

## Provider evidence and association

These are the records measured with the pinned CLIs, a mock model, the real session server and
Chromium's clipboard. They apply equally to a Workspace composer send and a terminal paste.

| Agent | Native record | Association |
| --- | --- | --- |
| Claude 2.1.287 | The user content has text and a base64 `image` block. A separate `isMeta: true`, `turnCompanion: true` user record has `[Image: source: <absolute path>]`. Both records have the same `promptId`. | Join only that companion to the user entry with its exact `promptId`, then extract the uploaded id from its source path. An unmatched companion is ignored. |
| Codex 0.160.0 | A `response_item` user message has an `input_text` wrapper `<image name=[Image #1] path="<absolute path>">`, an `input_image`, a closing wrapper, and the typed text. | The path and image belong to the same native user message. Repeated `event_msg` notifications remain excluded. |
| OpenCode 1.18.35 | Its SQLite message parts include `{ type: "file", mime: "image/png", url: "data:…", source: { type: "file", path: "<absolute path>" } }`. | The managed plugin reads native message parts through its own SDK client and writes an append-only transcript adapter under `.opencode/agenthub-transcripts/<session-id>.jsonl`. It keeps `source.path`, omits the redundant data URI, and excludes child sessions. The driver follows the remembered top-level session id. |

Rejected: matching image bytes or hashing base64 blocks. CLI resizing/re-encoding can change
those bytes, and two uploads can contain the same image. Neither identifies the uploaded file.
Rejected: opening OpenCode's live database from the session server. The CLI already exposes the
native parts to its plugin, without coupling the transport to SQLite's schema or locking.
The adapter records user messages and completed assistant text; streaming tool details remain
available in the terminal. SDK snapshots request the most recent 100 messages, normalized messages
keep at most 100 parts, 50,000 text characters and five image sources. A 200-id side index and a
512 KiB journal tail bound deduplication reads and recover an interrupted index update. Older
pre-adapter sessions can therefore export their most recent 100 messages rather than their entire
SQLite history.

Claude can append the companion after the user entry has already been polled. The incremental
parser retains the prompt identity internally and enriches that earlier entry across chunks.
The conversation identity includes a digest of attachment associations, so the browser reloads
an already displayed entry when it gains its attachment. New immutable entry copies keep an
in-flight page from being modified underneath its reader. Image-only user turns are retained.

## Content and access

A thumbnail uses the same authenticated blob fetch as FilePreview. A normal image URL cannot
carry the signed-in user's authorization header. Shared sessions use the existing shared Files
content endpoint with their share token, including Viewer and Collaborator links. No new
content endpoint or alternative access policy is introduced. Session/file membership, expired
content and revoked access remain decisions of the Files services.

Only PNG, JPEG, WebP and GIF response blobs are displayed. Clicking a thumbnail opens a native
modal dialog; Escape or its close button dismisses it. Object URLs are revoked on replacement
and unmount. Missing, expired, denied, unsupported or undecodable content shows an unavailable
message. Transcript base64 and arbitrary local filesystem paths never become browser sources.
Old transcripts without an exact source association cannot recover an uploaded id; the reader
does not guess. OpenCode needs the managed adapter before its turns have native conversation
records.

## Verification

The `workspace-composer-real-cli-e2e` rig was run on 2026-10-19 with the branch's Vite frontend,
real Chromium `ClipboardItem` PNG + Ctrl+V, the real pod PUT/materialize path, real common/server.js,
each pinned CLI in its PTY, and the backend's actual NativeTranscript and ConversationPage over
the runtime HTTP transcript. For **all three agents, both composer and terminal**: the model
received image content, the uploaded id appeared on the user entry, the thumbnail decoded,
clicking enlarged it, and a reload recovered it from the transcript. Repeated identical prompt
text with distinct uploads also passed.

For #189, plain OpenCode user and completed assistant text were rendered through the same
pipeline. Native /new left the remembered identity unchanged until the next prompt; that prompt
created a new session, changed the followed transcript, retained the old history and produced the
Workspace clear boundary. A real state archive was extracted into a fresh home at the same
absolute path. The actual driver launched the CLI with --session targeting the new id; a subsequent
model request retained the post-clear text and the resumed prompt and excluded the old task.
The native welcome composer text plus its command footer confirm context-clear readiness,
because waiting for the lazy transcript would deadlock a handover. The graphical logo and
ellipsis can use OSC 66, which headless xterm skips, so they cannot be readiness requirements.
The corrected predicate also passed the real runtime HTTP context-clear route: after an owner
turn, /new ran and the model received the handover without the previous task. This additional
probe does not exercise the full MCP-to-backend forwarding chain.

A second HTTP host ran the actual SessionFilesController, SharedSessionFilesController,
SessionAccessService, SessionFileService and AgentFileClient against the real pod file store,
with fixture identity/license/access/registry stores. Owner, Viewer and Collaborator user reads
and both share roles returned the uploaded PNG bytes. Unknown users, revoked shares and another
session returned 404. Real Chromium decoded and enlarged images for both share roles through
the shared Files routes; a revoked token showed unavailable images without falling back to the
owner route. This proves controller/service authorization and pod I/O, not a deployed identity
provider, database, object-storage redirect or cluster rollout.

Regression coverage includes cross-poll companion enrichment, wrong-prompt isolation,
image-only turns, exact Codex/OpenCode source paths, unrecognized paths and active image types,
shared versus signed-in fetch selection, denied/expired content, object URL cleanup, and adapter
deduplication/completed-message/subagent behavior.

After merging the single-reporter fix from main, the real `fleet-delivery-real-cli-rig` ran
OpenCode `SCENARIO=all` under static proot with the branch managed configuration. Mid-turn and
idle peers arrived as next turns; a peer waiting behind an owner draft stayed queued until the
owner submitted; priority delivery used the PTY. Each of the four peer messages appeared in
exactly one model request. This checks that the transcript adapter preserves the existing
single-reporter delivery path.
