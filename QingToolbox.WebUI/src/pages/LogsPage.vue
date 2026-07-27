<script setup lang="ts">
import { computed, inject, ref, watch } from 'vue'
import type { LogClient } from '../bridge/clients/LogClient'
import type { LogLevel } from '../contracts/logs'
import { useAppStore } from '../app/store'
import { useLogStore } from '../app/logStore'
import QPage from '../design-system/components/QPage.vue'
import QButton from '../design-system/components/QButton.vue'
import QBadge from '../design-system/components/QBadge.vue'
import QIcon from '../design-system/components/QIcon.vue'
import QSkeleton from '../design-system/components/QSkeleton.vue'

type LogLevelFilter = 'All' | LogLevel

const client = inject<LogClient>('logClient')!
const app = useAppStore()
const logs = useLogStore()
const searchQuery = ref('')
const levelFilter = ref<LogLevelFilter>('All')
const levels: LogLevelFilter[] = ['All', 'Information', 'Warning', 'Error']

const hasSnapshot = computed(() => logs.generatedAt !== null)
const refreshed = computed(() => logs.generatedAt ? new Date(logs.generatedAt).toLocaleTimeString() : null)
const counts = computed(() => ({
  All: logs.entries.length,
  Information: logs.entries.filter(entry => entry.level === 'Information').length,
  Warning: logs.entries.filter(entry => entry.level === 'Warning').length,
  Error: logs.entries.filter(entry => entry.level === 'Error').length
}))
const filteredEntries = computed(() => {
  const query = searchQuery.value.trim().toLocaleLowerCase()
  return logs.entries.filter(entry => {
    const levelMatches = levelFilter.value === 'All' || entry.level === levelFilter.value
    const queryMatches = !query || [entry.category, entry.message, entry.level].some(value => value.toLocaleLowerCase().includes(query))
    return levelMatches && queryMatches
  })
})
const snapshotNotice = computed(() => {
  if (!hasSnapshot.value) return ''
  if (logs.status === 'error') return 'Log refresh failed. Showing the last available session entries.'
  if (app.bridge !== 'Connected') return 'The host is disconnected. Showing the last available session entries.'
  if (logs.status === 'loading') return 'Refreshing session logs…'
  return ''
})

const time = (value: string) => new Date(value).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' })
const tone = (level: LogLevel): 'info'|'warning'|'danger' => level === 'Error' ? 'danger' : level === 'Warning' ? 'warning' : 'info'
async function refresh() { logs.begin(); try { logs.complete(await client.getSnapshot()) } catch (error) { logs.fail(error) } }
function clearFilters() { searchQuery.value = ''; levelFilter.value = 'All' }

watch(() => app.bridge, bridge => {
  if (bridge === 'Connected' && logs.status === 'idle') void refresh()
}, { immediate: true })
</script>

<template>
  <QPage class="logs-page">
    <div class="logs-workspace">
      <header class="wpf-page-header logs-header">
        <div><h1>Session logs</h1><p>Review events recorded during this QingToolbox session.</p><small v-if="refreshed">Last refreshed {{ refreshed }}</small></div>
        <QButton :disabled="app.bridge !== 'Connected' || logs.status === 'loading'" @click="refresh"><QIcon name="refresh" /> {{ logs.status === 'loading' ? 'Refreshing…' : 'Refresh' }}</QButton>
      </header>

      <div v-if="snapshotNotice" class="logs-snapshot-notice" role="status"><QIcon :name="logs.status === 'error' ? 'statusWarning' : 'statusInfo'" /><span>{{ snapshotNotice }}</span><QButton v-if="logs.status === 'error' && app.bridge === 'Connected'" @click="refresh">Retry</QButton></div>

      <section v-if="!hasSnapshot && app.bridge !== 'Connected'" class="q-empty logs-state"><div class="q-empty-icon"><QIcon name="logs" :size="28" /></div><h3>Waiting for the host</h3><p>Session logs will appear after the secure bridge is connected.</p><QBadge tone="warning">{{ app.bridge }}</QBadge></section>
      <section v-else-if="!hasSnapshot && logs.status === 'loading'" class="logs-skeleton" aria-label="Loading session logs"><QSkeleton v-for="item in 6" :key="item" /></section>
      <section v-else-if="!hasSnapshot && logs.status === 'error'" class="q-empty logs-state"><div class="q-empty-icon"><QIcon name="statusDanger" :size="28" /></div><h3>Session logs are unavailable</h3><p>The current session log snapshot could not be read.</p><QButton @click="refresh"><QIcon name="refresh" /> Retry</QButton></section>

      <template v-else-if="hasSnapshot">
        <section class="logs-severity-overview" aria-label="Log severity overview">
          <button v-for="level in levels" :key="level" type="button" :class="`is-${level.toLocaleLowerCase()}`" :aria-pressed="levelFilter === level" @click="levelFilter = level"><span>{{ level }}</span><strong>{{ counts[level] }}</strong></button>
        </section>

        <section class="logs-tools" aria-label="Log filters">
          <label class="logs-search"><span>Search logs</span><div><QIcon name="search" /><input v-model="searchQuery" type="search" aria-label="Search logs" placeholder="Search category, message, or level" /></div></label>
          <label class="logs-level-filter"><span>Severity</span><select v-model="levelFilter" aria-label="Severity filter"><option v-for="level in levels" :key="level" :value="level">{{ level }}</option></select></label>
        </section>

        <p v-if="logs.entries.length" class="logs-results-summary">Showing {{ filteredEntries.length }} of {{ logs.entries.length }} entries</p>
        <section v-if="logs.entries.length === 0" class="q-empty logs-state logs-inline-state"><div class="q-empty-icon"><QIcon name="logs" :size="28" /></div><h3>No session entries yet</h3><p>New in-memory events will appear after a manual refresh.</p></section>
        <section v-else-if="filteredEntries.length === 0" class="q-empty logs-state logs-inline-state"><div class="q-empty-icon"><QIcon name="search" :size="28" /></div><h3>No matching session entries</h3><p>Try changing the search or severity filter.</p><QButton @click="clearFilters">Clear filters</QButton></section>
        <section v-else class="logs-table" aria-label="Session log entries">
          <div class="logs-head"><span>Time</span><span>Level</span><span>Category</span><span>Message</span></div>
          <article v-for="(entry, index) in filteredEntries" :key="`${entry.timestamp}-${index}`" class="logs-row">
            <time :datetime="entry.timestamp">{{ time(entry.timestamp) }}</time>
            <QBadge :tone="tone(entry.level)">{{ entry.level }}</QBadge>
            <strong>{{ entry.category }}</strong>
            <p>{{ entry.message }}</p>
          </article>
        </section>
      </template>
    </div>
  </QPage>
</template>
