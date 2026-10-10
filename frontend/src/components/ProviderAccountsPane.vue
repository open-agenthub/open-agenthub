<script setup>
import { onMounted, ref } from 'vue'
import { api } from '../api.js'
import { accountLimitLabel, accountsFor, agentOptions, isAccountExhausted } from '../lib/agent.js'

/**
 * The stored provider logins, as accounts: one list per agent with label, identity, default
 * marker and the three things a person can do to a login the product never lets them type in —
 * rename it, make it the default for new sessions, forget it.
 *
 * `status` is the plain credential-status booleans. They are the fallback when the accounts
 * listing is unavailable (an older backend, a failed request): a stored login is then still
 * shown as stored and can still be removed, which is what the dialog offered before accounts.
 */
const props = defineProps({ status: { type: Object, default: () => ({}) } })
const emit = defineEmits(['changed'])

const providers = agentOptions.map(option => option.value)
const statusKey = { Claude: 'claudeSubscription', Codex: 'codexSubscription', Cursor: 'cursorSubscription', OpenClaw: 'openclawSubscription' }

const accounts = ref({})
const listed = ref(false)
const error = ref('')
// Which row is mid-action, so only that row shows progress.
const busy = ref('')
const editing = ref(null)
const draftLabel = ref('')

async function load() {
  try {
    accounts.value = (await api.listProviderAccounts()) || {}
    listed.value = true
  } catch {
    accounts.value = {}
    listed.value = false
  }
}
onMounted(load)

function identity(account) {
  return [account.email, account.organization].filter(Boolean).join(' · ')
}

function startRename(agent, account) {
  editing.value = `${agent}/${account.id}`
  draftLabel.value = account.label
}

async function run(key, action) {
  if (busy.value) return
  busy.value = key
  error.value = ''
  try {
    await action()
    await load()
    emit('changed')
  } catch (e) {
    error.value = e?.message || 'The change could not be saved.'
  } finally {
    busy.value = ''
  }
}

function saveLabel(agent, account) {
  const label = draftLabel.value.trim()
  if (!label || label === account.label) { editing.value = null; return }
  run(`${agent}/${account.id}`, async () => {
    await api.updateProviderAccount(agent, account.id, { label })
    editing.value = null
  })
}

const makeDefault = (agent, account) =>
  run(`${agent}/${account.id}`, () => api.updateProviderAccount(agent, account.id, { isDefault: true }))

const remove = (agent, account) =>
  run(`${agent}/${account.id}`, () => api.deleteProviderAccount(agent, account.id))

// The way out of a wrong limit detection (docs/account-limits.md): the mark is lifted and the
// account goes back into the rotation for new sessions and failovers at once.
const clearExhausted = (agent, account) =>
  run(`${agent}/${account.id}`, () => api.updateProviderAccount(agent, account.id, { clearExhausted: true }))

/**
 * Removes the stored login outright when the listing is not available. Like the per-account
 * removal it is immediate, not staged for save: a credential the user has decided is broken
 * should not survive them closing the dialog without saving.
 */
const removeAll = agent => run(agent, () => api.deleteSubscriptionCredential(agent))
</script>

<template>
  <div class="accounts" data-provider-accounts>
    <div class="head">
      <label>Provider logins</label>
      <small>Sign-in happens inside an Interactive session; every login is kept as an account you can pick per session.</small>
    </div>
    <section v-for="agent in providers" :key="agent" class="provider" :data-provider-accounts-for="agent">
      <div class="provider-name">{{ agent }}</div>
      <template v-if="accountsFor(accounts, agent).length">
        <small :data-credential-status="statusKey[agent]">
          {{ accountsFor(accounts, agent).length === 1 ? 'One' : accountsFor(accounts, agent).length }} {{ agent }} subscription
          {{ accountsFor(accounts, agent).length === 1 ? 'login is' : 'logins are' }} stored.
        </small>
        <ul class="list">
          <li v-for="account in accountsFor(accounts, agent)" :key="account.id" class="row" :data-provider-account="account.id">
            <div class="who">
              <template v-if="editing === `${agent}/${account.id}`">
                <input v-model="draftLabel" data-account-label-input maxlength="80" aria-label="Account label"
                  @keydown.enter.prevent="saveLabel(agent, account)" @keydown.esc.prevent="editing = null" />
              </template>
              <template v-else>
                <span class="label" data-account-label>{{ account.label }}</span>
                <span v-if="account.isDefault" class="badge" data-account-default>default</span>
                <span v-if="isAccountExhausted(account)" class="badge limit" data-account-exhausted
                  :title="account.exhaustedReason || ''">{{ accountLimitLabel(account) }}</span>
              </template>
              <span v-if="identity(account)" class="identity" data-account-identity>{{ identity(account) }}</span>
            </div>
            <div class="actions">
              <template v-if="editing === `${agent}/${account.id}`">
                <button type="button" class="chip" data-account-save-label :disabled="busy === `${agent}/${account.id}`"
                  @click="saveLabel(agent, account)">save</button>
                <button type="button" class="chip" data-account-cancel-label @click="editing = null">cancel</button>
              </template>
              <template v-else>
                <button v-if="isAccountExhausted(account)" type="button" class="chip" data-account-clear-exhausted
                  :disabled="busy === `${agent}/${account.id}`" @click="clearExhausted(agent, account)">clear limit</button>
                <button type="button" class="chip" data-account-rename @click="startRename(agent, account)">rename</button>
                <button v-if="!account.isDefault" type="button" class="chip" data-account-make-default
                  :disabled="busy === `${agent}/${account.id}`" @click="makeDefault(agent, account)">make default</button>
                <button type="button" class="chip del" data-account-remove :disabled="busy === `${agent}/${account.id}`"
                  :aria-label="`Remove stored ${agent} login ${account.label}`"
                  @click="remove(agent, account)">{{ busy === `${agent}/${account.id}` ? 'removing…' : 'remove ✕' }}</button>
              </template>
            </div>
          </li>
        </ul>
      </template>
      <small v-else-if="!listed && status[statusKey[agent]]" :data-credential-status="statusKey[agent]">
        {{ agent }} subscription login is stored (sign-in happens in an Interactive session).
        <button type="button" class="chip del" :data-remove-subscription="agent"
          :disabled="busy === agent" :aria-label="`Remove stored ${agent} subscription login`"
          @click="removeAll(agent)">{{ busy === agent ? 'removing…' : 'remove ✕' }}</button>
      </small>
      <small v-else :data-credential-status="statusKey[agent]">
        No {{ agent }} subscription login is stored yet. Sign in during an Interactive {{ agent }} session.
      </small>
    </section>
    <p v-if="error" class="err" data-provider-accounts-error>{{ error }}</p>
  </div>
</template>

<style scoped>
.accounts { margin-bottom: 14px; }
.head small { display: block; margin: 5px 0 10px; color: var(--muted-3); font-size: 11px; line-height: 1.4; }
.provider { padding: 10px 0; border-top: 1px solid var(--border); }
.provider-name { font-size: 13px; font-weight: 700; color: var(--text); }
.provider > small { display: block; margin-top: 4px; color: var(--muted-3); font-size: 11px; line-height: 1.4; }
.list { list-style: none; margin: 8px 0 0; padding: 0; display: flex; flex-direction: column; gap: 6px; }
.row { display: flex; align-items: center; gap: 10px; padding: 8px 10px; border: 1px solid var(--border-2); border-radius: var(--radius); background: var(--input); }
.who { flex: 1; min-width: 0; display: flex; flex-wrap: wrap; align-items: baseline; gap: 6px 10px; }
.who input { width: 220px; max-width: 100%; }
.label { font-size: 13px; font-weight: 700; color: var(--strong); }
.badge { font-family: var(--mono); font-size: 10px; color: var(--ok); border: 1px solid var(--border); border-radius: 999px; padding: 1px 8px; }
.badge.limit { color: var(--warn); border-color: var(--warn); }
.identity { font-family: var(--mono); font-size: 11px; color: var(--muted-3); overflow: hidden; text-overflow: ellipsis; }
.actions { display: flex; gap: 6px; flex-shrink: 0; }
.chip {
  width: auto; font-family: var(--mono); font-size: 10px; cursor: pointer; background: none;
  color: var(--muted); border: 1px solid var(--border); border-radius: 999px; padding: 1px 8px;
}
.chip:hover { border-color: var(--accent); color: var(--accent); }
.chip.del { color: var(--danger); border-color: var(--border); }
.chip.del:hover { border-color: var(--danger); }
.provider > small .chip { margin-left: 8px; }
.err { color: var(--danger); font-family: var(--mono); font-size: 12px; }
@media (max-width: 600px) {
  .row { flex-wrap: wrap; }
}
</style>
