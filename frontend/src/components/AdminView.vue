<script setup>
import { ref, computed, onMounted } from 'vue'
import { api } from '../api.js'
import { licenseBadge, licenseBadgeLabel, seatOverbooked } from '../lib/license.js'

defineEmits(['close'])

const data = ref(null)
const loading = ref(true)
const error = ref('')

const token = ref('')
const activating = ref(false)
const activateMsg = ref('')

const lic = computed(() => data.value?.license || {})
const seats = computed(() => data.value?.seats || { used: 0, included: 0 })
const badge = computed(() => licenseBadge(lic.value))
const badgeLabel = computed(() => licenseBadgeLabel(lic.value))
const overBooked = computed(() => seatOverbooked(seats.value))

async function load() {
  loading.value = true; error.value = ''
  try { data.value = await api.adminOverview() }
  catch (e) { error.value = String(e.message || e) }
  finally { loading.value = false }
}
onMounted(load)

async function activate() {
  const t = token.value.trim()
  if (!t) return
  activating.value = true; activateMsg.value = ''; error.value = ''
  try {
    await api.activateLicense(t)
    token.value = ''
    activateMsg.value = 'License activated ✓'
    await load()
  } catch (e) { error.value = String(e.message || e) }
  finally { activating.value = false }
}

async function deactivate() {
  if (!confirm('Remove the activated license? Enterprise features (e.g. Slack) will stop working.')) return
  error.value = ''
  try { await api.deactivateLicense(); activateMsg.value = ''; await load() }
  catch (e) { error.value = String(e.message || e) }
}

async function toggleSeat(u) {
  error.value = ''
  try { await api.setUserSeat(u.owner, !u.licensed); await load() }
  catch (e) { error.value = String(e.message || e) }
}

function fmtDate(d) {
  if (!d) return '—'
  try { return new Date(d).toLocaleDateString() } catch { return d }
}
</script>

<template>
  <div class="admin-page">
    <div class="bar">
      <button class="back" @click="$emit('close')">‹ Back</button>
      <span class="title">Admin</span>
    </div>

    <div class="embed">
      <div class="embed-inner">
        <p v-if="error" class="err">{{ error }}</p>
        <p v-if="loading" class="muted">Loading…</p>

        <template v-else>
          <!-- License -->
          <section class="card">
            <div class="card-head">
              <h4>Enterprise license</h4>
              <span class="badge" :class="{ ok: badge === 'active', bad: badge === 'invalid', off: badge === 'off' }">
                {{ badgeLabel }}
              </span>
            </div>
            <p class="note">
              The license is verified offline and stored in the database — not in the Helm chart.
              Paste the token you received to unlock enterprise features (Slack notifications, permission buttons).
            </p>

            <div v-if="lic.valid" class="kv">
              <div><span>Plan</span><b>{{ lic.plan || '—' }}</b></div>
              <div><span>Organization</span><b>{{ lic.org || '—' }}</b></div>
              <div><span>Seats included</span><b>{{ lic.seats || '—' }}</b></div>
              <div><span>Valid until</span><b>{{ fmtDate(lic.validUntil) }}</b></div>
            </div>
            <p v-else-if="lic.reason" class="reason">{{ lic.reason }}</p>

            <div class="field">
              <label>License token</label>
              <textarea v-model="token" placeholder="eyJ… (paste the license token)"></textarea>
            </div>
            <div class="row">
              <button v-if="lic.present" class="danger" @click="deactivate">Remove license</button>
              <span v-if="activateMsg" class="ok-text">{{ activateMsg }}</span>
              <button class="primary" :disabled="activating || !token.trim()" @click="activate">
                {{ activating ? 'Activating…' : 'Activate' }}
              </button>
            </div>
          </section>

          <!-- Seats / users -->
          <section class="card">
            <div class="card-head">
              <h4>Seats &amp; users</h4>
              <span class="badge" :class="overBooked ? 'bad' : 'ok'">
                {{ seats.used }}<template v-if="seats.included"> / {{ seats.included }}</template> in use
              </span>
            </div>
            <p class="note">
              Every user who signs in gets a seat automatically. Revoke a seat to free it up.
              <span v-if="overBooked" class="reason">You are over your licensed seat count — reduce seats or upgrade your plan.</span>
            </p>

            <p v-if="!data.users.length" class="muted">No users have signed in yet.</p>
            <div v-else class="users">
              <div v-for="u in data.users" :key="u.owner" class="urow">
                <div class="uinfo">
                  <span class="uname">{{ u.displayName || u.owner }}</span>
                  <span class="umeta">{{ u.email || u.owner }} · seen {{ fmtDate(u.updatedAt) }}</span>
                </div>
                <label class="seat">
                  <input type="checkbox" :checked="u.licensed" @change="toggleSeat(u)" />
                  <span>{{ u.licensed ? 'licensed' : 'no seat' }}</span>
                </label>
              </div>
            </div>
          </section>

          <!-- Billing -->
          <section class="card">
            <div class="card-head"><h4>Billing</h4></div>
            <p class="note">
              Subscription, invoices and cancellation are managed through the billing portal.
              Statutory invoices can be downloaded there.
            </p>
            <div class="row">
              <a v-if="data.billingPortalUrl" class="btn-link" :href="data.billingPortalUrl" target="_blank" rel="noopener">Open billing portal ↗</a>
              <span v-else class="muted">No billing portal configured on this instance.</span>
            </div>
          </section>
        </template>
      </div>
    </div>
  </div>
</template>

<style scoped>
.admin-page { flex: 1; display: flex; flex-direction: column; min-width: 0; min-height: 0; background: var(--bg); }
.bar { display: flex; align-items: center; gap: 12px; padding: 8px 14px; border-bottom: 1px solid var(--border); background: var(--panel); }
.bar .title { font-size: 13px; font-weight: 600; }
.back { background: none; border: none; color: var(--muted); }
.back:hover { color: var(--accent); border: none; }
.card { background: var(--panel); border: 1px solid var(--border); border-radius: 14px; padding: 18px; margin-bottom: 16px; }
.card-head { display: flex; align-items: center; justify-content: space-between; margin-bottom: 6px; }
.card-head h4 { margin: 0; font-size: 15px; }
.badge { font-size: 11px; font-family: var(--mono); padding: 2px 9px; border-radius: 999px; border: 1px solid var(--border); color: var(--muted); }
.badge.ok { color: var(--ok); border-color: var(--ok); }
.badge.bad { color: var(--danger); border-color: var(--danger); }
.badge.off { color: var(--muted); }
.note { color: var(--muted); font-size: 12px; line-height: 1.5; margin: 0 0 14px; }
.reason { color: var(--danger); font-size: 12px; }
.kv { display: grid; grid-template-columns: 1fr 1fr; gap: 10px; margin-bottom: 14px; }
.kv > div { display: flex; flex-direction: column; gap: 2px; background: var(--panel-2); border: 1px solid var(--border); border-radius: 10px; padding: 10px 12px; }
.kv span { color: var(--muted); font-size: 11px; text-transform: uppercase; letter-spacing: .04em; }
.kv b { font-family: var(--mono); font-size: 14px; }
.field { display: flex; flex-direction: column; gap: 4px; margin-bottom: 12px; }
.field label { font-size: 12px; color: var(--muted); }
.field textarea { min-height: 90px; font-family: var(--mono); font-size: 12px; }
.row { display: flex; align-items: center; justify-content: flex-end; gap: 12px; }
.ok-text { color: var(--ok); font-size: 12px; margin-right: auto; }
.users { display: flex; flex-direction: column; gap: 8px; }
.urow { display: flex; align-items: center; gap: 12px; border: 1px solid var(--border); border-radius: 10px; padding: 10px 12px; }
.uinfo { flex: 1; min-width: 0; display: flex; flex-direction: column; gap: 2px; }
.uname { font-weight: 600; }
.umeta { color: var(--muted); font-size: 11px; font-family: var(--mono); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.seat { display: flex; align-items: center; gap: 7px; font-size: 12px; color: var(--muted); flex-shrink: 0; }
.seat input { width: auto; }
.btn-link { font-size: 13px; color: var(--accent); text-decoration: none; border: 1px solid var(--border); border-radius: var(--radius); padding: 7px 12px; }
.btn-link:hover { border-color: var(--accent); }
.muted { color: var(--muted); font-size: 12px; }
.err { color: var(--danger); font-family: var(--mono); font-size: 12px; }
@media (max-width: 620px) { .kv { grid-template-columns: 1fr; } }
</style>
