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

const app = useAppStore()
const modules = useModuleStore()
const client = inject<ModuleClient>('moduleClient')!
const router = useRouter()

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
const startsOnLaunch = computed(() => modules.modules.filter(module => module.isStartupEnabled).length)
const runningPreview = computed(() => modules.runningModules.slice(0, 3))
const hasSnapshot = computed(() => modules.modules.length > 0)
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
const viewModule = (module: ModuleSnapshotItem) => browse('all', module.id)
</script>

<template>
  <QPage class="dashboard-page">
    <section class="dashboard-welcome">
      <div class="dashboard-welcome-copy">
        <span class="dashboard-eyebrow">{{ environment }} workspace</span>
        <h1>QingToolbox</h1>
        <p>Your tools, modules, and workspace at a glance.</p>
        <div class="dashboard-actions">
          <RouterLink class="q-button is-primary" to="/modules">Browse modules</RouterLink>
          <RouterLink class="q-button" to="/running">View running</RouterLink>
        </div>
      </div>
      <dl class="dashboard-host-state">
        <div><dt>Environment</dt><dd>{{ environment }}</dd></div>
        <div><dt>Host version</dt><dd>{{ hostVersion }}</dd></div>
        <div><dt>Bridge</dt><dd><QBadge :tone="app.bridge === 'Connected' ? 'success' : 'warning'">{{ app.bridge }}</QBadge></dd></div>
      </dl>
    </section>

    <div v-if="modules.status === 'error' && hasSnapshot" class="dashboard-stale-notice" role="status">
      <QIcon name="statusWarning" />
      <span>Module refresh failed. The dashboard is showing the last available snapshot.</span>
      <QButton @click="refresh">Retry</QButton>
    </div>

    <section v-if="modules.status === 'loading' && !hasSnapshot" class="dashboard-loading" aria-label="Loading dashboard">
      <QSkeleton v-for="item in 4" :key="item" />
    </section>
    <section v-else-if="modules.status === 'error' && !hasSnapshot" class="q-empty dashboard-full-error">
      <div class="q-empty-icon"><QIcon name="statusDanger" :size="28" /></div>
      <h3>Workspace overview is unavailable</h3>
      <p>The host could not provide the current module state.</p>
      <QButton @click="refresh"><QIcon name="refresh" /> Retry</QButton>
    </section>
    <template v-else>
      <section class="dashboard-overview" aria-labelledby="workspace-overview-title">
        <h2 id="workspace-overview-title">Workspace overview</h2>
        <div class="dashboard-overview-grid">
          <button @click="browse('all')"><span>Total modules</span><strong>{{ modules.modules.length }}</strong><small>Browse installed modules</small></button>
          <button @click="navigate('/running')"><span>Running</span><strong class="success">{{ modules.runningModules.length }}</strong><small>Review active modules</small></button>
          <button @click="openIssues"><span>Needs attention</span><strong class="warning">{{ attentionIds.size }}</strong><small>Review module health</small></button>
          <button @click="browse('all')"><span>Starts on launch</span><strong class="accent">{{ startsOnLaunch }}</strong><small>Review startup access</small></button>
        </div>
      </section>

      <div class="dashboard-main-grid">
        <section class="dashboard-section dashboard-attention" aria-labelledby="attention-title">
          <header><div><h2 id="attention-title">Needs attention</h2><p>Items that may require a decision.</p></div></header>
          <div v-if="attentionIds.size" class="dashboard-attention-list">
            <button v-if="moduleIssues.length" @click="openIssues"><QIcon name="statusWarning" /><span><strong>Module issues</strong><small>{{ moduleIssues.length }} module{{ moduleIssues.length === 1 ? '' : 's' }} report validation or runtime issues.</small></span><em>{{ moduleIssues.length }}</em></button>
            <button v-if="recoveryBlocked.length" @click="openBlocked"><QIcon name="statusDanger" /><span><strong>Recovery blocked</strong><small>{{ recoveryBlocked.length }} module{{ recoveryBlocked.length === 1 ? '' : 's' }} cannot continue until recovery completes.</small></span><em>{{ recoveryBlocked.length }}</em></button>
            <button v-if="startupApproval.length" @click="openStartupApproval"><QIcon name="statusInfo" /><span><strong>Startup approval required</strong><small>{{ startupApproval.length }} changed module{{ startupApproval.length === 1 ? '' : 's' }} need renewed approval.</small></span><em>{{ startupApproval.length }}</em></button>
          </div>
          <div v-else class="dashboard-ready"><QIcon name="statusSuccess" :size="24" /><div><strong>Everything looks ready.</strong><p>No module issues currently need your attention.</p></div></div>
        </section>

        <section class="dashboard-section dashboard-running" aria-labelledby="running-title">
          <header><div><h2 id="running-title">Running now</h2><p>Modules active in the host.</p></div><RouterLink v-if="modules.runningModules.length" to="/running">View all</RouterLink></header>
          <div v-if="runningPreview.length" class="dashboard-running-list">
            <article v-for="module in runningPreview" :key="module.id">
              <span class="module-icon">{{ module.displayName.slice(0, 1).toUpperCase() }}</span>
              <div><div class="dashboard-module-title"><strong>{{ module.displayName }}</strong><QBadge tone="success">Running</QBadge></div><small>v{{ module.version }}</small><p>{{ module.displayDescription }}</p></div>
              <button @click="viewModule(module)">View details</button>
            </article>
          </div>
          <div v-else class="dashboard-running-empty"><QIcon name="running" :size="24" /><div><strong>No modules are running.</strong><p>Start one from the Modules workspace.</p><RouterLink to="/modules">Browse modules</RouterLink></div></div>
        </section>
      </div>

      <section class="dashboard-destinations" aria-labelledby="destinations-title">
        <h2 id="destinations-title">Quick destinations</h2>
        <div>
          <RouterLink to="/modules"><QIcon name="modules" /><span><strong>Modules</strong><small>Browse and manage installed modules.</small></span></RouterLink>
          <RouterLink to="/running"><QIcon name="running" /><span><strong>Running</strong><small>Review modules active in the host.</small></span></RouterLink>
          <RouterLink to="/settings"><QIcon name="settings" /><span><strong>Settings</strong><small>Adjust the Development workspace.</small></span></RouterLink>
        </div>
      </section>
    </template>
  </QPage>
</template>
