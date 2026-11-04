<script setup>
// Who can open the sessions of a project (#409). A share here reaches every session in the
// project, the ones started later included, and their child sessions; a share on a single session
// can only add to it. docs/session-sharing.md records the precedence.
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { api } from '../api.js'
import { initials } from '../lib/text.js'
import { isLicenseError } from '../lib/license.js'
import LicenseGate from './LicenseGate.vue'

const props = defineProps({ project: { type: Object, required: true } })
defineEmits(['close'])

const shares = ref([])
const loaded = ref(false)
const locked = ref(false)
const error = ref('')
const kind = ref('user')
const subject = ref('')
const role = ref('Viewer')
const users = ref([])
const groups = ref([])
const searched = ref(false)
let generation = 0
let timer

const people = computed(() => shares.value.filter(share => share.subjectType === 'user'))
const teams = computed(() => shares.value.filter(share => share.subjectType === 'group'))

async function load() {
  try { shares.value = (await api.listProjectShares(props.project.id)) || [] }
  catch (e) {
    if (isLicenseError(e)) locked.value = true
    else error.value = String(e.message || e)
  } finally { loaded.value = true }
}

// The owner's own groups are the only ones a project can go to, so they are offered up front;
// users are looked up as they are typed, with the same rules as the session dialog.
async function search(query) {
  const current = ++generation
  try {
    const found = await api.findProjectShareRecipients(props.project.id, query)
    if (current !== generation) return
    users.value = found?.users || []
    groups.value = found?.groups || []
    searched.value = !!query
  } catch (e) {
    if (current === generation && !isLicenseError(e)) error.value = String(e.message || e)
  }
}
watch(subject, value => {
  clearTimeout(timer)
  searched.value = false
  timer = setTimeout(() => search(value.trim()), 250)
})

async function change(action) {
  error.value = ''
  try { await action(); await load() }
  catch (e) {
    if (isLicenseError(e)) locked.value = true
    else error.value = String(e.message || e)
  }
}
async function add() {
  const name = subject.value.trim()
  if (!name) return
  await change(async () => {
    await api.upsertProjectShare(props.project.id, { subjectType: kind.value, subject: name, role: role.value })
    subject.value = ''
  })
}
function choose(type, name) { kind.value = type; subject.value = name }
async function setRole(share, value) {
  if (value === 'Remove') await change(() => api.deleteProjectShare(props.project.id, share.subjectType, share.subject))
  else await change(() => api.upsertProjectShare(props.project.id, { subjectType: share.subjectType, subject: share.subject, role: value }))
}

onMounted(async () => { await load(); if (!locked.value) search('') })
onUnmounted(() => { ++generation; clearTimeout(timer) })
</script>

<template>
  <div class="overlay" data-project-share @click.self="$emit('close')">
    <div class="modal">
      <h3>Share “{{ project.name }}”</h3>
      <LicenseGate v-if="locked" feature="Sharing projects with other users" />
      <template v-else>
        <p class="note">Everyone listed here opens every session in this project with the role shown —
          sessions started later and their child sessions included. A share on a single session can give
          someone more, never less. Removing a person here closes the project's sessions to them at once.</p>
        <p class="note">Anyone with terminal input can read whatever a session holds, including its secrets.</p>

        <div class="add-row">
          <select v-model="kind" class="fit" aria-label="Share with" data-project-share-kind><option value="user">User</option><option value="group">Group</option></select>
          <input v-model="subject" :aria-label="kind === 'user' ? 'Username or email' : 'Group name'"
            :placeholder="kind === 'user' ? 'Name, username, or exact email…' : 'One of your groups…'" autocomplete="off"
            data-project-share-subject @keyup.enter="add" />
          <select v-model="role" class="fit" data-project-share-role><option>Viewer</option><option>Collaborator</option></select>
          <button data-project-share-add @click="add">Add</button>
        </div>
        <ul v-if="kind === 'user' && users.length" class="options" aria-label="Matching users">
          <li v-for="person in users" :key="person.username"><button type="button" @click="choose('user', person.username)">{{ person.displayName || person.username }} · Username: {{ person.username }}</button></li>
        </ul>
        <p v-else-if="kind === 'user' && searched" class="sub" role="status">No matching user. The person must have signed in here before.</p>
        <ul v-if="kind === 'group' && groups.length" class="options" aria-label="Your groups">
          <li v-for="group in groups" :key="group"><button type="button" @click="choose('group', group)">{{ group }}</button></li>
        </ul>
        <p v-else-if="kind === 'group' && loaded" class="sub" role="status">You can share with the groups you belong to; none match.</p>

        <section class="bordered">
          <div class="sect-title">Who has access</div>
          <p v-if="loaded && !shares.length" class="sub" data-project-share-empty>Only you.</p>
          <div v-for="share in [...teams, ...people]" :key="share.subjectType + ':' + share.subject" class="person"
            :data-project-share-entry="share.subjectType + ':' + share.subject">
            <span class="avatar">{{ share.subjectType === 'group' ? '#' : initials(share.subject) }}</span>
            <span class="pname">{{ share.subject }}<small v-if="share.subjectType === 'group'"> · group</small></span>
            <select class="fit" :value="share.role" :data-project-share-entry-role="share.subjectType + ':' + share.subject" @change="setRole(share, $event.target.value)"><option>Viewer</option><option>Collaborator</option><option>Remove</option></select>
          </div>
        </section>
      </template>
      <p v-if="error" class="err" role="alert" data-project-share-error>{{ error }}</p>
      <div class="row"><button @click="$emit('close')">Close</button></div>
    </div>
  </div>
</template>

<style scoped>
.overlay { position: fixed; inset: 0; z-index: 50; display: flex; align-items: flex-start; justify-content: center; overflow-y: auto; padding: 24px; background: rgba(10,9,8,.7); }
.modal { width: 560px; max-width: 100%; padding: 22px; border: 1px solid var(--border-2); border-radius: var(--radius-lg); background: var(--panel); }
h3 { margin: 0 0 6px; font-size: 18px; }
.note, .sub { margin: 0 0 10px; color: var(--muted-2); font-size: 12px; line-height: 1.5; }
.sect-title { font-weight: 600; font-size: 13px; color: var(--text); margin-bottom: 4px; }
section.bordered { border-top: 1px solid var(--border); margin-top: 14px; padding-top: 14px; }
.add-row { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; margin: 8px 0 10px; }
.add-row input { flex: 1 1 160px; min-width: 0; }
.fit { width: auto; }
.options { list-style: none; padding: 0; margin: 6px 0; }
.person { display: flex; gap: 10px; align-items: center; padding: 6px 0; font-size: 13px; }
.avatar { width: 26px; height: 26px; border-radius: 50%; background: #3d3a33; display: inline-flex; align-items: center; justify-content: center; font-size: 10px; color: #d6d1c8; font-weight: 700; flex-shrink: 0; }
.pname { flex: 1; min-width: 0; font-weight: 600; overflow: hidden; text-overflow: ellipsis; }
.pname small { color: var(--faint); font-weight: 400; }
.err { color: var(--danger); font: 12px var(--mono); }
.row { display: flex; justify-content: flex-end; margin-top: 16px; }
</style>
