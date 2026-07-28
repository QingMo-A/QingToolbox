<script setup lang="ts">
import { computed, inject, ref } from 'vue'
import { useLocalization } from '../localization/localization'
import type { TranslationParameters } from '../localization/localization'
import type { TranslationKey } from '../localization/messages/en-US'
import { bridgeStateKey } from '../presentation/workspacePresentation'
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
type DiagnosticNotice={key:TranslationKey;parameters?:TranslationParameters;tone:'success'|'warning'|'danger'}
const actionNotice=ref<DiagnosticNotice|null>(null)
const {currentLocale,t}=useLocalization()
const bridgeLabel=(value:string)=>{const key=bridgeStateKey(value);return key?t(key):value}

const hasSnapshot = computed(() => store.snapshot !== null)
const invalidModules = computed(() => Math.max(0, (store.snapshot?.totalModuleCount ?? 0) - (store.snapshot?.validModuleCount ?? 0)))
const bridgeTone = computed<'success'|'warning'|'danger'>(() => store.bridge === 'Connected' ? 'success' : store.bridge === 'Connecting' ? 'warning' : 'danger')
const hostActionsDisabled = computed(() => store.bridge !== 'Connected' || activeAction.value !== null)
const notice = computed(() => {
  if(actionNotice.value)return {...actionNotice.value,text:t(actionNotice.value.key,actionNotice.value.parameters)}
  if (store.error) return { text:t('diagnostics.error'), tone: 'danger' }
  if (store.bridge !== 'Connected' && hasSnapshot.value) return { text:t('diagnostics.disconnected'), tone: 'warning' }
  if (store.bridge === 'Connected' && !hasSnapshot.value) return { text:t('diagnostics.noSnapshot'), tone: 'warning' }
  return null
})

const localTime = (value: string) => new Date(value).toLocaleString(currentLocale.value)

async function ping() {
  if (hostActionsDisabled.value) return
  activeAction.value = 'ping'; actionNotice.value=null
  const started = performance.now()
  try {
    await app.ping()
    store.pingMs = Math.round(performance.now() - started)
    actionNotice.value={key:'diagnostics.pingSuccess',parameters:{milliseconds:store.pingMs},tone:'success'}
  } catch {
    actionNotice.value={key:'diagnostics.pingFailed',tone:'danger'}
  } finally { activeAction.value = null }
}

async function refreshSnapshot() {
  if (hostActionsDisabled.value) return
  activeAction.value = 'snapshot'; actionNotice.value=null
  try {
    store.rebuild(await app.getSnapshot())
    actionNotice.value={key:'diagnostics.refreshSuccess',tone:'success'}
  } catch {
    actionNotice.value={key:'diagnostics.refreshFailed',tone:'danger'}
  } finally { activeAction.value = null }
}

function reload() { window.location.reload() }
</script>

<template>
  <QPage class="diagnostics-page">
    <div class="diagnostics-workspace">
      <header class="wpf-page-header diagnostics-header">
        <div><span class="diagnostics-eyebrow">{{t('diagnostics.only')}}</span><h1>{{t('diagnostics.title')}}</h1><p>{{t('diagnostics.subtitle')}}</p></div>
      </header>

      <div v-if="notice" class="diagnostics-notice" :class="`is-${notice.tone}`" role="status" aria-live="polite"><QIcon :name="notice.tone === 'danger' ? 'statusDanger' : notice.tone === 'success' ? 'statusSuccess' : 'statusWarning'" /><span>{{ notice.text }}</span></div>

      <section v-if="store.bridge !== 'Connected' && !hasSnapshot" class="q-empty diagnostics-waiting"><div class="q-empty-icon"><QIcon name="statusInfo" :size="28" /></div><h3>{{t('diagnostics.waiting')}}</h3><p>{{t('diagnostics.waitingHint')}}</p><QBadge :tone="bridgeTone">{{ bridgeLabel(store.bridge) }}</QBadge></section>

      <div class="diagnostics-primary-grid">
          <section class="diagnostics-panel" aria-labelledby="host-connection-title"><header><div><h2 id="host-connection-title">{{t('diagnostics.host')}}</h2><p>{{t('diagnostics.hostHint')}}</p></div><QBadge :tone="bridgeTone">{{bridgeLabel(store.bridge)}}</QBadge></header><dl class="diagnostics-values"><div><dt>{{t('diagnostics.bridge')}}</dt><dd>{{bridgeLabel(store.bridge)}}</dd></div><div><dt>{{t('diagnostics.environment')}}</dt><dd>{{store.snapshot?.environmentDisplayName??t('diagnostics.unavailable')}}</dd></div><div><dt>{{t('diagnostics.mode')}}</dt><dd>{{store.mode}}</dd></div><div><dt>{{t('diagnostics.version')}}</dt><dd>{{store.snapshot?.hostVersion??t('diagnostics.unavailable')}}</dd></div><div><dt>{{t('diagnostics.protocol')}}</dt><dd>{{store.snapshot?`v${store.snapshot.protocolVersion}`:t('diagnostics.unavailable')}}</dd></div></dl></section>

          <section class="diagnostics-panel" aria-labelledby="module-snapshot-title"><header><div><h2 id="module-snapshot-title">{{t('diagnostics.snapshot')}}</h2><p>{{t('diagnostics.snapshotHint')}}</p></div></header><div v-if="store.snapshot" class="diagnostics-module-counts"><article><span>{{t('diagnostics.total')}}</span><strong>{{store.snapshot.totalModuleCount}}</strong></article><article class="is-success"><span>{{t('diagnostics.valid')}}</span><strong>{{store.snapshot.validModuleCount}}</strong></article><article class="is-brand"><span>{{t('diagnostics.running')}}</span><strong>{{store.snapshot.runningModuleCount}}</strong></article><article :class="{'is-warning':invalidModules>0}"><span>{{t('diagnostics.invalid')}}</span><strong>{{invalidModules}}</strong></article></div><div v-else class="diagnostics-compact-empty"><strong>{{t('diagnostics.snapshotEmpty')}}</strong><p>{{t('diagnostics.snapshotEmptyHint')}}</p></div></section>
      </div>

        <section class="diagnostics-panel diagnostics-activity" aria-labelledby="activity-title"><header><div><h2 id="activity-title">{{t('diagnostics.activity')}}</h2><p>{{t('diagnostics.activityHint')}}</p></div></header><dl class="diagnostics-activity-grid"><div><dt>{{t('diagnostics.lastSnapshot')}}</dt><dd>{{store.snapshot?localTime(store.snapshot.generatedAt):t('diagnostics.notAvailable')}}</dd></div><div><dt>{{t('diagnostics.lastEvent')}}</dt><dd>{{store.lastEvent}}</dd></div><div><dt>{{t('diagnostics.lastPing')}}</dt><dd>{{store.pingMs===null?t('diagnostics.notRun'):`${store.pingMs} ms`}}</dd></div></dl></section>

        <section class="diagnostics-tools" aria-labelledby="tools-title"><header><h2 id="tools-title">{{t('diagnostics.tools')}}</h2><p>{{t('diagnostics.toolsHint')}}</p></header><div class="diagnostics-tool-groups"><section><h3>{{t('diagnostics.hostChecks')}}</h3><article><div><strong>{{t('diagnostics.ping')}}</strong><p>{{t('diagnostics.pingHint')}}</p></div><QButton :disabled="hostActionsDisabled" @click="ping"><QIcon name="statusInfo" />{{t(activeAction==='ping'?'diagnostics.pinging':'diagnostics.ping')}}</QButton></article><article><div><strong>{{t('diagnostics.refresh')}}</strong><p>{{t('diagnostics.refreshHint')}}</p></div><QButton :disabled="hostActionsDisabled" @click="refreshSnapshot"><QIcon name="refresh" />{{t(activeAction==='snapshot'?'diagnostics.refreshing':'diagnostics.refresh')}}</QButton></article></section><section><h3>{{t('diagnostics.web')}}</h3><article><div><strong>{{t('diagnostics.reload')}}</strong><p>{{t('diagnostics.reloadHint')}}</p></div><QButton @click="reload"><QIcon name="refresh" />{{t('diagnostics.reload')}}</QButton></article></section></div></section>
    </div>
  </QPage>
</template>
