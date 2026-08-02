import { describe, expect, it } from 'vitest'
import { previewKind } from './files.js'

const ready = (mimeType, extra = {}) => ({ id: 'f1', name: 'file', mimeType, state: 'Ready', ...extra })

describe('file preview routing', () => {
  it.each([
    ['image/png', 'image'],
    ['application/pdf', 'pdf'],
    ['text/markdown', 'markdown'],
    ['text/plain', 'text'],
    ['text/html', 'download'],
    ['image/svg+xml', 'download']
  ])('routes %s to %s', (mimeType, expected) => {
    expect(previewKind(ready(mimeType), {})).toBe(expected)
  })

  it('honors the inline types advertised by the capabilities endpoint', () => {
    expect(previewKind(ready('image/png'), { directPreviewMimeTypes: ['application/pdf'] }))
      .toBe('download')
  })

  it('routes office conversions and lifecycle states explicitly', () => {
    const docx = 'application/vnd.openxmlformats-officedocument.wordprocessingml.document'
    expect(previewKind(ready(docx, { previewState: 'Ready', previewFileId: 'pdf-1' }), {})).toBe('office-pdf')
    expect(previewKind(ready(docx, { previewState: 'Converting' }), {})).toBe('pending')
    expect(previewKind({ ...ready('image/png'), state: 'Expired' }, {})).toBe('expired')
    expect(previewKind({ ...ready('image/png'), state: 'Uploading' }, {})).toBe('pending')
  })
})
