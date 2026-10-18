import { describe, expect, it, vi } from 'vitest'
import { terminalAttachmentPaths, terminalSafeFile, uploadAttachmentFiles } from './attachments.js'

describe('terminalSafeFile', () => {
  const png = name => new File(['x'], name, { type: 'image/png', lastModified: 5 })
  it('names a clipboard image by when it was pasted', () => {
    const now = new Date(2026, 9, 18, 16, 12, 5)
    expect(terminalSafeFile(png('image.png'), { pasted: true, now }).name).toBe('pasted-image-20261018-161205.png')
    expect(terminalSafeFile(png('image.png'), { pasted: true, now, index: 1 }).name).toBe('pasted-image-20261018-161205-2.png')
  })
  it('keeps a real name, made pasteable', () => {
    expect(terminalSafeFile(png('Screen Shot (1).PNG')).name).toBe('Screen-Shot-1.png')
    const safe = png('diagram.png')
    expect(terminalSafeFile(safe)).toBe(safe)
    expect(terminalSafeFile(png('diagram.png'), { pasted: true }).name).toBe('diagram.png')
  })
  it('keeps type and timestamp, so the upload checks still hold', () => {
    const file = terminalSafeFile(png('a b.png'))
    expect([file.type, file.lastModified]).toEqual(['image/png', 5])
  })
})

describe('terminalAttachmentPaths', () => {
  it('returns the paths in the order of the ids', async () => {
    const api = { terminalAttachments: vi.fn().mockResolvedValue({ attachments: [{ id: 'b', path: '/b.png' }, { id: 'a', path: '/a.png' }] }) }
    expect(await terminalAttachmentPaths(api, 's1', ['a', 'b'])).toEqual(['/a.png', '/b.png'])
  })
  it.each([[501, 'from before terminal attachments'], [503, 'not running'], [422, 'Rename'], [500, 'could not be handed']])('maps %s to a reason', async (status, text) => {
    const api = { terminalAttachments: vi.fn().mockRejectedValue(Object.assign(new Error('x'), { status })) }
    await expect(terminalAttachmentPaths(api, 's1', ['a'])).rejects.toThrow(text)
  })
  it('refuses an answer missing a path rather than pasting nothing', async () => {
    const api = { terminalAttachments: vi.fn().mockResolvedValue({ attachments: [] }) }
    await expect(terminalAttachmentPaths(api, 's1', ['a'])).rejects.toThrow()
  })
})

describe('uploadAttachmentFiles', () => {
  it('resolves to the ids once all are stored, and rejects with the composer message', async () => {
    const api = {
      reserveSessionFile: vi.fn(async () => ({ file: { id: 'f1' }, upload: { kind: 'proxy', url: '/u' } })),
      uploadSessionFile: vi.fn().mockResolvedValue(null),
      completeSessionFile: vi.fn(async (_s, id) => ({ id }))
    }
    const file = new File(['x'], 'a.png', { type: 'image/png' })
    expect(await uploadAttachmentFiles({ sessionId: 's1', api, files: [file] })).toEqual(['f1'])
    await expect(uploadAttachmentFiles({ sessionId: 's1', api, files: [new File(['x'], 'a.exe', { type: '' })] })).rejects.toThrow('not supported')
  })
})
