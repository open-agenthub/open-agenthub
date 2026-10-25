# File type admission and preview safety

Upload admission used to be an extension allowlist in the backend, files MCP and browser.
That prevented agents and people from handing over ordinary archives, source files and SVG
artwork. The allowlist now selects safe previews rather than deciding whether a file may exist.
Every safe filename can be uploaded; unknown extensions, including extensionless names, become
`application/octet-stream` at reservation and completion. Their bytes are never sniffed into an
active preview type. Known preview formats retain their content validation.

SVG uses the image limit (20 MiB by default). Other opaque files use the document limit
(50 MiB by default). Existing video, batch, session count and storage limits still apply. An
unknown video container is an opaque document-sized download, not a streaming preview.

## SVG is an image

SVG must parse as XML with the SVG namespace and root. DTDs and entity resolution are disabled.
The original bytes are preserved, including any scripts: this is not a sanitizer. The preview
uses only an `img` element, whose image context disables scripts and external resource loads.
It never inserts uploaded SVG into the application's DOM or frames it as a document.

A data URL supplies that image. A blob URL was rejected for SVG because opening a blob URL as
a document inherits the creating application's origin; a data document has an opaque origin.
The API serves SVG as an attachment with `Content-Security-Policy: sandbox; default-src 'none'`
and `X-Content-Type-Options: nosniff`. SVG and opaque downloads are proxied even when public
object-storage redirects are enabled, because storage cannot guarantee those response headers.
The files MCP returns SVG only as a local path, never as inline text or raster image content.

Opaque downloads have the same response headers and attachment disposition. Browser download
links relabel their blobs as octet-stream, so opening a link instead of saving cannot turn an
HTML upload into an application-origin document. The alternative of retaining arbitrary
uploader MIME types was rejected because MIME labels control browser execution and previews.

## Proof

`tests/FileTypesProof` hosts the shipped controllers, authorizer, project-access rule and file
service over real Postgres and S3-compatible storage. Unchanged main rejects SVG, ZIP and
extensionless reservations with `unsupported_file_type`. The changed backend passes upload,
completion, byte-identical own/sibling download, strict headers and existing size ceilings for
SVG, HTML, ZIP, extensionless and empty files. The shipped stdio MCP passes upload/read and
sibling list/fetch without inlining SVG or opaque files. Real Chromium renders an SVG containing
script, onload and an external image as an image, with no script execution, external request or
inline SVG/iframe/object/embed. HTML stays download-only. Auth is synthetic in the proof host;
no live identity provider, kubelet or complete session-pod lifecycle is simulated by that host.
