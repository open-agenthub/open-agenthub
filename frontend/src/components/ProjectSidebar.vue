<script setup>
import { computed, ref } from 'vue'
import { api } from '../api.js'
import { groupSessions } from '../lib/projects.js'
import MoveSessionDialog from './MoveSessionDialog.vue'
import { reorderSessions } from '../lib/session-order.js'
import SessionList from './SessionList.vue'
import ProjectSecretsDialog from './ProjectSecretsDialog.vue'

const props = defineProps({ projects: Array, sessions: Array, active: String, query: { type: String, default: '' } })
const emit = defineEmits(['new', 'select', 'remove', 'resume', 'pause', 'edit', 'duplicate', 'share', 'projects-changed'])
const collapsed = ref(new Set())
// The project whose secret selection is being edited, or null.
const secretsFor = ref(null)
async function secretsSaved() { secretsFor.value = null; await changed() }
// The sidebar group is a projection without the selection; the dialog needs the project itself.
function openSecrets(group) { secretsFor.value = (props.projects || []).find(project => project.id === group.id) || null }
const groups = computed(() => {
  const result = groupSessions(props.projects, props.sessions, props.query)
  if (!props.query && !result.some(g => g.id === 'ungrouped')) result.push({ id: 'ungrouped', name: 'Ungrouped', sessions: [] })
  return result
})
const organizing = ref(null)
const orderBusy = ref(false)
const orderError = ref('')
function organize(id, target) {
  const session = props.sessions.find(s => s.id === id && (!s.accessRole || s.accessRole === 'Owner'))
  if (session) organizing.value = { session, target }
}
function dragSession(event, id) {
  event.dataTransfer.setData('application/x-agenthub-session', id)
  event.dataTransfer.effectAllowed = 'move'
}
async function saveOrder(id, beforeId) {
  if (orderBusy.value) return
  orderBusy.value = true; orderError.value = ''
  try { await api.setSessionOrder(reorderSessions(props.sessions, id, beforeId)); await changed() }
  catch (e) { orderError.value = String(e.message || e) }
  finally { orderBusy.value = false }
}
function dropSession(event, projectId, beforeId = null) {
  const id = event.dataTransfer.getData('application/x-agenthub-session')
  const session = props.sessions.find(s => s.id === id && (!s.accessRole || s.accessRole === 'Owner'))
  if (!session || projectId === 'shared') return
  const target = projectId === 'ungrouped' ? '' : projectId
  if ((session.projectId || '') !== target) organize(id, target)
  else saveOrder(id, beforeId)
}

function isCollapsed(id) { return collapsed.value.has(id) }
function toggleGroup(id) {
  const next = new Set(collapsed.value)
  next.has(id) ? next.delete(id) : next.add(id)
  collapsed.value = next
}
async function changed() { emit('projects-changed') }
async function createProject() {
  const name = prompt('Project name')?.trim()
  if (!name) return
  await api.createProject({ name, color: null }); await changed()
}
async function rename(project) {
  const name = prompt('Project name', project.name)?.trim()
  if (!name) return
  await api.updateProject(project.id, { name }); await changed()
}
async function recolor(project) {
  const color = prompt('Hex color (leave blank to clear)', project.color || '')
  if (color === null) return
  await api.updateProject(project.id, { color: color.trim() || null }); await changed()
}
async function reorder(project, direction) {
  const ordered = [...(props.projects || [])].sort((a, b) => a.sortOrder - b.sortOrder)
  const index = ordered.findIndex(item => item.id === project.id)
  const other = ordered[index + direction]
  if (!other) return
  await Promise.all([
    api.updateProject(project.id, { sortOrder: other.sortOrder }),
    api.updateProject(other.id, { sortOrder: project.sortOrder })
  ])
  await changed()
}
async function removeProject(project) {
  if (!confirm(`Delete project “${project.name}”? Its sessions will remain ungrouped.`)) return
  await api.deleteProject(project.id); await changed()
}
</script>

<template>
  <div class="project-groups">
    <section v-for="group in groups" :key="group.id" class="project-group">
      <header class="group-head" :data-project-drop="group.id" @dragover.prevent @drop.prevent="dropSession($event, group.id)">
        <button class="group-title" :data-toggle-group="group.id" :aria-expanded="!isCollapsed(group.id)" @click="toggleGroup(group.id)"><span class="chevron">{{ isCollapsed(group.id) ? '▸' : '▾' }}</span><i v-if="group.color" :style="{ background: group.color }"></i>{{ group.name }} <small>{{ group.sessions.length }}</small></button>
        <span v-if="group.id !== 'shared' && group.id !== 'ungrouped'" class="group-controls"><button title="Move up" @click="reorder(group, -1)">↑</button><button title="Move down" @click="reorder(group, 1)">↓</button><button title="Rename" @click="rename(group)">✎</button><button title="Color" @click="recolor(group)">●</button><button title="Secrets" :data-project-secrets-open="group.id" @click="openSecrets(group)">⚿</button><button title="Delete project" @click="removeProject(group)">×</button></span>
      </header>
      <SessionList v-if="!isCollapsed(group.id) && group.sessions.length" :sessions="group.sessions" :active="active" @organize="organize" @drag-session="dragSession" @drop-session="(event, session) => dropSession(event, group.id, session.id)" @select="$emit('select', $event)" @remove="$emit('remove', $event)" @resume="$emit('resume', $event)" @pause="$emit('pause', $event)" @edit="$emit('edit', $event)" @duplicate="$emit('duplicate', $event)" @share="$emit('share', $event)" />
    </section>
    <p v-if="orderError" role="alert">{{ orderError }}</p>
    <MoveSessionDialog v-if="organizing" :session="organizing.session" :target="organizing.target" :projects="projects" :sessions="sessions" @close="organizing = null" @changed="changed" @reorder="saveOrder" />
    <p v-if="!groups.length" class="none">No matching sessions.</p>
    <button class="new-project" @click="createProject">+ New project</button>
    <ProjectSecretsDialog v-if="secretsFor" :project="secretsFor" @close="secretsFor = null" @saved="secretsSaved" />
  </div>
</template>

<style scoped>
.project-groups { padding-bottom: 8px; }
.project-group { margin: 0 0 6px; }
.group-head { display: flex; align-items: center; justify-content: space-between; padding: 4px 8px 2px 4px; color: var(--muted-3); font-size: 12px; font-weight: 600; }
.group-title { display: flex; align-items: center; gap: 7px; padding: 2px 4px; border: 0; background: none; color: inherit; font-size: inherit; font-weight: inherit; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.group-title:hover { border: 0; background: none; color: var(--muted); }
.chevron { width: 10px; }
.group-title i { width: 9px; height: 9px; border-radius: 50%; flex-shrink: 0; }
small { font-family: var(--mono); font-weight: 400; color: var(--faint); }
.group-controls { display: flex; opacity: 0; transition: opacity .12s; }
.group-head:hover .group-controls { opacity: .8; }
.group-controls button { border: 0; background: none; padding: 1px 3px; font-size: 12px; color: var(--muted-3); font-weight: 400; }
.group-controls button:hover { color: var(--accent); border: 0; background: none; }
.none { color: var(--muted-3); font-size: 13px; text-align: center; padding: 16px; }
.new-project { display: block; border: 0; background: none; color: var(--faint); font-size: 12px; font-weight: 600; padding: 6px 8px; }
.new-project:hover { color: var(--muted); background: none; }
</style>
