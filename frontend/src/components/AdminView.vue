<script setup>
import { ref, computed, onMounted } from 'vue'
import { api, auth } from '../api.js'
import { licenseBadge, licenseBadgeLabel, seatOverbooked } from '../lib/license.js'

defineEmits(['close'])
const props = defineProps({
  embedded: { type: Boolean, default: false },
  // 'all' (standalone admin page) or one of 'license' | 'seats' | 'billing'
  // when a settings tab renders just one slice.
  section: { type: String, default: 'all' }
})
const showLicense = computed(() => ['all', 'license'].includes(props.section))
const showSeats = computed(() => ['all', 'seats'].includes(props.section))
const showBilling = computed(() => ['all', 'billing'].includes(props.section))

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

// --- "Get enterprise license" → Stripe checkout on the license service ---
const checkoutOpen = ref(false)
const checkoutBusy = ref(false)
const checkout = ref({ email: '', org: '', seats: 1 })

function openCheckout() {
  checkout.value = {
    email: auth.email || '',
    org: checkout.value.org || '',
    seats: Math.max(1, data.value?.users?.length || 1)
  }
  checkoutOpen.value = true
}

async function startCheckout() {
  checkoutBusy.value = true; error.value = ''
  try {
    const res = await api.startLicenseCheckout({
      email: checkout.value.email.trim(),
      org: checkout.value.org.trim(),
      seats: Number(checkout.value.seats) || 1,
      returnUrl: `${location.origin}/license/activate`
    })
    if (res?.url) location.href = res.url
    else error.value = 'The license service did not return a checkout URL.'
  } catch (e) { error.value = String(e.message || e) }
  finally { checkoutBusy.value = false }
}

function fmtDate(d) {
  if (!d) return '—'
  try { return new Date(d).toLocaleDateString() } catch { return d }
}
function fmtDateTime(d) {
  if (!d) return '—'
  try { return new Date(d).toLocaleString() } catch { return d }
}
</script>

<template>
  <div class="admin-page">
    <div v-if="!embedded" class="bar">
      <button class="back" @click="$emit('close')">‹ Back</button>
      <span class="title">Admin</span>
    </div>

    <div class="embed">
      <div class="embed-inner">
        <h3 v-if="embedded && section === 'license'" class="pane-head">License</h3>
        <h3 v-else-if="embedded && section === 'seats'" class="pane-head">Users &amp; seats</h3>
        <h3 v-else-if="embedded && section === 'billing'" class="pane-head">Billing &amp; invoices</h3>
        <p v-if="error" class="err">{{ error }}</p>
        <p v-if="loading" class="muted">Loading…</p>

        <template v-else>
          <!-- No active license: one clear path to get one (shown on every admin tab) -->
          <section v-if="!lic.valid" class="card cta">
            <div class="card-head">
              <h4>Enterprise features are locked</h4>
              <span class="badge off">unlicensed</span>
            </div>
            <p class="note">
              The core stays free and fully usable. An enterprise license unlocks session
              sharing, Slack notifications and more — new customers get a 3-month free trial.
            </p>
            <div v-if="!checkoutOpen" class="row start">
              <button class="primary" data-get-license @click="openCheckout">Get enterprise license</button>
            </div>
            <div v-else class="checkout-form">
              <div class="cgrid">
                <div class="field"><label>Billing email</label><input v-model="checkout.email" type="email" placeholder="billing@company.com" /></div>
                <div class="field"><label>Organization</label><input v-model="checkout.org" placeholder="ACME GmbH" /></div>
                <div class="field seats-field"><label>Seats</label><input v-model.number="checkout.seats" type="number" min="1" /></div>
              </div>
              <div class="row start">
                <button class="primary" data-start-checkout :disabled="checkoutBusy || !checkout.email.trim() || !checkout.org.trim()" @click="startCheckout">
                  {{ checkoutBusy ? 'Redirecting…' : 'Continue to checkout →' }}
                </button>
                <button @click="checkoutOpen = false">Cancel</button>
                <span class="muted">You will be redirected to Stripe and back here afterwards — the license activates automatically.</span>
              </div>
            </div>
          </section>

          <!-- License -->
          <section v-if="showLicense" class="card">
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
          <section v-if="showSeats" class="card">
            <div class="card-head">
              <h4>Seats &amp; users</h4>
              <span class="badge" :class="overBooked ? 'bad' : 'ok'">
                {{ seats.used }}<template v-if="seats.included"> / {{ seats.included }}</template> in use
              </span>
            </div>
            <p v-if="lic.valid" class="note">
              Every user who signs in gets a seat automatically. Revoke a seat to free it up.
              The seat count is reported to the license service as a monthly heartbeat, which
              also renews the license token; the count at month close is what gets billed.
              <br />Last check-in: <b>{{ data.lastCheckIn ? fmtDateTime(data.lastCheckIn) : 'never' }}</b>
              <span v-if="overBooked" class="reason">You are over your licensed seat count — reduce seats or upgrade your plan.</span>
            </p>
            <p v-else class="note">
              Everyone who signs in appears here. Without an active license there are no
              enterprise seats — get a license above and every user becomes licensed automatically.
            </p>

            <p v-if="!data.users.length" class="muted">No users have signed in yet.</p>
            <div v-else class="users">
              <div v-for="u in data.users" :key="u.owner" class="urow">
                <div class="uinfo">
                  <span class="uname">{{ u.displayName || u.owner }}</span>
                  <span class="umeta">{{ u.email || u.owner }} · seen {{ fmtDate(u.updatedAt) }}</span>
                </div>
                <label v-if="lic.valid" class="seat">
                  <input type="checkbox" :checked="u.licensed" @change="toggleSeat(u)" />
                  <span>{{ u.licensed ? 'licensed' : 'no seat' }}</span>
                </label>
                <span v-else class="badge off" data-user-unlicensed>unlicensed</span>
              </div>
            </div>
          </section>

          <!-- Billing -->
          <section v-if="showBilling" class="card">
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
.pane-head { font-size: 22px; margin: 0 0 16px; }
.cta { border-color: #4a3e1e; background: #1f1b12; }
.checkout-form { margin-top: 4px; }
.cgrid { display: grid; grid-template-columns: 1.4fr 1.4fr 0.6fr; gap: 12px; }
.seats-field input { font-family: var(--mono); }
.row.start { justify-content: flex-start; }
@media (max-width: 620px) { .cgrid { grid-template-columns: 1fr; } }
.card { background: var(--panel); border: 1px solid var(--border-2); border-radius: var(--radius-lg); padding: 18px 20px; margin-bottom: 16px; }
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
