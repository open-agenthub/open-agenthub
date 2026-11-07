import { describe, it, expect } from 'vitest'
import { capabilityText, catalogQuery, catalogStatus, modelOptions, supportsModelChoice, unlistedNote } from './model-catalog.js'

const now = Date.parse('2026-11-07T12:00:00Z')
const view = (catalog, extra = {}) => ({ freshness: 'fresh', fetchedAt: '2026-11-07T11:50:00Z', catalog, ...extra })

describe('model catalog helpers', () => {
  it('asks for one account, an endpoint by URL, and nothing for OpenClaw', () => {
    expect(supportsModelChoice('OpenClaw')).toBe(false)
    expect(catalogQuery({ agent: 'OpenClaw', authMode: 'ApiKey' })).toBeNull()
    expect(catalogQuery({ agent: 'Codex', authMode: 'Subscription', credentialId: 'acct-1' }))
      .toEqual({ agent: 'Codex', authMode: 'Subscription', credentialId: 'acct-1' })
    expect(catalogQuery({ agent: 'Claude', authMode: 'ApiKey', credentialId: '' })).toEqual({ agent: 'Claude', authMode: 'ApiKey' })
    expect(catalogQuery({ agent: 'Codex', authMode: 'CustomEndpoint', modelEndpointUrl: 'https://llm.ex' }))
      .toEqual({ agent: 'Codex', authMode: 'CustomEndpoint', endpointUrl: 'https://llm.ex' })
    expect(catalogQuery({ agent: 'Codex', authMode: 'CustomEndpoint', modelEndpointUrl: 'half typed' })).toBeNull()
  })

  it('says how far the list can be trusted', () => {
    expect(catalogStatus({ freshness: 'unknown' }).text).toMatch(/No list for this account yet/)
    expect(catalogStatus(view({ state: 'ok', verified: true, source: 'cli', models: [] }), now)).toEqual({ tone: 'ok', text: "This account's models (10 min ago)." })
    expect(catalogStatus(view({ state: 'ok', verified: false, source: 'cli', models: [] }), now).text).toMatch(/not checked against this account/)
    expect(catalogStatus(view({ state: 'ok', source: 'endpoint', verified: true }), now).text).toMatch(/endpoint lists/)
    const fallback = catalogStatus(view({ state: 'fallback', models: [] }), now)
    expect(fallback.tone).toBe('warn')
    expect(fallback.text).toMatch(/refused/)
    const error = catalogStatus(view({ state: 'error', error: { code: 'not_signed_in', message: 'Authentication required.' } }), now)
    expect(error).toMatchObject({ tone: 'warn' })
    expect(error.text).toMatch(/Authentication required\..*Type a model id/)
    const stale = catalogStatus(view({ state: 'ok', verified: true }, { freshness: 'stale', fetchedAt: '2026-11-04T12:00:00Z' }), now)
    expect(stale.tone).toBe('warn')
    expect(stale.text).toMatch(/may be out of date \(3 days ago\)/)
  })

  it('offers the default first and nothing from a failed probe', () => {
    const v = view({ state: 'ok', models: [{ id: 'a' }, { id: 'b', isDefault: true }] })
    expect(modelOptions(v).map(m => m.id)).toEqual(['b', 'a'])
    expect(modelOptions(view({ state: 'error', models: [{ id: 'a' }] }))).toEqual([])
    expect(modelOptions(null)).toEqual([])
  })

  it('describes controls and keeps an unlisted choice with a note', () => {
    expect(capabilityText({ capabilities: { reasoningEfforts: ['low', 'high'], contextWindow: 1_048_576, imageInput: true, fastMode: true } }))
      .toBe('effort low/high · 1M context · images · fast mode')
    expect(capabilityText({ id: 'x' })).toBe('')
    const v = view({ state: 'ok', models: [{ id: 'default', resolvedId: 'model-large-5' }] })
    expect(unlistedNote(v, 'model-large-5')).toBe('')
    expect(unlistedNote(v, '')).toBe('')
    expect(unlistedNote(v, 'gone')).toMatch(/“gone” is not in this account's list. It is kept as typed/)
    expect(unlistedNote({ freshness: 'unknown' }, 'anything')).toBe('')
  })
})
