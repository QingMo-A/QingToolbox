<script setup lang="ts">
import { computed, inject, onBeforeUnmount, onMounted, watch } from 'vue'
import type { ModuleClient } from '../bridge/clients/ModuleClient'
import { useAppStore } from '../app/store'
import { useModuleStore, type ModuleFilter, type ModuleOperation } from '../app/moduleStore'
import type { ModuleSnapshotItem } from '../contracts/modules'
import { useToastStore } from '../app/toastStore'
import QPage from '../design-system/components/QPage.vue'
import QButton from '../design-system/components/QButton.vue'
import QBadge from '../design-system/components/QBadge.vue'
import QEmptyState from '../design-system/components/QEmptyState.vue'
import QSkeleton from '../design-system/components/QSkeleton.vue'
import QIcon from '../design-system/components/QIcon.vue'

const client = inject<ModuleClient>('moduleClient')!
const app = useAppStore()
const store = useModuleStore()
const toast = useToastStore()

async function refresh(showToast = false) {
  store.begin()
  try {
    store.complete(await client.getSnapshot())
    if (showToast) toast.show('Module snapshot refreshed.', 'success')
  } catch (error) {
    store.fail(error)
    toast.show('The host could not refresh modules.', 'error')
  }
}

async function operate(module: ModuleSnapshotItem, operation: ModuleOperation) {
  if (!store.beginOperation(module.id, operation)) return
  try {
    const snapshot = operation === 'load' ? await client.load(module.id)
      : operation === 'activate' ? await client.activate(module.id)
        : operation === 'open' ? await client.open(module.id)
          : operation === 'deactivate' ? await client.deactivate(module.id)
            : await client.unload(module.id)
    store.complete(snapshot)
    toast.show(operation === 'open' ? `${module.displayName} window opened or focused.` : `${module.displayName} ${operation === 'load' ? 'loaded' : operation === 'activate' ? 'activated' : operation === 'deactivate' ? 'deactivated' : 'unloaded'}.`, 'success')
  } catch (error) {
    toast.show(error instanceof Error ? error.message : String(error), 'error')
    try { store.complete(await client.getSnapshot()) } catch { /* preserve the original operation error and snapshot */ }
  } finally {
    store.endOperation(module.id)
  }
}

const operationLabel = (module: ModuleSnapshotItem, operation: ModuleOperation) => store.operations[module.id] === operation
  ? (operation === 'load' ? 'Loading…' : operation === 'activate' ? 'Activating…' : operation === 'open' ? 'Opening…' : operation === 'deactivate' ? 'Deactivating…' : 'Unloading…')
  : (operation === 'load' ? 'Load' : operation === 'activate' ? 'Activate' : operation === 'open' ? 'Open' : operation === 'deactivate' ? 'Deactivate' : 'Unload')

async function setStartupAuthorization(module: ModuleSnapshotItem, enabled: boolean) {
  if (!store.beginOperation(module.id, 'startupAuthorization')) return
  try {
    store.complete(await client.setStartupAuthorization(module.id, enabled))
    toast.show(enabled ? `${module.displayName} will start with QingToolbox.` : `${module.displayName} will no longer start with QingToolbox.`, 'success')
  } catch (error) {
    toast.show(error instanceof Error ? error.message : String(error), 'error')
    try { store.complete(await client.getSnapshot()) } catch { /* preserve original operation failure */ }
  } finally {
    store.endOperation(module.id)
  }
}

const startupMessage = (state: ModuleSnapshotItem['startupAuthorizationState']) => ({
  NotEnabled: 'This module is not authorized for automatic startup.',
  Enabled: 'The current module payload is authorized to start with QingToolbox.',
  ChangedNeedsConfirmation: 'The module payload changed. Turn this on again to approve the current version.',
  Unavailable: 'The module payload could not be verified, so startup authorization cannot be changed.',
  Missing: 'The saved startup authorization no longer matches an available module.'
}[state])
const cardCanLoad = (module: ModuleSnapshotItem) => ['NotLoaded', 'Unloaded'].includes(module.runtimeState) && module.canLoad
const cardCanActivate = (module: ModuleSnapshotItem) => ['Loaded', 'Deactivated'].includes(module.runtimeState) && module.canActivate
const cardCanOpen = (module: ModuleSnapshotItem) => ['Loaded', 'Running', 'Deactivated'].includes(module.runtimeState) && module.canOpen && !module.isExecutionBlocked
const openDetails = (moduleId: string) => { store.selectedModuleId = moduleId }
watch(() => app.bridge, bridge => { if (bridge === 'Connected' && store.status === 'idle') void refresh() }, { immediate: true })
const failed = computed(() => store.modules.filter(x => x.errorCount > 0 || !x.isValid).length)
const notLoaded = computed(() => store.modules.filter(x => x.runtimeState === 'NotLoaded').length)
const loaded = computed(() => store.modules.filter(x => x.runtimeState === 'Loaded').length)
const running = computed(() => store.modules.filter(x => x.runtimeState === 'Running').length)
const filters: { key: ModuleFilter; label: string }[] = [{ key: 'all', label: 'All' }, { key: 'running', label: 'Running' }, { key: 'notLoaded', label: 'Not loaded' }, { key: 'issues', label: 'Issues' }, { key: 'invalid', label: 'Invalid' }]
const tone = (module: { isValid: boolean; runtimeState: string }) => !module.isValid ? 'danger' : module.runtimeState === 'Running' ? 'success' : module.runtimeState === 'NotLoaded' ? 'neutral' : 'info'
const closeOnEscape = (event: KeyboardEvent) => { if (event.key === 'Escape') store.selectedModuleId = null }
onMounted(() => window.addEventListener('keydown', closeOnEscape))
onBeforeUnmount(() => window.removeEventListener('keydown', closeOnEscape))
</script>

<template>
  <QPage class="modules-page">
    <header class="wpf-page-header">
      <div><h1>Modules</h1><p>Discover, browse, and inspect toolbox modules.</p></div>
      <QButton @click="refresh(true)" :disabled="store.status === 'loading'"><QIcon name="refresh" /> {{ store.status === 'loading' ? 'Refreshing…' : 'Refresh modules' }}</QButton>
    </header>
    <section class="wpf-status-strip">
      <span v-if="store.status === 'loading'">Refreshing module state…</span>
      <span v-else-if="store.status === 'error'">Module state is unavailable.</span>
      <span v-else>Found {{ store.modules.length }} module{{ store.modules.length === 1 ? '' : 's' }}.</span>
    </section>
    <section v-if="store.modules.length" class="wpf-module-summary">
      <article><label>Total</label><strong>{{ store.modules.length }}</strong></article>
      <article><label>Valid</label><strong class="success">{{ store.modules.filter(x => x.isValid).length }}</strong></article>
      <article><label>Failed</label><strong class="warning">{{ failed }}</strong></article>
      <article><label>Not loaded</label><strong class="accent">{{ notLoaded }}</strong></article>
      <article><label>Loaded</label><strong class="info">{{ loaded }}</strong></article>
      <article><label>Running</label><strong class="success">{{ running }}</strong></article>
    </section>
    <section class="wpf-module-tools">
      <label><span class="sr-only">Search modules</span><span class="wpf-search-icon"><QIcon name="search" /></span><input v-model="store.searchQuery" type="search" placeholder="Search modules…"></label>
      <div class="filters" aria-label="Module state filter"><button v-for="filter in filters" :key="filter.key" :class="{ active: store.stateFilter === filter.key }" @click="store.stateFilter = filter.key">{{ filter.label }}</button></div>
    </section>
    <div class="wpf-module-workspace" :class="{ 'has-selection': !!store.selectedModule }">
      <section class="wpf-module-list">
        <div v-if="store.status === 'loading' && store.modules.length === 0" class="wpf-module-stack"><QSkeleton v-for="n in 3" :key="n" /></div>
        <QEmptyState v-else-if="store.status === 'error'" title="Modules are unavailable" :description="store.error"><QButton @click="refresh()">Try again</QButton></QEmptyState>
        <QEmptyState v-else-if="store.visibleModules.length === 0" :title="store.modules.length ? 'No modules found' : 'No modules installed'" :description="store.modules.length ? 'Try another search or state filter.' : 'Imported modules will appear here without being loaded automatically.'" />
        <div v-else class="wpf-module-stack">
          <article v-for="module in store.visibleModules" :key="module.id" class="wpf-module-card" :class="{ selected: store.selectedModuleId === module.id }">
            <header>
              <span class="module-icon">{{ module.displayName.slice(0, 1).toUpperCase() }}</span>
              <div><h2>{{ module.displayName }} <small>v{{ module.version }}</small></h2></div>
              <div class="module-card-badges">
                <QBadge :tone="tone(module)">{{ module.isValid ? module.runtimeState : 'Invalid' }}</QBadge>
                <QBadge v-if="module.startupAuthorizationState === 'Enabled'" tone="info">Starts on launch</QBadge>
                <QBadge v-else-if="module.startupAuthorizationState === 'ChangedNeedsConfirmation'" tone="warning">Startup approval required</QBadge>
              </div>
            </header>
            <p>{{ module.displayDescription }}</p>
            <p v-if="module.isExecutionBlocked" class="module-operation-blocked">Module operations are blocked while recovery is pending.</p>
            <div class="module-actions module-card-actions">
              <QButton v-if="cardCanLoad(module)" variant="primary" :disabled="!!store.operations[module.id] || module.isBusy" @click="operate(module, 'load')">{{ operationLabel(module, 'load') }}</QButton>
              <QButton v-if="cardCanActivate(module)" variant="primary" :disabled="!!store.operations[module.id] || module.isBusy" @click="operate(module, 'activate')">{{ operationLabel(module, 'activate') }}</QButton>
              <QButton v-if="cardCanOpen(module)" variant="primary" :disabled="!!store.operations[module.id] || module.isBusy" @click="operate(module, 'open')">{{ operationLabel(module, 'open') }}</QButton>
              <QButton class="module-details-button" @click="openDetails(module.id)" @keydown.enter.prevent="openDetails(module.id)" @keydown.space.prevent="openDetails(module.id)">Details</QButton>
            </div>
            <div class="module-card-meta"><span>Runtime: <strong>{{ module.runtimeState }}</strong></span><span :class="{ issue: module.errorCount }">{{ module.errorCount ? `${module.errorCount} issue${module.errorCount === 1 ? '' : 's'}` : 'No issues' }}</span></div>
          </article>
        </div>
      </section>
      <aside v-if="store.selectedModule" class="wpf-module-details">
        <button class="wpf-back" aria-label="Back to module list" @click="store.selectedModuleId = null"><QIcon name="back" /></button>
        <header><span class="module-icon">{{ store.selectedModule.displayName.slice(0, 1).toUpperCase() }}</span><div><h2>{{ store.selectedModule.displayName }}</h2><small>v{{ store.selectedModule.version }}</small></div></header>
        <QBadge :tone="tone(store.selectedModule)">{{ store.selectedModule.isValid ? store.selectedModule.runtimeState : 'Invalid' }}</QBadge>
        <p>{{ store.selectedModule.displayDescription }}</p>
        <p v-if="store.selectedModule.isExecutionBlocked" class="module-operation-blocked">Module operations are blocked while recovery is pending.</p>
        <h3>Module actions</h3>
        <div class="module-actions module-detail-actions">
          <QButton v-if="store.selectedModule.canLoad" variant="primary" :disabled="!!store.operations[store.selectedModule.id] || store.selectedModule.isBusy" @click="operate(store.selectedModule, 'load')">{{ operationLabel(store.selectedModule, 'load') }}</QButton>
          <QButton v-if="store.selectedModule.canActivate" variant="primary" :disabled="!!store.operations[store.selectedModule.id] || store.selectedModule.isBusy" @click="operate(store.selectedModule, 'activate')">{{ operationLabel(store.selectedModule, 'activate') }}</QButton>
          <QButton v-if="store.selectedModule.canOpen && !store.selectedModule.isExecutionBlocked" variant="primary" :disabled="!!store.operations[store.selectedModule.id] || store.selectedModule.isBusy" @click="operate(store.selectedModule, 'open')">{{ operationLabel(store.selectedModule, 'open') }}</QButton>
          <QButton v-if="store.selectedModule.canDeactivate && !store.selectedModule.isExecutionBlocked" :disabled="!!store.operations[store.selectedModule.id] || store.selectedModule.isBusy" @click="operate(store.selectedModule, 'deactivate')">{{ operationLabel(store.selectedModule, 'deactivate') }}</QButton>
          <QButton v-if="store.selectedModule.canUnload && !store.selectedModule.isExecutionBlocked" :disabled="!!store.operations[store.selectedModule.id] || store.selectedModule.isBusy" @click="operate(store.selectedModule, 'unload')">{{ operationLabel(store.selectedModule, 'unload') }}</QButton>
        </div>
        <div class="wpf-detail-divider" />
        <section class="module-startup"><h3>Startup</h3><p>Loads and activates this module the next time QingToolbox starts, after verifying its current payload.</p><div class="settings-switch-row"><div><strong>Start with QingToolbox</strong><small>This takes effect the next time QingToolbox starts.</small></div><button class="q-switch" type="button" role="switch" :aria-checked="store.selectedModule.isStartupEnabled" :disabled="!store.selectedModule.canChangeStartupAuthorization || store.selectedModule.isStartupAuthorizationBusy || !!store.operations[store.selectedModule.id] || store.selectedModule.isBusy" @click="setStartupAuthorization(store.selectedModule, !store.selectedModule.isStartupEnabled)"><span /><em>{{ store.operations[store.selectedModule.id] === 'startupAuthorization' ? (store.selectedModule.isStartupEnabled ? 'Disabling…' : 'Authorizing…') : (store.selectedModule.isStartupEnabled ? 'On' : 'Off') }}</em></button></div><p class="module-startup-status">{{ startupMessage(store.selectedModule.startupAuthorizationState) }}</p></section>
        <div class="wpf-detail-divider" />
        <h3>Module information</h3>
        <dl class="wpf-detail-grid"><div><dt>Runtime</dt><dd>{{ store.selectedModule.runtimeType }}</dd></div><div><dt>Load mode</dt><dd>{{ store.selectedModule.loadMode }}</dd></div><div><dt>Author</dt><dd>{{ store.selectedModule.author }}</dd></div><div><dt>Permissions</dt><dd>{{ store.selectedModule.permissions.length ? store.selectedModule.permissions.join(', ') : 'None declared' }}</dd></div><div><dt>Module ID</dt><dd>{{ store.selectedModule.id }}</dd></div><div><dt>Minimum host version</dt><dd>{{ store.selectedModule.minimumHostVersion }}</dd></div><div><dt>User installed</dt><dd>{{ store.selectedModule.isUserInstalled ? 'Yes' : 'No' }}</dd></div><div><dt>Valid</dt><dd>{{ store.selectedModule.isValid ? 'Yes' : 'No' }}</dd></div></dl>
        <template v-if="store.selectedModule.errors.length"><h3>Issues</h3><ul><li v-for="error in store.selectedModule.errors" :key="error">{{ error }}</li></ul></template>
      </aside>
    </div>
  </QPage>
</template>

<style scoped>
.module-actions { display: flex; flex-wrap: wrap; gap: 8px; margin: 10px 0; }
.module-card-badges { display: flex; flex-wrap: wrap; justify-content: flex-end; gap: 6px; }
.module-card-badges .q-badge { padding: 5px 9px; }
.module-card-meta { display: flex; align-items: center; justify-content: space-between; gap: 12px; margin-top: 13px; color: var(--q-text-2); font-size: 12px; }
.module-card-meta strong { color: var(--q-brand); }
.module-card-meta .issue, .module-operation-blocked { color: var(--q-color-warning, #9a5b00); }
.module-operation-blocked { font-size: 12px; }
.module-detail-actions { margin-bottom: 0; }
.module-startup > p, .module-startup-status { color: var(--q-text-2); font-size: 12px; }
.module-startup .settings-switch-row { margin-top: 8px; }
.module-startup-status { margin-top: 4px; }
@media (max-width: 650px) {
  .module-actions { row-gap: 8px; }
  .module-card-badges { grid-column: 1 / -1; justify-content: flex-start; }
}
</style>
