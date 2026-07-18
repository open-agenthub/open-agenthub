<script setup>
import { computed, ref } from 'vue'
import { api } from '../api.js'
import { groupSessions } from '../lib/projects.js'
import SessionList from './SessionList.vue'

const props = defineProps({ projects: Array, sessions: Array, active: String, query: { type: String, default: '' } })
const emit = defineEmits(['new', 'select', 'remove', 'resume', 'pause', 'edit', 'duplicate', 'share', 'projects-changed'])
const collapsed = ref(new Set())
const groups = computed(() => groupSessions(props.projects, props.sessions, props.query))

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
      <header class="group-head">
        <button class="group-title" :data-toggle-group="group.id" :aria-expanded="!isCollapsed(group.id)" @click="toggleGroup(group.id)"><span class="chevron">{{ isCollapsed(group.id) ? '▸' : '▾' }}</span><i v-if="group.color" :style="{ background: group.color }"></i>{{ group.name }} <small>{{ group.sessions.length }}</small></button>
        <span v-if="group.id !== 'shared' && group.id !== 'ungrouped'" class="group-controls"><button title="Move up" @click="reorder(group, -1)">↑</button><button title="Move down" @click="reorder(group, 1)">↓</button><button title="Rename" @click="rename(group)">✎</button><button title="Color" @click="recolor(group)">●</button><button title="Delete project" @click="removeProject(group)">×</button></span>
      </header>
      <SessionList v-if="!isCollapsed(group.id)" :sessions="group.sessions" :active="active" @select="$emit('select', $event)" @remove="$emit('remove', $event)" @resume="$emit('resume', $event)" @pause="$emit('pause', $event)" @edit="$emit('edit', $event)" @duplicate="$emit('duplicate', $event)" @share="$emit('share', $event)" />
    </section>
    <p v-if="!groups.length" class="none">No matching sessions.</p>
    <button class="new-project" @click="createProject">+ New project</button>
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
