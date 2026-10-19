# Video in session files

An agent that rendered a demo video had no way to hand it over. `upload_file` refused `.mp4`
with `unsupported_file_type`, because both allowlists — the in-pod MCP server's MIME-by-extension
map and `SessionFileValidator` — stopped at images, PDF, text and Office documents. The only way
out was a base64 text file.

Session files now accept **MP4 (H.264 video, AAC audio)** and **WebM (VP8, VP9 or AV1 video,
Opus or Vorbis audio)**, and the Files pane plays them in a native `<video controls>` element
with seeking. This file records what was decided and what was rejected.

## Checked by content, down to the codec

`VideoContainerInspector` walks the container rather than matching a magic number. For MP4 it
reads the top-level boxes: `ftyp` first with an ISO/MP4 brand, a `moov` somewhere (before or
after `mdat`, both are common), every video track's sample entry `avc1`/`avc3`, every audio
track's `mp4a`. For WebM it reads the EBML header (`DocType` must be `webm`, so a renamed
Matroska file is refused) and the `Tracks` element up to the first `Cluster`.

**The alternative was a signature check** — `ftyp` at offset 4, `1A 45 DF A3` at offset 0. It was
rejected because it does not answer the question that matters, which is whether the Files pane
can play the file. An HEVC MP4 carries the same `ftyp` as an H.264 one and does not play in every
browser; a HEIC photo starts with an `ftyp` box too; a Matroska file has the same EBML signature
as WebM. A file that passes the check and then shows a broken player is worse than a clear
refusal at upload, where the agent can re-encode.

Tracks the player ignores — timecode, chapters, metadata — do not cause a refusal; refusing them
would refuse ordinary editor exports.

**The file is streamed, not buffered.** Completion used to read every upload into memory, bounded
by the document limit, before detecting its type. A video is allowed to be larger than that, and
only the `moov` box or the `Tracks` element is needed to decide; the inspector keeps those (each
capped at 16 MiB, far above any real file) and counts the rest of the bytes as they pass, so the
total is still compared with the reserved size.

A renamed non-video fails at completion with `content_type_mismatch`, like a renamed image does.

## Its own limit, and the pod fallback refuses large videos

`files.maxVideoBytes` (default 100 MiB) is separate from the image and document limits, so raising
it does not move theirs. The upload routes take their request-body limit from the largest of the
three instead of a fixed attribute, which could not follow a chart value; the reserved size,
enforced while streaming, stays the real bound for each file.

**Without object storage a video is held to the document limit, however high the video limit is.**
The issue asked whether the fallback storage should refuse large videos; it does, at reservation.
The fallback is not a database: it is the session pod's own disk, so the copy disappears when the
pod stops, and the pod's file store caps every file at 50 MiB (`AGENTHUB_FILE_MAX_BYTES`). The
alternatives were to raise that cap for video, which would put up to 100 MiB per file on a volume
the agent is also working in, for a copy that does not outlive the pod; or to leave it, which
would accept a reservation the pod then refuses halfway through the transfer. Refusing up front
returns `file_too_large` before any byte moves.

An agent's upload reaches the backend in the cluster, so the ingress body limit (`ingress.bodySize`,
55m) does not apply to it. The Files pane and the composer accept video too, but their uploads go
through the ingress and the browser's queue holds every upload to the 50 MiB message limit; a
larger video from the browser would need both raised, which was left for when someone asks.

## Playback: Range, and a ticket instead of the bearer header

The browser's content routes — the owner's, a shared link's and the stream route below — answer a
single `bytes=` range with 206 and `Content-Range`, a range past the end with 416, and advertise
`Accept-Ranges: bytes`. The range is asked of the source — S3
`GetObject` with a byte range, the pod's `GET /agenthub/files/:id` with a `Range` header — rather
than read from the start and discarded. Where a source answers 200 anyway (a pod still running an
older runtime, a storage server ignoring ranges) the backend slices the stream itself, so the
answer is correct either way. Multi-range and malformed headers get the whole file, as RFC 9110
allows.

A `<video>` element fetches its source itself, in ranges, and cannot send the `Authorization`
header every other file read carries. Three ways round that were considered:

- **Fetch the whole file into a blob first**, as the other previews do. Rejected: playback waits
  for the full download, and the tab holds up to `maxVideoBytes` in memory.
- **The access token in the query string**, as the terminal WebSocket does. Rejected: the token is
  good for every API call for its whole lifetime, and a media URL lands in proxy logs and the media
  cache.
- **A Data Protection token.** Rejected: the key ring is not shared between backend replicas here
  (two by default), so a URL issued by one pod would fail on the other.

What was built is a **stream ticket**: `POST /api/sessions/{id}/files/{fileId}/stream` returns a
URL with a random ticket whose SHA-256 is stored in Postgres (`session_file_stream_tickets`),
bound to one file and the requesting principal, valid for `files.streamTicketMinutes` (120). The
ticket only says who asked: `GET …/stream?ticket=` resolves that principal's access to the session
on every request, so revoking a share or deleting the file (the row cascades) stops the next
range. Tickets are issued for video only — for any other type it would be a second, header-free
way to read a file that already has one. When a ticket expires under an open player, the pane
asks for one fresh ticket and resumes at the same position.

A shared link needs none of this: its credential is already in the URL, so the player is pointed
at `/api/shared/{token}/files/{fileId}/content`, which honours ranges like the owner's routes and
applies the same viewer/collaborator rules as before.

Video is served `inline` with its detected type and `X-Content-Type-Options: nosniff`. Download
still fetches the original bytes under the original name, as for every other type.

## Agents

`read_file` and `fetch_project_file` put a video on the pod's disk and return its path and
metadata. Its bytes are never returned inline — the inline path names images and small text only.
