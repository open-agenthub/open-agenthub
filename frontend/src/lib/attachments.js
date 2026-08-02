const MIB = 1024 * 1024
export const ATTACHMENT_LIMITS = Object.freeze({
  maxFiles: 5,
  maxImageBytes: 20 * MIB,
  maxDocumentBytes: 50 * MIB,
  maxMessageBytes: 50 * MIB
})

const MIME_BY_EXTENSION = Object.freeze({
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.jpeg': 'image/jpeg',
  '.webp': 'image/webp',
  '.gif': 'image/gif',
  '.pdf': 'application/pdf',
  '.md': 'text/markdown',
  '.markdown': 'text/markdown',
  '.txt': 'text/plain',
  '.docx': 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
  '.pptx': 'application/vnd.openxmlformats-officedocument.presentationml.presentation',
  '.xlsx': 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet'
})

const COPY = Object.freeze({
  unsupported_file_type: 'This file type is not supported.',
  content_type_mismatch: 'File content does not match its type.',
  file_too_large: 'This file is too large.',
  message_too_large: 'Attachments exceed the 50 MiB message limit.',
  too_many_files: 'You can attach up to 5 files.',
  duplicate_file: 'This file is already attached.',
  upload_unavailable: 'File upload is not available for this session.',
  session_file_limit: 'This session has reached its file limit.',
  session_storage_limit: 'This session has reached its storage limit.',
  upload_failed: 'Upload failed. Try again.'
})

function extension(name) {
  const index = String(name).lastIndexOf('.')
  return index < 0 ? '' : String(name).slice(index).toLowerCase()
}

export function attachmentFingerprint(file) {
  return `${file.name}:${file.size}:${file.lastModified}:${file.type}`
}

function errorCode(error) {
  if (typeof error?.code === 'string') return error.code
  const match = String(error?.message || '').match(/"code"\s*:\s*"([a-z_]+)"/)
  return match?.[1] || 'upload_failed'
}

export function attachmentErrorMessage(error) {
  return COPY[errorCode(error)] || COPY.upload_failed
}

export function validateAttachmentBatch(files, existing = []) {
  const accepted = []
  const errors = []
  const seen = new Set(existing.map(item => item.fingerprint).filter(Boolean))
  const candidates = Array.from(files || [])
  if (existing.length + candidates.length > ATTACHMENT_LIMITS.maxFiles) {
    return { accepted, errors: [{ code: 'too_many_files', message: COPY.too_many_files }] }
  }

  let total = existing.reduce((sum, item) => sum + (item.file?.size || item.size || 0), 0)
  for (const file of candidates) {
    const expectedMime = MIME_BY_EXTENSION[extension(file.name)]
    const fingerprint = attachmentFingerprint(file)
    let code = null
    if (!expectedMime) code = 'unsupported_file_type'
    else if (file.type !== expectedMime) code = 'content_type_mismatch'
    else if (seen.has(fingerprint)) code = 'duplicate_file'
    else if (file.size > (expectedMime.startsWith('image/') ? ATTACHMENT_LIMITS.maxImageBytes : ATTACHMENT_LIMITS.maxDocumentBytes)) code = 'file_too_large'
    else if (total + file.size > ATTACHMENT_LIMITS.maxMessageBytes) code = 'message_too_large'
    if (code) errors.push({ file, code, message: COPY[code] })
    else {
      accepted.push(file)
      seen.add(fingerprint)
      total += file.size
    }
  }
  return { accepted, errors }
}

let nextKey = 0

export function createAttachmentQueue({ sessionId, api, onChange = () => {} }) {
  const items = []
  const changed = () => onChange(items)

  async function cleanup(item) {
    if (!item.id || item.cleanupStarted) return
    item.cleanupStarted = true
    try {
      await api.deleteSessionFile(sessionId, item.id)
    } catch { /* a reserved file also expires server-side */ }
  }

  async function upload(item) {
    item.state = 'uploading'
    item.progress = 5
    item.error = ''
    item.controller = new AbortController()
    changed()
    try {
      if (!item.upload) {
        const reserved = await api.reserveSessionFile(sessionId, {
          name: item.file.name,
          mimeType: item.file.type,
          size: item.file.size,
          batchId: item.batchId
        })
        item.id = reserved.file.id
        item.upload = reserved.upload
      }
      if (item.state === 'cancelled') return await cleanup(item)
      item.progress = 35
      changed()
      await api.uploadSessionFile(item.upload, item.file, { signal: item.controller.signal })
      if (item.state === 'cancelled') return await cleanup(item)
      item.progress = 85
      changed()
      const ready = await api.completeSessionFile(sessionId, item.id)
      if (item.state === 'cancelled') return await cleanup(item)
      Object.assign(item, {
        id: ready.id,
        name: ready.name || item.file.name,
        mimeType: ready.mimeType || item.file.type,
        size: ready.size ?? item.file.size,
        state: 'ready',
        progress: 100,
        error: ''
      })
    } catch (error) {
      if (item.state !== 'cancelled') {
        item.state = 'failed'
        item.error = attachmentErrorMessage(error)
      }
    } finally {
      item.controller = null
      changed()
    }
    return item
  }

  function add(files) {
    const { accepted, errors } = validateAttachmentBatch(files, items.filter(item => item.state !== 'cancelled'))
    const batchId = globalThis.crypto?.randomUUID?.() || `chat-${Date.now()}`
    const added = accepted.map(file => {
      const item = {
        key: `attachment-${++nextKey}`,
        fingerprint: attachmentFingerprint(file),
        file,
        name: file.name,
        mimeType: file.type,
        size: file.size,
        batchId,
        state: 'queued',
        progress: 0,
        error: ''
      }
      items.push(item)
      item.promise = upload(item)
      return item
    })
    for (const error of errors) {
      items.push({
        key: `attachment-${++nextKey}`,
        fingerprint: error.file ? attachmentFingerprint(error.file) : '',
        file: error.file,
        name: error.file?.name || 'Attachments',
        mimeType: error.file?.type || '',
        size: error.file?.size || 0,
        state: 'failed',
        progress: 0,
        error: error.message,
        validationError: true
      })
    }
    changed()
    return added
  }

  async function retry(key) {
    const item = items.find(candidate => candidate.key === key)
    if (!item || item.validationError || !item.file) return null
    item.promise = upload(item)
    return item.promise
  }

  async function remove(key) {
    const index = items.findIndex(candidate => candidate.key === key)
    if (index < 0) return
    const item = items[index]
    item.state = 'cancelled'
    item.controller?.abort()
    items.splice(index, 1)
    changed()
    await cleanup(item)
  }

  function clearReady() {
    for (let index = items.length - 1; index >= 0; index -= 1) {
      if (items[index].state === 'ready') items.splice(index, 1)
    }
    changed()
  }

  async function cancelAll() {
    await Promise.all(items.map(item => remove(item.key)))
  }

  return { items, add, retry, remove, clearReady, cancelAll }
}
