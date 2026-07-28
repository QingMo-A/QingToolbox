<script setup lang="ts">
import { computed, inject, watch } from 'vue'
import { useRouter } from 'vue-router'
import { useAppStore } from '../app/store'
import { useModuleStore } from '../app/moduleStore'
import type { ModuleClient } from '../bridge/clients/ModuleClient'
import type { ModuleSnapshotItem } from '../contracts/modules'
import QPage from '../design-system/components/QPage.vue'
import QButton from '../design-system/components/QButton.vue'
import QBadge from '../design-system/components/QBadge.vue'
import QIcon from '../design-system/components/QIcon.vue'
import QSkeleton from '../design-system/components/QSkeleton.vue'
import { useLocalization } from '../localization/localization'
import { bridgeStateKey } from '../presentation/workspacePresentation'

const app = useAppStore()
const modules = useModuleStore()
const client = inject<ModuleClient>('moduleClient')!
const router = useRouter()
const {t}=useLocalization();const bridgeLabel=(value:string)=>{const key=bridgeStateKey(value);return key?t(key):value}

async function refresh() {
  modules.begin()
  try { modules.complete(await client.getSnapshot()) }
  catch (error) { modules.fail(error) }
}

watch(() => app.bridge, bridge => {
  if (bridge === 'Connected' && modules.status === 'idle') void refresh()
}, { immediate: true })

const moduleIssues = computed(() => modules.modules.filter(module => !module.isValid || module.errorCount > 0))
const recoveryBlocked = computed(() => modules.modules.filter(module => module.isExecutionBlocked))
const startupApproval = computed(() => modules.modules.filter(module => module.startupAuthorizationState === 'ChangedNeedsConfirmation'))
const attentionIds = computed(() => new Set([...moduleIssues.value, ...recoveryBlocked.value, ...startupApproval.value].map(module => module.id)))
const startupEnabled = computed(() => modules.modules.filter(module => module.isStartupEnabled))
const runningPreview = computed(() => modules.runningModules.slice(0, 3))
const hasSnapshot = computed(() => modules.lastUpdatedAt !== null)
const waitingForHost = computed(() => app.bridge !== 'Connected' && !hasSnapshot.value && modules.modules.length === 0)
const snapshotWarning = computed(() => {
  if (!hasSnapshot.value) return ''
  if (modules.status === 'error') return t('home.refreshFailed')
  if (app.bridge !== 'Connected') return t('home.disconnected')
  return ''
})
const environment = computed(() => app.snapshot?.environmentDisplayName || app.mode || 'Development')
const hostVersion = computed(() => app.snapshot?.hostVersion || 'Unavailable')

async function navigate(path: string) { await router.push(path) }
async function browse(filter: 'all' | 'issues' = 'all', selectedId: string | null = null) {
  modules.stateFilter = filter
  modules.selectedModuleId = selectedId
  await navigate('/modules')
}
const openIssues = () => browse('issues', moduleIssues.value[0]?.id ?? null)
const openBlocked = () => browse('all', recoveryBlocked.value[0]?.id ?? null)
const openStartupApproval = () => browse('all', startupApproval.value[0]?.id ?? null)
function openPrimaryAttention() {
  if (moduleIssues.value.length) return openIssues()
  if (recoveryBlocked.value.length) return openBlocked()
  if (startupApproval.value.length) return openStartupApproval()
  return browse('all')
}
const openStartsOnLaunch = () => browse('all', startupEnabled.value[0]?.id ?? null)
const viewModule = (module: ModuleSnapshotItem) => browse('all', module.id)
</script>

<template>
  <QPage class="dashboard-page">
    <section class="dashboard-welcome">
      <div class="dashboard-welcome-copy">
        <span class="dashboard-eyebrow">{{t('home.workspace',{environment})}}</span>
        <h1>QingToolbox</h1>
        <p>{{t('home.subtitle')}}</p>
        <div class="dashboard-actions">
          <RouterLink class="q-button is-primary" to="/modules">{{t('home.browse')}}</RouterLink><RouterLink class="q-button" to="/running">{{t('home.viewRunning')}}</RouterLink>
        </div>
      </div>
      <dl class="dashboard-host-state">
        <div><dt>{{t('home.environment')}}</dt><dd>{{ environment }}</dd></div><div><dt>{{t('home.hostVersion')}}</dt><dd>{{ hostVersion }}</dd></div><div><dt>{{t('home.bridge')}}</dt><dd><QBadge :tone="app.bridge === 'Connected' ? 'success' : 'warning'">{{bridgeLabel(app.bridge)}}</QBadge></dd></div>
      </dl>
    </section>

    <div v-if="snapshotWarning" class="dashboard-stale-notice" role="status">
      <QIcon name="statusWarning" />
      <span>{{ snapshotWarning }}</span>
      <QButton v-if="modules.status === 'error'" @click="refresh">{{t('home.retry')}}</QButton>
    </div>

    <section v-if="waitingForHost" class="q-empty dashboard-waiting" aria-live="polite">
      <div class="q-empty-icon"><QIcon name="statusInfo" :size="28" /></div>
      <h3>{{t('home.waiting')}}</h3><p>{{t('home.waitingHint')}}</p><QBadge tone="warning">{{bridgeLabel(app.bridge)}}</QBadge>
    </section>
    <section v-else-if="modules.status === 'loading' && !hasSnapshot" class="dashboard-loading" :aria-label="t('home.loading')">
      <QSkeleton v-for="item in 4" :key="item" />
    </section>
    <section v-else-if="modules.status === 'error' && !hasSnapshot" class="q-empty dashboard-full-error">
      <div class="q-empty-icon"><QIcon name="statusDanger" :size="28" /></div>
      <h3>{{t('home.unavailable')}}</h3><p>{{t('home.unavailableHint')}}</p><QButton @click="refresh"><QIcon name="refresh" /> {{t('home.retry')}}</QButton>
    </section>
    <template v-else>
      <section class="dashboard-overview" aria-labelledby="workspace-overview-title">
        <h2 id="workspace-overview-title">{{t('home.overview')}}</h2>
        <div class="dashboard-overview-grid">
          <button @click="browse('all')"><span>{{t('home.total')}}</span><strong>{{modules.modules.length}}</strong><small>{{t('home.browseInstalled')}}</small></button>
          <button @click="navigate('/running')"><span>{{t('home.running')}}</span><strong class="success">{{modules.runningModules.length}}</strong><small>{{t('home.reviewRunning')}}</small></button><button @click="openPrimaryAttention"><span>{{t('home.attention')}}</span><strong class="warning">{{attentionIds.size}}</strong><small>{{t('home.reviewHealth')}}</small></button><button @click="openStartsOnLaunch"><span>{{t('home.starts')}}</span><strong class="accent">{{startupEnabled.length}}</strong><small>{{t('home.reviewStartup')}}</small></button>
        </div>
      </section>

      <div class="dashboard-main-grid">
        <section class="dashboard-section dashboard-attention" aria-labelledby="attention-title">
          <header><div><h2 id="attention-title">{{t('home.attention')}}</h2><p>{{t('home.attentionHint')}}</p></div></header>
          <div v-if="attentionIds.size" class="dashboard-attention-list">
            <button v-if="moduleIssues.length" @click="openIssues"><QIcon name="statusWarning" /><span><strong>{{t('home.moduleIssues')}}</strong><small>{{t(moduleIssues.length===1?'home.moduleIssueOne':'home.moduleIssueMany',{count:moduleIssues.length})}}</small></span><em>{{moduleIssues.length}}</em></button>
            <button v-if="recoveryBlocked.length" @click="openBlocked"><QIcon name="statusDanger" /><span><strong>{{t('home.recovery')}}</strong><small>{{t(recoveryBlocked.length===1?'home.recoveryOne':'home.recoveryMany',{count:recoveryBlocked.length})}}</small></span><em>{{recoveryBlocked.length}}</em></button>
            <button v-if="startupApproval.length" @click="openStartupApproval"><QIcon name="statusInfo" /><span><strong>{{t('home.approval')}}</strong><small>{{t(startupApproval.length===1?'home.approvalOne':'home.approvalMany',{count:startupApproval.length})}}</small></span><em>{{startupApproval.length}}</em></button>
          </div>
          <div v-else class="dashboard-ready"><QIcon name="statusSuccess" :size="24" /><div><strong>{{t('home.ready')}}</strong><p>{{t('home.readyHint')}}</p></div></div>
        </section>

        <section class="dashboard-section dashboard-running" aria-labelledby="running-title">
          <header><div><h2 id="running-title">{{t('home.runningNow')}}</h2><p>{{t('home.runningHint')}}</p></div><RouterLink v-if="modules.runningModules.length" to="/running">{{t('home.viewAll')}}</RouterLink></header>
          <div v-if="runningPreview.length" class="dashboard-running-list">
            <article v-for="module in runningPreview" :key="module.id">
              <span class="module-icon">{{ module.displayName.slice(0, 1).toUpperCase() }}</span>
              <div><div class="dashboard-module-title"><strong>{{module.displayName}}</strong><QBadge tone="success">{{t('moduleState.running')}}</QBadge></div><small>v{{module.version}}</small><p>{{module.displayDescription}}</p></div>
              <button @click="viewModule(module)">{{t('home.details')}}</button>
            </article>
          </div>
          <div v-else class="dashboard-running-empty"><QIcon name="running" :size="24" /><div><strong>{{t('home.noneRunning')}}</strong><p>{{t('home.noneRunningHint')}}</p><RouterLink to="/modules">{{t('home.browse')}}</RouterLink></div></div>
        </section>
      </div>

      <section class="dashboard-destinations" aria-labelledby="destinations-title">
        <h2 id="destinations-title">{{t('home.destinations')}}</h2>
        <div>
          <RouterLink to="/modules"><QIcon name="modules" /><span><strong>{{t('home.modules')}}</strong><small>{{t('home.modulesHint')}}</small></span></RouterLink><RouterLink to="/running"><QIcon name="running" /><span><strong>{{t('home.runningDestination')}}</strong><small>{{t('home.runningDestinationHint')}}</small></span></RouterLink><RouterLink to="/settings"><QIcon name="settings" /><span><strong>{{t('home.settings')}}</strong><small>{{t('home.settingsHint')}}</small></span></RouterLink>
        </div>
      </section>
    </template>
  </QPage>
</template>
