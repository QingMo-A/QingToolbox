<script setup lang="ts">
import { computed, inject, ref } from 'vue'
import { useAppStore } from '../app/store'
import type { AppClient } from '../bridge/clients/AppClient'
import QPage from '../design-system/components/QPage.vue'
import QButton from '../design-system/components/QButton.vue'
import QBadge from '../design-system/components/QBadge.vue'
import QIcon from '../design-system/components/QIcon.vue'

type DiagnosticAction = 'ping' | 'snapshot' | null

const app = inject<AppClient>('appClient')!
const store = useAppStore()
const activeAction = ref<DiagnosticAction>(null)
const actionMessage = ref('')
const actionError = ref('')

const hasSnapshot = computed(() => store.snapshot !== null)
const invalidModules = computed(() => Math.max(0, (store.snapshot?.totalModuleCount ?? 0) - (store.snapshot?.validModuleCount ?? 0)))
const bridgeTone = computed<'success'|'warning'|'danger'>(() => store.bridge === 'Connected' ? 'success' : store.bridge === 'Connecting' ? 'warning' : 'danger')
const hostActionsDisabled = computed(() => store.bridge !== 'Connected' || activeAction.value !== null)
const notice = computed(() => {
  if (actionError.value) return { text: actionError.value, tone: 'danger' }
  if (actionMessage.value) return { text: actionMessage.value, tone: 'success' }
  if (store.error) return { text: 'The Development Web workspace reported a host communication error.', tone: 'danger' }
  if (store.bridge !== 'Connected' && hasSnapshot.value) return { text: 'The host is disconnected. Showing the last available diagnostic snapshot.', tone: 'warning' }
  if (store.bridge === 'Connected' && !hasSnapshot.value) return { text: 'The bridge is connected, but no host snapshot is currently available.', tone: 'warning' }
  return null
})

const localTime = (value: string) => new Date(value).toLocaleString()

async function ping() {
  if (hostActionsDisabled.value) return
  activeAction.value = 'ping'; actionMessage.value = ''; actionError.value = ''
  const started = performance.now()
  try {
    await app.ping()
    store.pingMs = Math.round(performance.now() - started)
    actionMessage.value = `Host responded in ${store.pingMs} ms.`
  } catch {
    actionError.value = 'The host ping could not be completed.'
  } finally { activeAction.value = null }
}

async function refreshSnapshot() {
  if (hostActionsDisabled.value) return
  activeAction.value = 'snapshot'; actionMessage.value = ''; actionError.value = ''
  try {
    store.rebuild(await app.getSnapshot())
    actionMessage.value = 'Host snapshot refreshed.'
  } catch {
    actionError.value = 'The host snapshot could not be refreshed.'
  } finally { activeAction.value = null }
}

function reload() { window.location.reload() }
</script>

<template>
  <QPage class="diagnostics-page">
    <div class="diagnostics-workspace">
      <header class="wpf-page-header diagnostics-header">
        <div><span class="diagnostics-eyebrow">Development only</span><h1>Development diagnostics</h1><p>Inspect the current Web Shell and host connection.</p></div>
      </header>

      <div v-if="notice" class="diagnostics-notice" :class="`is-${notice.tone}`" role="status" aria-live="polite"><QIcon :name="notice.tone === 'danger' ? 'statusDanger' : notice.tone === 'success' ? 'statusSuccess' : 'statusWarning'" /><span>{{ notice.text }}</span></div>

      <section v-if="store.bridge !== 'Connected' && !hasSnapshot" class="q-empty diagnostics-waiting"><div class="q-empty-icon"><QIcon name="statusInfo" :size="28" /></div><h3>Waiting for the host</h3><p>Diagnostics will become available after the Development bridge connects.</p><QBadge :tone="bridgeTone">{{ store.bridge }}</QBadge></section>

      <div class="diagnostics-primary-grid">
          <section class="diagnostics-panel" aria-labelledby="host-connection-title"><header><div><h2 id="host-connection-title">Host connection</h2><p>Current Development bridge identity.</p></div><QBadge :tone="bridgeTone">{{ store.bridge }}</QBadge></header><dl class="diagnostics-values"><div><dt>Bridge</dt><dd>{{ store.bridge }}</dd></div><div><dt>Environment</dt><dd>{{ store.snapshot?.environmentDisplayName ?? 'Unavailable' }}</dd></div><div><dt>Mode</dt><dd>{{ store.mode }}</dd></div><div><dt>Host version</dt><dd>{{ store.snapshot?.hostVersion ?? 'Unavailable' }}</dd></div><div><dt>Protocol</dt><dd>{{ store.snapshot ? `v${store.snapshot.protocolVersion}` : 'Unavailable' }}</dd></div></dl></section>

          <section class="diagnostics-panel" aria-labelledby="module-snapshot-title"><header><div><h2 id="module-snapshot-title">Module snapshot</h2><p>Authoritative counts from the last host snapshot.</p></div></header><div v-if="store.snapshot" class="diagnostics-module-counts"><article><span>Total modules</span><strong>{{ store.snapshot.totalModuleCount }}</strong></article><article class="is-success"><span>Valid modules</span><strong>{{ store.snapshot.validModuleCount }}</strong></article><article class="is-brand"><span>Running modules</span><strong>{{ store.snapshot.runningModuleCount }}</strong></article><article :class="{ 'is-warning': invalidModules > 0 }"><span>Invalid modules</span><strong>{{ invalidModules }}</strong></article></div><div v-else class="diagnostics-compact-empty"><strong>No host snapshot is available.</strong><p>Request a snapshot after the bridge connects.</p></div></section>
      </div>

        <section class="diagnostics-panel diagnostics-activity" aria-labelledby="activity-title"><header><div><h2 id="activity-title">Activity and checks</h2><p>Latest values observed in this Web session.</p></div></header><dl class="diagnostics-activity-grid"><div><dt>Last snapshot</dt><dd>{{ store.snapshot ? localTime(store.snapshot.generatedAt) : 'Not available' }}</dd></div><div><dt>Last host event</dt><dd>{{ store.lastEvent }}</dd></div><div><dt>Last ping</dt><dd>{{ store.pingMs === null ? 'Not run' : `${store.pingMs} ms` }}</dd></div></dl></section>

        <section class="diagnostics-tools" aria-labelledby="tools-title"><header><h2 id="tools-title">Development tools</h2><p>Run explicit checks without changing module or host lifecycle state.</p></header><div class="diagnostics-tool-groups"><section><h3>Host checks</h3><article><div><strong>Ping host</strong><p>Check whether the activated Development bridge responds.</p></div><QButton :disabled="hostActionsDisabled" @click="ping"><QIcon name="statusInfo" />{{ activeAction === 'ping' ? 'Pinging…' : 'Ping host' }}</QButton></article><article><div><strong>Refresh snapshot</strong><p>Request the latest authoritative host overview.</p></div><QButton :disabled="hostActionsDisabled" @click="refreshSnapshot"><QIcon name="refresh" />{{ activeAction === 'snapshot' ? 'Refreshing…' : 'Refresh snapshot' }}</QButton></article></section><section><h3>Web workspace</h3><article><div><strong>Reload Web UI</strong><p>Reload only the current Web workspace and establish a new page session.</p></div><QButton @click="reload"><QIcon name="refresh" />Reload Web UI</QButton></article></section></div></section>
    </div>
  </QPage>
</template>
