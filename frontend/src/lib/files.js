const OFFICE_MIME_TYPES = new Set([
  'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
  'application/vnd.openxmlformats-officedocument.presentationml.presentation',
  'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet'
])

const IMAGE_MIME_TYPES = new Set(['image/png', 'image/jpeg', 'image/webp', 'image/gif'])

export function previewKind(file, capabilities = {}) {
  if (!file) return 'download'
  if (file.state === 'Expired') return 'expired'
  if (file.state !== 'Ready') return 'pending'
  const advertised = capabilities.directPreviewMimeTypes
  if (Array.isArray(advertised) && advertised.length &&
      !advertised.includes(file.mimeType) && !OFFICE_MIME_TYPES.has(file.mimeType)) return 'download'
  if (IMAGE_MIME_TYPES.has(file.mimeType)) return 'image'
  if (file.mimeType === 'application/pdf') return 'pdf'
  if (file.mimeType === 'text/markdown') return 'markdown'
  if (file.mimeType === 'text/plain') return 'text'
  if (OFFICE_MIME_TYPES.has(file.mimeType)) {
    if (file.previewState === 'Ready' && file.previewFileId) return 'office-pdf'
    if (['Queued', 'Converting'].includes(file.previewState)) return 'pending'
  }
  return 'download'
}

export function fileIcon(file) {
  const kind = previewKind(file)
  if (kind === 'image') return 'IMG'
  if (kind === 'pdf' || kind === 'office-pdf') return 'PDF'
  if (kind === 'markdown') return 'MD'
  if (kind === 'text') return 'TXT'
  return 'FILE'
}

export function fileSize(bytes) {
  if (!Number.isFinite(bytes)) return ''
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${Math.ceil(bytes / 1024)} KiB`
  return `${(bytes / 1024 / 1024).toFixed(1)} MiB`
}

// Saves bytes we already hold under the file's own name. Session content is reached with a bearer
// header, so a plain link to the content endpoint would arrive unauthenticated; the bytes have to
// be fetched first and handed to the browser from memory.
//
// The blob is relabelled application/octet-stream before it gets a URL. A blob: URL lives on this
// app's origin and is served with the blob's own type, so an uploaded text/html or image/svg+xml
// file — the types the backend marks download_only — would run as a document of ours if anything
// ever navigated to that URL instead of saving it (a browser ignoring the download attribute, a
// user opening the link in a new tab). Labelled octet-stream it can only be saved. slice() relabels
// without copying, so what lands on disk is the original bytes; the name comes from the download
// attribute, not from the type.
export function saveBlob(blob, name) {
  const url = URL.createObjectURL(blob.slice(0, blob.size, 'application/octet-stream'))
  const link = document.createElement('a')
  link.href = url
  link.download = name || 'download'
  link.rel = 'noopener'
  link.hidden = true
  document.body.appendChild(link)
  link.click()
  link.remove()
  // Revoking in the same task cancels the save in browsers that start reading the blob
  // asynchronously after the click.
  setTimeout(() => URL.revokeObjectURL(url), 60_000)
}
