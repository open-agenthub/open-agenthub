// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'

const authMocks = vi.hoisted(() => ({
  getToken: vi.fn().mockResolvedValue('access-token'),
  login: vi.fn(),
  auth: { enabled: true }
}))

vi.mock('./auth.js', () => ({
  auth: authMocks.auth,
  getToken: authMocks.getToken,
  login: authMocks.login,
  initAuth: vi.fn(),
  config: {}
}))

import {
  api,
  getSessionFileContent,
  getSharedFileCapabilities,
  getSharedSessionFileContent,
  getSharedFilePresentation,
  listSharedSessionFiles,
  sharedSessionFileContentUrl
} from './api.js'

describe('session file API', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    globalThis.fetch = vi.fn().mockResolvedValue({
      ok: true,
      status: 200,
      blob: vi.fn().mockResolvedValue(new Blob(['file'])),
      json: vi.fn().mockResolvedValue({ ok: true }),
      text: vi.fn().mockResolvedValue('')
    })
  })

  it('encodes session and file ids on authenticated metadata routes', async () => {
    await api.reserveSessionFile('session/one', { name: 'shot.png', mimeType: 'image/png', size: 8 })
    expect(fetch).toHaveBeenLastCalledWith('/api/sessions/session%2Fone/files/reserve', expect.objectContaining({
      method: 'POST',
      headers: expect.objectContaining({ Authorization: 'Bearer access-token' })
    }))

    await api.completeSessionFile('session/one', 'file/two')
    expect(fetch).toHaveBeenLastCalledWith('/api/sessions/session%2Fone/files/file%2Ftwo/complete', expect.any(Object))
  })

  it('uploads a proxy file with auth and descriptor headers', async () => {
    const body = new Blob(['image'])
    await api.uploadSessionFile({
      kind: 'proxy',
      url: '/api/sessions/s1/files/f1/content',
      headers: { 'Content-Type': 'image/png' }
    }, body)

    expect(fetch).toHaveBeenCalledWith('/api/sessions/s1/files/f1/content', {
      method: 'PUT',
      headers: { 'Content-Type': 'image/png', Authorization: 'Bearer access-token' },
      body
    })
  })

  it('does not leak the app bearer token to a presigned S3 upload', async () => {
    const body = new Blob(['image'])
    await api.uploadSessionFile({
      kind: 'presigned',
      url: 'https://storage.example/upload?signature=secret',
      headers: { 'Content-Type': 'image/png' }
    }, body)

    expect(fetch).toHaveBeenCalledWith('https://storage.example/upload?signature=secret', {
      method: 'PUT',
      headers: { 'Content-Type': 'image/png' },
      body
    })
  })

  it('builds content URLs without fetching or exposing presigned URLs', () => {
    expect(api.sessionFileContentUrl('session/one', 'file/two')).toBe(
      '/api/sessions/session%2Fone/files/file%2Ftwo/content')
    expect(sharedSessionFileContentUrl('token/value', 'file/two')).toBe(
      '/api/shared/token%2Fvalue/files/file%2Ftwo/content')
  })

  it('uses anonymous read-only shared routes', async () => {
    await getSharedFileCapabilities('token/value')
    expect(fetch).toHaveBeenLastCalledWith('/api/shared/token%2Fvalue/files/capabilities', expect.objectContaining({ method: 'GET' }))

    await listSharedSessionFiles('token/value')
    expect(fetch).toHaveBeenLastCalledWith('/api/shared/token%2Fvalue/files', expect.objectContaining({ method: 'GET' }))

    await getSharedFilePresentation('token/value')
    expect(fetch).toHaveBeenLastCalledWith('/api/shared/token%2Fvalue/files/presentation', expect.objectContaining({ method: 'GET' }))

    for (const [, init] of fetch.mock.calls) {
      expect(init.headers?.Authorization).toBeUndefined()
    }
  })

  it('fetches authenticated and shared file blobs without putting tokens in URLs', async () => {
    await getSessionFileContent('session/one', 'file/two')
    expect(fetch).toHaveBeenLastCalledWith('/api/sessions/session%2Fone/files/file%2Ftwo/content', {
      method: 'GET', headers: { Authorization: 'Bearer access-token' }
    })

    await getSharedSessionFileContent('token/value', 'file/two')
    expect(fetch).toHaveBeenLastCalledWith('/api/shared/token%2Fvalue/files/file%2Ftwo/content', {
      method: 'GET', headers: {}
    })
  })
})

describe('error mapping', () => {
  const failing = (status, body) => vi.fn().mockResolvedValue({
    ok: false, status, text: vi.fn().mockResolvedValue(body), json: vi.fn()
  })

  it('marks a 402 as license_required even when the body carries no code', async () => {
    // Most enterprise controllers answer with the message only; the gate keys on the code.
    globalThis.fetch = failing(402, '{"error":"An active enterprise license is required."}')
    const err = await api.listSessionShares('s1').catch(e => e)
    expect(err.status).toBe(402)
    expect(err.code).toBe('license_required')
    expect(err.message).toBe('402 {"error":"An active enterprise license is required."}')
  })

  it('keeps a code the body sends and ignores bodies that are not JSON', async () => {
    globalThis.fetch = failing(402, '{"error":"x","code":"seat_limit"}')
    expect((await api.listSessionShares('s1').catch(e => e)).code).toBe('seat_limit')

    globalThis.fetch = failing(402, 'Payment Required')
    expect((await api.listSessionShares('s1').catch(e => e)).code).toBe('license_required')
  })

  it('sets no code on other failures', async () => {
    globalThis.fetch = failing(404, '{"error":"not found","code":"session_not_found"}')
    const err = await api.listSessionShares('s1').catch(e => e)
    expect(err.status).toBe(404)
    expect(err.code).toBeUndefined()
  })

  it('applies the same mapping on the status-returning variant', async () => {
    globalThis.fetch = failing(402, '{"error":"license"}')
    const err = await api.setSignalPrefs({}).catch(e => e)
    expect(err.status).toBe(402)
    expect(err.code).toBe('license_required')
  })
})
