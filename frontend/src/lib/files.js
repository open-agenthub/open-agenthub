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
