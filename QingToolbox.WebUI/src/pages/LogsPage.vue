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
import { useLocalization } from '../localization/localization'
import { bridgeStateKey, logLevelKey } from '../presentation/workspacePresentation'

type LogLevelFilter = 'All' | LogLevel

const client = inject<LogClient>('logClient')!
const app = useAppStore()
const logs = useLogStore()
const searchQuery = ref('')
const levelFilter = ref<LogLevelFilter>('All')
const levels: LogLevelFilter[] = ['All', 'Information', 'Warning', 'Error']
const { currentLocale, t } = useLocalization()
const levelLabel=(level:LogLevelFilter)=>level==='All'?t('logs.severity.all'):t(logLevelKey(level))
const bridgeLabel = computed(() => {
  const key = bridgeStateKey(app.bridge)
  return key ? t(key) : app.bridge
})

const hasSnapshot = computed(() => logs.generatedAt !== null)
const refreshed = computed(() => logs.generatedAt ? new Date(logs.generatedAt).toLocaleTimeString(currentLocale.value) : null)
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
    const queryMatches = !query || [entry.category, entry.message, entry.level, levelLabel(entry.level)].some(value => value.toLocaleLowerCase().includes(query))
    return levelMatches && queryMatches
  })
})
const snapshotNotice = computed(() => {
  if (!hasSnapshot.value) return ''
  if (logs.status === 'error') return t('logs.refreshFailed')
  if (app.bridge !== 'Connected') return t('logs.disconnected')
  if (logs.status === 'loading') return t('logs.refreshingNotice')
  return ''
})

const time = (value: string) => new Date(value).toLocaleTimeString(currentLocale.value, { hour: '2-digit', minute: '2-digit', second: '2-digit' })
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
        <div><h1>{{t('logs.title')}}</h1><p>{{t('logs.subtitle')}}</p><small v-if="refreshed">{{t('logs.lastRefreshed',{time:refreshed})}}</small></div>
        <QButton :disabled="app.bridge !== 'Connected' || logs.status === 'loading'" @click="refresh"><QIcon name="refresh" /> {{ t(logs.status === 'loading'?'logs.refreshing':'logs.refresh') }}</QButton>
      </header>

      <div v-if="snapshotNotice" class="logs-snapshot-notice" role="status"><QIcon :name="logs.status === 'error' ? 'statusWarning' : 'statusInfo'" /><span>{{snapshotNotice}}</span><QButton v-if="logs.status === 'error' && app.bridge === 'Connected'" @click="refresh">{{t('logs.retry')}}</QButton></div>

      <section v-if="!hasSnapshot && app.bridge !== 'Connected'" class="q-empty logs-state"><div class="q-empty-icon"><QIcon name="logs" :size="28" /></div><h3>{{t('logs.waiting')}}</h3><p>{{t('logs.waitingHint')}}</p><QBadge tone="warning">{{ bridgeLabel }}</QBadge></section>
      <section v-else-if="!hasSnapshot && logs.status === 'loading'" class="logs-skeleton" :aria-label="t('logs.loading')"><QSkeleton v-for="item in 6" :key="item" /></section>
      <section v-else-if="!hasSnapshot && logs.status === 'error'" class="q-empty logs-state"><div class="q-empty-icon"><QIcon name="statusDanger" :size="28" /></div><h3>{{t('logs.unavailable')}}</h3><p>{{t('logs.unavailableHint')}}</p><QButton @click="refresh"><QIcon name="refresh" /> {{t('logs.retry')}}</QButton></section>

      <template v-else-if="hasSnapshot">
        <section class="logs-severity-overview" :aria-label="t('logs.severityOverview')">
          <button v-for="level in levels" :key="level" type="button" :class="`is-${level.toLocaleLowerCase()}`" :aria-pressed="levelFilter === level" @click="levelFilter = level"><span>{{ levelLabel(level) }}</span><strong>{{ counts[level] }}</strong></button>
        </section>

        <section class="logs-tools" :aria-label="t('logs.filters')">
          <label class="logs-search"><span>{{t('logs.search')}}</span><div><QIcon name="search" /><input v-model="searchQuery" type="search" :aria-label="t('logs.search')" :placeholder="t('logs.searchPlaceholder')" /></div></label><label class="logs-level-filter"><span>{{t('logs.severity')}}</span><select v-model="levelFilter" :aria-label="t('logs.severity')"><option v-for="level in levels" :key="level" :value="level">{{levelLabel(level)}}</option></select></label>
        </section>

        <p v-if="logs.entries.length" class="logs-results-summary">{{t('logs.summary',{visible:filteredEntries.length,total:logs.entries.length})}}</p>
        <section v-if="logs.entries.length === 0" class="q-empty logs-state logs-inline-state"><div class="q-empty-icon"><QIcon name="logs" :size="28" /></div><h3>{{t('logs.empty')}}</h3><p>{{t('logs.emptyHint')}}</p></section>
        <section v-else-if="filteredEntries.length === 0" class="q-empty logs-state logs-inline-state"><div class="q-empty-icon"><QIcon name="search" :size="28" /></div><h3>{{t('logs.noMatch')}}</h3><p>{{t('logs.noMatchHint')}}</p><QButton @click="clearFilters">{{t('logs.clear')}}</QButton></section>
        <section v-else class="logs-table" :aria-label="t('logs.entries')">
          <div class="logs-head"><span>{{t('logs.time')}}</span><span>{{t('logs.level')}}</span><span>{{t('logs.category')}}</span><span>{{t('logs.message')}}</span></div>
          <article v-for="(entry, index) in filteredEntries" :key="`${entry.timestamp}-${index}`" class="logs-row">
            <time :datetime="entry.timestamp">{{ time(entry.timestamp) }}</time>
            <QBadge :tone="tone(entry.level)">{{ levelLabel(entry.level) }}</QBadge>
            <strong>{{ entry.category }}</strong>
            <p>{{ entry.message }}</p>
          </article>
        </section>
      </template>
    </div>
  </QPage>
</template>
