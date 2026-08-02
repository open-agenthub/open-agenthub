import { describe, expect, it } from 'vitest'
import { attachmentErrorMessage, createAttachmentQueue, validateAttachmentBatch } from './attachments.js'

const png = (name = 'shot.png', size = 12, lastModified = 1) =>
  new File([new Uint8Array(size)], name, { type: 'image/png', lastModified })

describe('chat attachments', () => {
  it('rejects unsupported, duplicate, and oversized batches before reserving', () => {
    expect(validateAttachmentBatch([new File(['x'], 'x.svg', { type: 'image/svg+xml' })]).errors[0].code)
      .toBe('unsupported_file_type')
    expect(validateAttachmentBatch([png(), png()]).accepted).toHaveLength(1)
    expect(validateAttachmentBatch(Array.from({ length: 6 }, (_, i) => png(`${i}.png`))).errors[0].code)
      .toBe('too_many_files')
    expect(validateAttachmentBatch([png('huge.png', 20 * 1024 * 1024 + 1)]).errors[0].code)
      .toBe('file_too_large')
  })

  it('uploads, completes, retries, and deletes reserved files on removal', async () => {
    let fail = true
    const calls = []
    const api = {
      reserveSessionFile: async () => ({ file: { id: 'f1' }, upload: { kind: 'proxy', url: '/upload' } }),
      uploadSessionFile: async () => { if (fail) throw Object.assign(new Error('failed'), { code: 'upload_failed' }) },
      completeSessionFile: async () => ({ id: 'f1', name: 'shot.png', mimeType: 'image/png', size: 12, state: 'Ready' }),
      deleteSessionFile: async (...args) => calls.push(args)
    }
    const queue = createAttachmentQueue({ sessionId: 's1', api })
    const [item] = queue.add([png()])
    await item.promise
    expect(item.state).toBe('failed')
    fail = false
    await queue.retry(item.key)
    expect(item).toMatchObject({ state: 'ready', id: 'f1' })
    await queue.remove(item.key)
    expect(calls).toEqual([['s1', 'f1']])
  })

  it('cleans up a reservation that finishes after the user removes the item', async () => {
    let finishReservation
    const deleted = []
    const api = {
      reserveSessionFile: () => new Promise(resolve => { finishReservation = resolve }),
      uploadSessionFile: async () => { throw new Error('must not upload') },
      completeSessionFile: async () => { throw new Error('must not complete') },
      deleteSessionFile: async (...args) => deleted.push(args)
    }
    const queue = createAttachmentQueue({ sessionId: 's1', api })
    const [item] = queue.add([png()])
    await Promise.resolve()
    const removal = queue.remove(item.key)
    finishReservation({
      file: { id: 'late-id' },
      upload: { kind: 'proxy', url: '/upload' }
    })
    await Promise.all([item.promise, removal])
    expect(deleted).toEqual([['s1', 'late-id']])
  })

  it('maps stable backend errors to useful copy', () => {
    expect(attachmentErrorMessage({ code: 'content_type_mismatch' }))
      .toBe('File content does not match its type.')
  })
})
