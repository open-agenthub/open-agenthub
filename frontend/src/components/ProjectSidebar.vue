<script setup>
import { computed, ref } from 'vue'
import { api } from '../api.js'
import { groupSessions } from '../lib/projects.js'
import SessionList from './SessionList.vue'

const props = defineProps({ projects: Array, sessions: Array, active: String })
const emit = defineEmits(['new', 'select', 'remove', 'resume', 'pause', 'edit', 'duplicate', 'share', 'projects-changed'])
const query = ref('')
const groups = computed(() => groupSessions(props.projects, props.sessions, query.value))

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
  <div class="sidebar-head"><h2>Sessions</h2><button class="primary" @click="$emit('new')">New Session</button></div>
  <div class="project-actions"><input v-model="query" class="session-search" placeholder="Search sessions…" /><button title="Create project" @click="createProject">+ Project</button></div>
  <div class="project-groups">
    <section v-for="group in groups" :key="group.id" class="project-group">
      <header class="group-head"><span class="group-title"><i v-if="group.color" :style="{ background: group.color }"></i>{{ group.name }} <small>{{ group.sessions.length }}</small></span>
        <span v-if="group.id !== 'shared' && group.id !== 'ungrouped'" class="group-controls"><button @click="reorder(group, -1)">↑</button><button @click="reorder(group, 1)">↓</button><button @click="rename(group)">✎</button><button @click="recolor(group)">●</button><button @click="removeProject(group)">×</button></span>
      </header>
      <SessionList :sessions="group.sessions" :active="active" @select="$emit('select', $event)" @remove="$emit('remove', $event)" @resume="$emit('resume', $event)" @pause="$emit('pause', $event)" @edit="$emit('edit', $event)" @duplicate="$emit('duplicate', $event)" @share="$emit('share', $event)" />
    </section>
    <p v-if="!groups.length" class="none">No matching sessions.</p>
  </div>
</template>

<style scoped>
.project-actions { display: flex; gap: 8px; padding: 0 16px 10px; } .session-search { margin: 0; flex: 1; } .project-actions button { padding: 6px 8px; font-size: 12px; white-space: nowrap; }
.project-groups { overflow-y: auto; padding-bottom: 16px; } .project-group { margin: 0 0 8px; } .group-head { display: flex; align-items: center; justify-content: space-between; padding: 7px 16px 3px; color: var(--muted); font-size: 12px; font-weight: 600; } .group-title { display: flex; align-items: center; gap: 6px; } .group-title i { width: 9px; height: 9px; border-radius: 50%; } small { font-family: var(--mono); font-weight: 400; } .group-controls { display: flex; opacity: .55; } .group-controls button { border: 0; background: none; padding: 1px 3px; font-size: 12px; color: var(--muted); } .group-controls button:hover { color: var(--accent); border: 0; }
.none { color: var(--muted); font-size: 13px; text-align: center; padding: 16px; }
</style>
