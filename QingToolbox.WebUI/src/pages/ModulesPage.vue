<script setup lang="ts">
import { computed, inject, onBeforeUnmount, onMounted, watch } from 'vue'
import type { ModuleClient } from '../bridge/clients/ModuleClient'
import { useAppStore } from '../app/store'
import { useModuleStore, type ModuleFilter } from '../app/moduleStore'
import type { ModuleSnapshotItem } from '../contracts/modules'
import { useToastStore } from '../app/toastStore'
import QPage from '../design-system/components/QPage.vue'
import QButton from '../design-system/components/QButton.vue'
import QBadge from '../design-system/components/QBadge.vue'
import QEmptyState from '../design-system/components/QEmptyState.vue'
import QSkeleton from '../design-system/components/QSkeleton.vue'
import QIcon from '../design-system/components/QIcon.vue'
import {
  isLoadedState,
  isNotLoadedState,
  isRunningState,
  summarizeModuleStates,
} from '../modules/moduleStateSummary'
import { useLocalization } from '../localization/localization'
import {
  moduleFilterLabelKey,
  moduleOperationFailureKey,
  moduleOperationLabelKey,
  moduleOperationSuccessKey,
  moduleRuntimeStateKey,
  startupAuthorizationMessageKey,
  type LifecycleModuleOperation,
} from '../modules/modulePresentation'

const client = inject<ModuleClient>('moduleClient')!
const app = useAppStore()
const store = useModuleStore()
const toast = useToastStore()
const { t } = useLocalization()

async function refresh(showToast = false) {
  if (app.bridge !== 'Connected' || store.status === 'loading') return
  store.begin()
  try {
    store.complete(await client.getSnapshot())
    if (showToast) toast.show(t('modules.toast.snapshotRefreshed'), 'success')
  } catch (error) {
    store.fail(error)
    toast.show(t('modules.toast.refreshFailed'), 'error')
  }
}

async function resyncAfterOperationFailure() {
  try {
    store.complete(await client.getSnapshot())
  } catch {
    store.fail(new Error('The host could not confirm the current module state.'))
  }
}

async function operate(module: ModuleSnapshotItem, operation: LifecycleModuleOperation) {
  if (!hostOperationsAvailable.value) return
  if (!store.beginOperation(module.id, operation)) return
  try {
    const snapshot = operation === 'load' ? await client.load(module.id)
      : operation === 'activate' ? await client.activate(module.id)
        : operation === 'open' ? await client.open(module.id)
          : operation === 'deactivate' ? await client.deactivate(module.id)
            : await client.unload(module.id)
    store.complete(snapshot)
    toast.show(t(moduleOperationSuccessKey(operation), { name: module.displayName }), 'success')
  } catch {
    toast.show(t(moduleOperationFailureKey(operation), { name: module.displayName }), 'error')
    await resyncAfterOperationFailure()
  } finally {
    store.endOperation(module.id)
  }
}

const operationLabel = (module: ModuleSnapshotItem, operation: LifecycleModuleOperation) =>
  t(moduleOperationLabelKey(operation, store.operations[module.id] === operation))

async function setStartupAuthorization(module: ModuleSnapshotItem, enabled: boolean) {
  if (!hostOperationsAvailable.value) return
  if (!store.beginOperation(module.id, 'startupAuthorization')) return
  try {
    store.complete(await client.setStartupAuthorization(module.id, enabled))
    toast.show(t(enabled ? 'modules.toast.startupEnabled' : 'modules.toast.startupDisabled', { name: module.displayName }), 'success')
  } catch {
    toast.show(t('modules.toast.startupFailed', { name: module.displayName }), 'error')
    await resyncAfterOperationFailure()
  } finally {
    store.endOperation(module.id)
  }
}

const startupMessage = (state: ModuleSnapshotItem['startupAuthorizationState']) => t(startupAuthorizationMessageKey(state))
const cardCanLoad = (module: ModuleSnapshotItem) => isNotLoadedState(module.runtimeState) && module.canLoad
const cardCanActivate = (module: ModuleSnapshotItem) => isLoadedState(module.runtimeState) && module.canActivate
const cardCanOpen = (module: ModuleSnapshotItem) => (isLoadedState(module.runtimeState) || isRunningState(module.runtimeState)) && module.canOpen && !module.isExecutionBlocked
const runtimeLabel = (module: ModuleSnapshotItem) => {
  if (!module.isValid) return t('moduleState.invalid')
  const key = moduleRuntimeStateKey(module.runtimeState)
  return key ? t(key) : module.runtimeState
}
const issueLabel = (count: number) => count === 0 ? t('modules.card.noIssues')
  : t(count === 1 ? 'modules.card.oneIssue' : 'modules.card.manyIssues', { count })
const openDetails = (moduleId: string) => { store.selectedModuleId = moduleId }
watch(() => app.bridge, bridge => { if (bridge === 'Connected' && store.status === 'idle') void refresh() }, { immediate: true })
const summary = computed(() => summarizeModuleStates(store.modules))
const hasConfirmedSnapshot = computed(() => store.lastUpdatedAt !== null)
const hostOperationsAvailable = computed(() => app.bridge === 'Connected' && store.status === 'ready')
const canRefresh = computed(() => app.bridge === 'Connected' && store.status !== 'loading')
const snapshotStatusMessage = computed(() => {
  if (!hasConfirmedSnapshot.value) return null
  if (app.bridge !== 'Connected') return t('modules.status.disconnected')
  if (store.status === 'error') return t('modules.status.refreshFailedStale')
  if (store.status === 'loading') return t('modules.status.refreshingStale')
  return null
})
const filterValues: ModuleFilter[] = ['all', 'running', 'notLoaded', 'issues', 'invalid']
const tone = (module: { isValid: boolean; runtimeState: string }) => !module.isValid ? 'danger' : module.runtimeState === 'Running' ? 'success' : module.runtimeState === 'NotLoaded' ? 'neutral' : 'info'
const closeOnEscape = (event: KeyboardEvent) => { if (event.key === 'Escape') store.selectedModuleId = null }
onMounted(() => window.addEventListener('keydown', closeOnEscape))
onBeforeUnmount(() => window.removeEventListener('keydown', closeOnEscape))
</script>

<template>
  <QPage class="modules-page">
    <header class="wpf-page-header">
      <div><h1>{{ t('modules.page.title') }}</h1><p>{{ t('modules.page.description') }}</p></div>
      <QButton @click="refresh(true)" :disabled="!canRefresh"><QIcon name="refresh" /> {{ t(store.status === 'loading' ? 'modules.page.refreshing' : 'modules.page.refresh') }}</QButton>
    </header>
    <section class="wpf-status-strip" role="status" aria-live="polite">
      <span v-if="snapshotStatusMessage">{{ snapshotStatusMessage }}</span>
      <span v-else-if="store.status === 'loading'">{{ t('modules.status.refreshing') }}</span>
      <span v-else-if="store.status === 'error'">{{ t('modules.status.unavailable') }}</span>
      <span v-else>{{ t(store.modules.length === 1 ? 'modules.status.foundOne' : 'modules.status.foundMany', { count: store.modules.length }) }}</span>
    </section>
    <section v-if="hasConfirmedSnapshot" class="wpf-module-summary">
      <article><label>{{ t('modules.summary.total') }}</label><strong>{{ summary.total }}</strong></article>
      <article><label>{{ t('modules.summary.valid') }}</label><strong class="success">{{ summary.valid }}</strong></article>
      <article><label>{{ t('modules.summary.issues') }}</label><strong class="warning">{{ summary.issues }}</strong></article>
      <article><label>{{ t('modules.summary.notLoaded') }}</label><strong class="accent">{{ summary.notLoaded }}</strong></article>
      <article><label>{{ t('modules.summary.loaded') }}</label><strong class="info">{{ summary.loaded }}</strong></article>
      <article><label>{{ t('modules.summary.running') }}</label><strong class="success">{{ summary.running }}</strong></article>
    </section>
    <section class="wpf-module-tools">
      <label><span class="sr-only">{{ t('modules.search.label') }}</span><span class="wpf-search-icon"><QIcon name="search" /></span><input v-model="store.searchQuery" type="search" :placeholder="t('modules.search.placeholder')"></label>
      <div class="filters" :aria-label="t('modules.filter.label')"><button v-for="filter in filterValues" :key="filter" :class="{ active: store.stateFilter === filter }" @click="store.stateFilter = filter">{{ t(moduleFilterLabelKey(filter)) }}</button></div>
    </section>
    <div class="wpf-module-workspace" :class="{ 'has-selection': !!store.selectedModule }">
      <section class="wpf-module-list">
        <div v-if="store.status === 'loading' && !hasConfirmedSnapshot" class="wpf-module-stack"><QSkeleton v-for="n in 3" :key="n" /></div>
        <QEmptyState v-else-if="store.status === 'error' && !hasConfirmedSnapshot" :title="t('modules.empty.unavailable')" :description="t('modules.empty.unavailableDescription')"><QButton :disabled="!canRefresh" @click="refresh()">{{ t('modules.empty.retry') }}</QButton></QEmptyState>
        <QEmptyState v-else-if="store.visibleModules.length === 0" :title="t(store.modules.length ? 'modules.empty.noResults' : 'modules.empty.noInstalled')" :description="t(store.modules.length ? 'modules.empty.searchHint' : 'modules.empty.importHint')" />
        <div v-else class="wpf-module-stack">
          <article v-for="module in store.visibleModules" :key="module.id" class="wpf-module-card" :class="{ selected: store.selectedModuleId === module.id }">
            <header>
              <span class="module-icon">{{ module.displayName.slice(0, 1).toUpperCase() }}</span>
              <div><h2>{{ module.displayName }} <small>v{{ module.version }}</small></h2></div>
              <div class="module-card-badges">
                <QBadge :tone="tone(module)">{{ runtimeLabel(module) }}</QBadge>
                <QBadge v-if="module.startupAuthorizationState === 'Enabled'" tone="info">{{ t('modules.card.startsOnLaunch') }}</QBadge>
                <QBadge v-else-if="module.startupAuthorizationState === 'ChangedNeedsConfirmation'" tone="warning">{{ t('modules.card.startupApprovalRequired') }}</QBadge>
              </div>
            </header>
            <p>{{ module.displayDescription }}</p>
            <p v-if="module.isExecutionBlocked" class="module-operation-blocked">{{ t('modules.card.operationsBlocked') }}</p>
            <div class="module-actions module-card-actions">
              <QButton v-if="cardCanLoad(module)" variant="primary" :disabled="!hostOperationsAvailable || !!store.operations[module.id] || module.isBusy" @click="operate(module, 'load')">{{ operationLabel(module, 'load') }}</QButton>
              <QButton v-if="cardCanActivate(module)" variant="primary" :disabled="!hostOperationsAvailable || !!store.operations[module.id] || module.isBusy" @click="operate(module, 'activate')">{{ operationLabel(module, 'activate') }}</QButton>
              <QButton v-if="cardCanOpen(module)" variant="primary" :disabled="!hostOperationsAvailable || !!store.operations[module.id] || module.isBusy" @click="operate(module, 'open')">{{ operationLabel(module, 'open') }}</QButton>
              <QButton class="module-details-button" @click="openDetails(module.id)" @keydown.enter.prevent="openDetails(module.id)" @keydown.space.prevent="openDetails(module.id)">{{ t('modules.card.details') }}</QButton>
            </div>
            <div class="module-card-meta"><span>{{ t('modules.card.runtime') }}: <strong>{{ runtimeLabel(module) }}</strong></span><span :class="{ issue: module.errorCount }">{{ issueLabel(module.errorCount) }}</span></div>
          </article>
        </div>
      </section>
      <aside v-if="store.selectedModule" class="wpf-module-details">
        <button class="wpf-back" :aria-label="t('modules.details.back')" @click="store.selectedModuleId = null"><QIcon name="back" /></button>
        <header><span class="module-icon">{{ store.selectedModule.displayName.slice(0, 1).toUpperCase() }}</span><div><h2>{{ store.selectedModule.displayName }}</h2><small>v{{ store.selectedModule.version }}</small></div></header>
        <QBadge :tone="tone(store.selectedModule)">{{ runtimeLabel(store.selectedModule) }}</QBadge>
        <p>{{ store.selectedModule.displayDescription }}</p>
        <p v-if="store.selectedModule.isExecutionBlocked" class="module-operation-blocked">{{ t('modules.card.operationsBlocked') }}</p>
        <h3>{{ t('modules.details.actions') }}</h3>
        <div class="module-actions module-detail-actions">
          <QButton v-if="store.selectedModule.canLoad" variant="primary" :disabled="!hostOperationsAvailable || !!store.operations[store.selectedModule.id] || store.selectedModule.isBusy" @click="operate(store.selectedModule, 'load')">{{ operationLabel(store.selectedModule, 'load') }}</QButton>
          <QButton v-if="store.selectedModule.canActivate" variant="primary" :disabled="!hostOperationsAvailable || !!store.operations[store.selectedModule.id] || store.selectedModule.isBusy" @click="operate(store.selectedModule, 'activate')">{{ operationLabel(store.selectedModule, 'activate') }}</QButton>
          <QButton v-if="store.selectedModule.canOpen && !store.selectedModule.isExecutionBlocked" variant="primary" :disabled="!hostOperationsAvailable || !!store.operations[store.selectedModule.id] || store.selectedModule.isBusy" @click="operate(store.selectedModule, 'open')">{{ operationLabel(store.selectedModule, 'open') }}</QButton>
          <QButton v-if="store.selectedModule.canDeactivate && !store.selectedModule.isExecutionBlocked" :disabled="!hostOperationsAvailable || !!store.operations[store.selectedModule.id] || store.selectedModule.isBusy" @click="operate(store.selectedModule, 'deactivate')">{{ operationLabel(store.selectedModule, 'deactivate') }}</QButton>
          <QButton v-if="store.selectedModule.canUnload && !store.selectedModule.isExecutionBlocked" :disabled="!hostOperationsAvailable || !!store.operations[store.selectedModule.id] || store.selectedModule.isBusy" @click="operate(store.selectedModule, 'unload')">{{ operationLabel(store.selectedModule, 'unload') }}</QButton>
        </div>
        <div class="wpf-detail-divider" />
        <section class="module-startup"><h3>{{ t('modules.details.startup') }}</h3><p>{{ t('modules.details.startupDescription') }}</p><div class="settings-switch-row"><div><strong>{{ t('modules.details.startWithToolbox') }}</strong><small>{{ t('modules.details.startupNextLaunch') }}</small></div><button class="q-switch" type="button" role="switch" :aria-checked="store.selectedModule.isStartupEnabled" :disabled="!hostOperationsAvailable || !store.selectedModule.canChangeStartupAuthorization || store.selectedModule.isStartupAuthorizationBusy || !!store.operations[store.selectedModule.id] || store.selectedModule.isBusy" @click="setStartupAuthorization(store.selectedModule, !store.selectedModule.isStartupEnabled)"><span /><em>{{ store.operations[store.selectedModule.id] === 'startupAuthorization' ? t(store.selectedModule.isStartupEnabled ? 'modules.startup.disabling' : 'modules.startup.authorizing') : t(store.selectedModule.isStartupEnabled ? 'modules.startup.on' : 'modules.startup.off') }}</em></button></div><p class="module-startup-status">{{ startupMessage(store.selectedModule.startupAuthorizationState) }}</p></section>
        <div class="wpf-detail-divider" />
        <h3>{{ t('modules.details.information') }}</h3>
        <dl class="wpf-detail-grid"><div><dt>{{ t('modules.details.runtime') }}</dt><dd>{{ store.selectedModule.runtimeType }}</dd></div><div><dt>{{ t('modules.details.loadMode') }}</dt><dd>{{ store.selectedModule.loadMode }}</dd></div><div><dt>{{ t('modules.details.author') }}</dt><dd>{{ store.selectedModule.author }}</dd></div><div><dt>{{ t('modules.details.permissions') }}</dt><dd>{{ store.selectedModule.permissions.length ? store.selectedModule.permissions.join(', ') : t('modules.details.noneDeclared') }}</dd></div><div><dt>{{ t('modules.details.moduleId') }}</dt><dd>{{ store.selectedModule.id }}</dd></div><div><dt>{{ t('modules.details.minimumHostVersion') }}</dt><dd>{{ store.selectedModule.minimumHostVersion }}</dd></div><div><dt>{{ t('modules.details.userInstalled') }}</dt><dd>{{ t(store.selectedModule.isUserInstalled ? 'modules.details.yes' : 'modules.details.no') }}</dd></div><div><dt>{{ t('modules.details.valid') }}</dt><dd>{{ t(store.selectedModule.isValid ? 'modules.details.yes' : 'modules.details.no') }}</dd></div></dl>
        <template v-if="store.selectedModule.errors.length"><h3>{{ t('modules.details.issues') }}</h3><ul><li v-for="error in store.selectedModule.errors" :key="error">{{ error }}</li></ul></template>
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
