// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import WebhooksPane from './WebhooksPane.vue'

const mocks = vi.hoisted(() => ({
  config: { gitEnabled: true },
  api: {
    listWebhookTriggers: vi.fn(),
    createWebhookTrigger: vi.fn(),
    deleteWebhookTrigger: vi.fn(),
    gitProviders: vi.fn()
  }
}))

vi.mock('../api.js', () => ({ api: mocks.api, config: mocks.config }))

const trigger = {
  id: 'wt1',
  name: 'review-bot',
  providerId: 'gitlab',
  events: ['opened', 'reopened'],
  repoFilter: null,
  promptTemplate: 'Review {{title}}',
  agent: 'Claude',
  autoApprove: false,
  url: 'https://hub.example.test/api/git/webhooks/wt1',
  lastTriggeredAt: null
}

describe('WebhooksPane', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.api.listWebhookTriggers.mockResolvedValue([])
    mocks.api.gitProviders.mockResolvedValue([
      { id: 'gitlab', displayName: 'GitLab', connected: true }
    ])
  })

  it('lists existing triggers with their delivery URL', async () => {
    mocks.api.listWebhookTriggers.mockResolvedValue([trigger])
    const wrapper = mount(WebhooksPane)
    await flushPromises()

    const row = wrapper.get('[data-webhook-row]')
    expect(row.text()).toContain('review-bot')
    expect(row.text()).toContain('https://hub.example.test/api/git/webhooks/wt1')
    expect(row.text()).toContain('opened, reopened')
  })

  it('shows an empty state without triggers', async () => {
    const wrapper = mount(WebhooksPane)
    await flushPromises()
    expect(wrapper.get('[data-webhook-empty]').text()).toContain('No webhook triggers yet')
  })

  it('creates a trigger and shows URL + secret exactly once', async () => {
    mocks.api.createWebhookTrigger.mockResolvedValue({ trigger, secret: 'whs_secret123' })
    const wrapper = mount(WebhooksPane)
    await flushPromises()

    // The create button stays disabled until a name is entered.
    expect(wrapper.get('[data-webhook-create]').attributes('disabled')).toBeDefined()
    await wrapper.get('[data-webhook-name]').setValue('review-bot')
    await wrapper.get('[data-webhook-repo-filter]').setValue(' my-group/ ')
    expect(wrapper.get('[data-webhook-create]').attributes('disabled')).toBeUndefined()

    await wrapper.get('[data-webhook-create]').trigger('click')
    await flushPromises()

    expect(mocks.api.createWebhookTrigger).toHaveBeenCalledTimes(1)
    const payload = mocks.api.createWebhookTrigger.mock.calls[0][0]
    expect(payload.name).toBe('review-bot')
    expect(payload.events).toEqual(['opened', 'reopened'])
    expect(payload.repoFilter).toBe('my-group/')
    expect(payload.agent).toBe('Claude')
    expect(payload.autoApprove).toBe(false)
    expect(payload.promptTemplate).toContain('{{title}}')

    expect(wrapper.get('[data-webhook-url]').text()).toBe(trigger.url)
    expect(wrapper.get('[data-webhook-secret]').text()).toBe('whs_secret123')
    // The list is reloaded after creating.
    expect(mocks.api.listWebhookTriggers).toHaveBeenCalledTimes(2)
  })

  it('sends the selected events and auto-approve flag', async () => {
    mocks.api.createWebhookTrigger.mockResolvedValue({ trigger, secret: 's' })
    const wrapper = mount(WebhooksPane)
    await flushPromises()

    await wrapper.get('[data-webhook-name]').setValue('t')
    await wrapper.get('[data-webhook-event="reopened"]').setValue(false)
    await wrapper.get('[data-webhook-event="merged"]').setValue(true)
    await wrapper.get('[data-webhook-auto-approve]').setValue(true)
    await wrapper.get('[data-webhook-create]').trigger('click')
    await flushPromises()

    const payload = mocks.api.createWebhookTrigger.mock.calls[0][0]
    expect(payload.events).toEqual(['opened', 'merged'])
    expect(payload.autoApprove).toBe(true)
  })

  it('deletes only after inline confirmation (no popup)', async () => {
    mocks.api.listWebhookTriggers.mockResolvedValue([trigger])
    mocks.api.deleteWebhookTrigger.mockResolvedValue(null)
    const wrapper = mount(WebhooksPane)
    await flushPromises()

    const del = wrapper.get('[data-webhook-delete]')
    await del.trigger('click')
    expect(mocks.api.deleteWebhookTrigger).not.toHaveBeenCalled()
    expect(del.text()).toContain('Really delete?')

    await del.trigger('click')
    await flushPromises()
    expect(mocks.api.deleteWebhookTrigger).toHaveBeenCalledWith('wt1')
  })

  it('shows API errors inline', async () => {
    mocks.api.createWebhookTrigger.mockRejectedValue(new Error('400 Name is required.'))
    const wrapper = mount(WebhooksPane)
    await flushPromises()

    await wrapper.get('[data-webhook-name]').setValue('x')
    await wrapper.get('[data-webhook-create]').trigger('click')
    await flushPromises()

    expect(wrapper.get('[data-webhook-error]').text()).toContain('Name is required')
  })
})
