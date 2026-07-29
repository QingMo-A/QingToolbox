<script setup lang="ts">
import { computed, inject, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
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
  summarizeModuleStates,
} from '../modules/moduleStateSummary'
import { useLocalization } from '../localization/localization'
import type { TranslationKey } from '../localization/messages/en-US'
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
const isImporting = ref(false)
const removeConfirmationModuleId = ref<string|null>(null)
type AnimatedLifecycleSuccess = 'load' | 'unload'
const lifecycleSuccessOperations = reactive(new Map<string, AnimatedLifecycleSuccess>())
const revealingLifecycleActions = reactive(new Set<string>())
const lifecycleFeedbackTimers = new Map<string, number[]>()
const lifecycleSuccessDurationMs = 1400
const lifecycleRevealDurationMs = 260

function clearLifecycleFeedbackTimers(moduleId: string) {
  for (const timer of lifecycleFeedbackTimers.get(moduleId) ?? []) window.clearTimeout(timer)
  lifecycleFeedbackTimers.delete(moduleId)
}

function showLifecycleSuccess(moduleId: string, operation: AnimatedLifecycleSuccess) {
  clearLifecycleFeedbackTimers(moduleId)
  revealingLifecycleActions.delete(moduleId)
  lifecycleSuccessOperations.set(moduleId, operation)
  const timers: number[] = []
  timers.push(window.setTimeout(() => {
    lifecycleSuccessOperations.delete(moduleId)
    revealingLifecycleActions.add(moduleId)
    timers.push(window.setTimeout(() => {
      revealingLifecycleActions.delete(moduleId)
      lifecycleFeedbackTimers.delete(moduleId)
    }, lifecycleRevealDurationMs))
  }, lifecycleSuccessDurationMs))
  lifecycleFeedbackTimers.set(moduleId, timers)
}

async function refresh(showToast = false) {
  if (app.bridge !== 'Connected' || store.status === 'loading' || isImporting.value) return
  store.begin()
  try {
    store.complete(await client.getSnapshot())
    if (showToast) toast.show(t('modules.toast.snapshotRefreshed'), 'success')
  } catch (error) {
    store.fail(error)
    toast.show(t('modules.toast.refreshFailed'), 'error')
  }
}

async function importModule() {
  if (!hostOperationsAvailable.value || isImporting.value) return
  isImporting.value = true
  try {
    const result = await client.importModule()
    if (result.disposition === 'Cancelled') return
    const imported = result.snapshot.modules.find(module => module.id === result.importedModuleId)
    if (!imported) throw new Error('Imported module is missing from the host snapshot.')
    store.complete(result.snapshot)
    store.selectedModuleId = imported.id
    if (!store.visibleModules.some(module => module.id === imported.id)) {
      store.searchQuery = ''
      store.stateFilter = 'all'
    }
    toast.show(t('modules.toast.imported', { name: imported.displayName }), 'success')
  } catch {
    toast.show(t('modules.toast.importFailed'), 'error')
    await resyncAfterOperationFailure()
  } finally {
    isImporting.value = false
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
    if (operation === 'load' || operation === 'unload') showLifecycleSuccess(module.id, operation)
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

async function openModuleDirectory(module: ModuleSnapshotItem) {
  if (!hostOperationsAvailable.value || !store.beginOperation(module.id, 'openDirectory')) return
  try {
    await client.openDirectory(module.id)
    toast.show(t('modules.management.opened'), 'success')
  } catch {
    toast.show(t('modules.management.openFailed'), 'error')
  } finally {
    store.endOperation(module.id)
  }
}

async function removeModule(module: ModuleSnapshotItem) {
  if (!hostOperationsAvailable.value || !module.canRemove || !store.beginOperation(module.id, 'remove')) return
  try {
    const result = await client.remove(module.id)
    store.complete(result.snapshot)
    store.selectedModuleId = null
    removeConfirmationModuleId.value = null
    toast.show(t(result.disposition === 'SucceededWithWarning'
      ? 'modules.management.removeWarning'
      : 'modules.management.removed'), result.disposition === 'SucceededWithWarning' ? 'warning' : 'success')
  } catch {
    toast.show(t('modules.management.removeFailed'), 'error')
    await resyncAfterOperationFailure()
  } finally {
    store.endOperation(module.id)
  }
}

const updateStatusKey = (status: ModuleSnapshotItem['updateStatus']): TranslationKey => `modules.update.status.${status}`
const downloadStatusKey = (status: ModuleSnapshotItem['downloadStatus']): TranslationKey => `modules.update.download.${status}`
const updateTone = (module: ModuleSnapshotItem) => {
  if (module.downloadStatus === 'Verified' || module.downloadStatus === 'AlreadyVerified' || module.updateStatus === 'UpToDate') return 'success'
  if (['SourceUnavailable','SourceInvalid','InvalidLocalVersion'].includes(module.updateStatus) ||
      ['SizeMismatch','HashMismatch','SourceUnavailable','SourceInvalid','UntrustedRedirect','StorageUnavailable','Failed','TransferTimedOut'].includes(module.downloadStatus)) return 'danger'
  if (['HostUpdateRequired','ModuleApiIncompatible','HostVersionIncompatible','DisabledByEnvironment'].includes(module.updateStatus) ||
      ['MetadataChanged','MetadataStale','Cancelled','DisabledByEnvironment'].includes(module.downloadStatus)) return 'warning'
  return module.updateStatus === 'UpdateAvailable' ? 'info' : 'neutral'
}
const isVerifiedUpdate = (module: ModuleSnapshotItem) => module.downloadStatus === 'Verified' || module.downloadStatus === 'AlreadyVerified'
const hasDownloadStatus = (module: ModuleSnapshotItem) => module.downloadStatus !== 'NotDownloaded'
const downloadPercentage = (module: ModuleSnapshotItem) => module.downloadExpectedBytes > 0
  ? Math.max(0, Math.min(100, module.downloadBytesReceived * 100 / module.downloadExpectedBytes))
  : null
const formatBytes = (bytes: number) => {
  const value = Math.max(0, bytes)
  if (value < 1024) return `${value.toFixed(0)} B`
  const units = ['KB', 'MB', 'GB']
  let scaled = value / 1024
  let unit = units[0]
  for (let index = 1; index < units.length && scaled >= 1024; index++) {
    scaled /= 1024
    unit = units[index]
  }
  return `${scaled < 10 ? scaled.toFixed(1) : scaled.toFixed(0)} ${unit}`
}
const downloadProgressText = (module: ModuleSnapshotItem) => {
  const percentage = downloadPercentage(module)
  if (percentage !== null) return `${formatBytes(module.downloadBytesReceived)} / ${formatBytes(module.downloadExpectedBytes)} · ${percentage.toFixed(0)}%`
  return module.downloadBytesReceived > 0 ? formatBytes(module.downloadBytesReceived) : null
}
const overallUpdateStatusKey = (module: ModuleSnapshotItem): TranslationKey => {
  if (isVerifiedUpdate(module)) return 'modules.update.verifiedBadge'
  if (hasDownloadStatus(module)) return downloadStatusKey(module.downloadStatus)
  return updateStatusKey(module.updateStatus)
}
const updateConditionDetailKey = (module: ModuleSnapshotItem): TranslationKey | null => {
  if (['HostUpdateRequired','ModuleApiIncompatible','HostVersionIncompatible'].includes(module.updateStatus)) return 'modules.update.compatibilityDetail'
  if (['SourceUnavailable','SourceInvalid','InvalidLocalVersion'].includes(module.updateStatus)) return 'modules.update.sourceDetail'
  if (module.updateStatus === 'DisabledByEnvironment') return 'modules.update.environmentDetail'
  return null
}

const updateConditionDetail = (module: ModuleSnapshotItem) => {
  const key = updateConditionDetailKey(module)
  return key ? t(key) : ''
}
const downloadToast = (status: ModuleSnapshotItem['downloadStatus']): { key: TranslationKey; kind: 'info'|'success'|'warning'|'error' } => {
  if (status === 'Verified' || status === 'AlreadyVerified') return { key: 'modules.update.toast.verified', kind: 'success' }
  if (status === 'SizeMismatch' || status === 'HashMismatch') return { key: 'modules.update.toast.validationFailed', kind: 'error' }
  if (status === 'SourceUnavailable' || status === 'SourceInvalid' || status === 'UntrustedRedirect') return { key: 'modules.update.toast.sourceRejected', kind: 'error' }
  if (status === 'StorageUnavailable') return { key: 'modules.update.toast.storageFailed', kind: 'error' }
  if (status === 'TransferTimedOut') return { key: 'modules.update.toast.timedOut', kind: 'error' }
  if (status === 'MetadataChanged' || status === 'MetadataStale') return { key: 'modules.update.toast.metadataChanged', kind: 'warning' }
  if (status === 'Cancelled') return { key: 'modules.update.toast.cancelled', kind: 'warning' }
  if (status === 'DisabledByEnvironment') return { key: 'modules.update.toast.disabled', kind: 'warning' }
  if (status === 'Failed') return { key: 'modules.update.toast.downloadFailed', kind: 'error' }
  return { key: 'modules.update.toast.downloadFinished', kind: 'warning' }
}

async function checkModuleUpdate(module: ModuleSnapshotItem) {
  if (!hostOperationsAvailable.value || !module.canCheckForUpdate || !store.beginOperation(module.id, 'checkUpdate')) return
  try {
    const snapshot = await client.checkUpdate(module.id)
    store.complete(snapshot)
    const current = snapshot.modules.find(item => item.id === module.id)
    if (!current) throw new Error('Updated module is missing from the host snapshot.')
    const kind = current.updateStatus === 'UpdateAvailable' ? 'warning'
      : current.updateStatus === 'UpToDate' ? 'success'
        : ['HostUpdateRequired','ModuleApiIncompatible','HostVersionIncompatible'].includes(current.updateStatus) ? 'warning' : 'info'
    toast.show(t(current.updateStatus === 'UpdateAvailable' ? 'modules.update.toast.available'
      : current.updateStatus === 'UpToDate' ? 'modules.update.toast.upToDate'
        : 'modules.update.toast.checked'), kind)
  } catch {
    toast.show(t('modules.update.toast.checkFailed'), 'error')
    await resyncAfterOperationFailure()
  } finally {
    store.endOperation(module.id)
  }
}

async function downloadModuleUpdate(module: ModuleSnapshotItem) {
  if (!hostOperationsAvailable.value || !module.canDownloadUpdate || !store.beginOperation(module.id, 'downloadUpdate')) return
  try {
    const snapshot = await client.downloadUpdate(module.id)
    store.complete(snapshot)
    const current = snapshot.modules.find(item => item.id === module.id)
    if (!current) throw new Error('Updated module is missing from the host snapshot.')
    const result = downloadToast(current.downloadStatus)
    toast.show(t(result.key), result.kind)
  } catch {
    toast.show(t('modules.update.toast.downloadFailed'), 'error')
    await resyncAfterOperationFailure()
  } finally {
    store.endOperation(module.id)
  }
}

const startupMessage = (state: ModuleSnapshotItem['startupAuthorizationState']) => t(startupAuthorizationMessageKey(state))
function availableLifecycleOperations(module: ModuleSnapshotItem): LifecycleModuleOperation[] {
  const operations: LifecycleModuleOperation[] = []
  if (module.canLoad) operations.push('load')
  if (module.canOpen && !module.isExecutionBlocked) operations.push('open')
  if (module.canActivate) operations.push('activate')
  if (module.canDeactivate && !module.isExecutionBlocked) operations.push('deactivate')
  if (module.canUnload && !module.isExecutionBlocked) operations.push('unload')
  return operations
}
const isPrimaryLifecycleOperation = (operation: LifecycleModuleOperation) =>
  operation === 'load' || operation === 'activate' || operation === 'open'
const runtimeLabel = (module: ModuleSnapshotItem) => {
  if (!module.isValid) return t('moduleState.invalid')
  const key = moduleRuntimeStateKey(module.runtimeState)
  return key ? t(key) : module.runtimeState
}
const issueLabel = (count: number) => count === 0 ? t('modules.card.noIssues')
  : t(count === 1 ? 'modules.card.oneIssue' : 'modules.card.manyIssues', { count })
const openDetails = (moduleId: string) => { store.selectedModuleId = moduleId }
const openDetailsFromCard = (event: MouseEvent, moduleId: string) => {
  const target = event.target
  if (target instanceof Element && target.closest('button, a, input, select, textarea, [role="button"], [role="switch"]')) return
  openDetails(moduleId)
}
watch(() => app.bridge, bridge => { if (bridge === 'Connected' && store.status === 'idle') void refresh() }, { immediate: true })
watch(() => store.selectedModuleId, () => { removeConfirmationModuleId.value = null })
const summary = computed(() => summarizeModuleStates(store.modules))
const hasConfirmedSnapshot = computed(() => store.lastUpdatedAt !== null)
const hostOperationsAvailable = computed(() => app.bridge === 'Connected' && store.status === 'ready')
const canRefresh = computed(() => app.bridge === 'Connected' && store.status !== 'loading' && !isImporting.value)
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
onBeforeUnmount(() => {
  removeConfirmationModuleId.value = null
  window.removeEventListener('keydown', closeOnEscape)
  for (const timers of lifecycleFeedbackTimers.values())
    for (const timer of timers) window.clearTimeout(timer)
  lifecycleFeedbackTimers.clear()
})
</script>

<template>
  <QPage class="modules-page">
    <header class="wpf-page-header">
      <div><h1>{{ t('modules.page.title') }}</h1><p>{{ t('modules.page.description') }}</p></div>
      <div class="module-page-actions">
        <QButton class="module-import-button" variant="primary" :aria-busy="isImporting" @click="importModule" :disabled="!hostOperationsAvailable || isImporting"><span v-if="isImporting" class="module-operation-spinner" aria-hidden="true" /><QIcon v-else name="import" /> {{ t(isImporting ? 'modules.page.importing' : 'modules.page.import') }}</QButton>
        <QButton class="module-refresh-button" variant="secondary" @click="refresh(true)" :disabled="!canRefresh"><QIcon name="refresh" /> {{ t(store.status === 'loading' ? 'modules.page.refreshing' : 'modules.page.refresh') }}</QButton>
      </div>
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
        <QEmptyState v-else-if="store.visibleModules.length === 0" :title="t(store.modules.length ? 'modules.empty.noResults' : 'modules.empty.noInstalled')" :description="t(store.modules.length ? 'modules.empty.searchHint' : 'modules.empty.importHint')"><QButton v-if="store.modules.length === 0" class="module-import-button" variant="primary" :aria-busy="isImporting" :disabled="!hostOperationsAvailable || isImporting" @click="importModule"><span v-if="isImporting" class="module-operation-spinner" aria-hidden="true" /><QIcon v-else name="import" /> {{ t(isImporting ? 'modules.page.importing' : 'modules.page.import') }}</QButton></QEmptyState>
        <div v-else class="wpf-module-stack">
          <article v-for="module in store.visibleModules" :key="module.id" class="wpf-module-card" :class="{ selected: store.selectedModuleId === module.id }" tabindex="0" :aria-label="`${t('modules.card.details')}: ${module.displayName}`" @click="openDetailsFromCard($event, module.id)" @keydown.enter.self.prevent="openDetails(module.id)" @keydown.space.self.prevent="openDetails(module.id)">
            <header>
              <span class="module-icon">{{ module.displayName.slice(0, 1).toUpperCase() }}</span>
              <div><h2>{{ module.displayName }} <small>v{{ module.version }}</small></h2></div>
              <div class="module-card-badges">
                <QBadge :tone="tone(module)">{{ runtimeLabel(module) }}</QBadge>
                <QBadge v-if="module.startupAuthorizationState === 'Enabled'" tone="info">{{ t('modules.card.startsOnLaunch') }}</QBadge>
                <QBadge v-else-if="module.startupAuthorizationState === 'ChangedNeedsConfirmation'" tone="warning">{{ t('modules.card.startupApprovalRequired') }}</QBadge>
                <QBadge v-if="isVerifiedUpdate(module)" tone="success">{{ t('modules.update.verifiedBadge') }}</QBadge>
                <QBadge v-else-if="module.updateStatus === 'UpdateAvailable'" tone="info">{{ t('modules.update.availableBadge') }}</QBadge>
              </div>
            </header>
            <p>{{ module.displayDescription }}</p>
            <p v-if="module.isExecutionBlocked" class="module-operation-blocked">{{ t('modules.card.operationsBlocked') }}</p>
            <div class="module-actions module-card-actions">
              <div class="module-card-lifecycle-actions" :class="{ 'is-revealing': revealingLifecycleActions.has(module.id) }">
                <span v-if="lifecycleSuccessOperations.has(module.id)" class="module-lifecycle-success" :class="{ 'is-unload': lifecycleSuccessOperations.get(module.id) === 'unload' }" aria-hidden="true">
                  <svg viewBox="0 0 36 36" width="34" height="34">
                    <circle class="module-lifecycle-success-ring" cx="18" cy="18" r="14" pathLength="100" />
                    <path class="module-lifecycle-success-check" d="M10.5 18.5 15.7 23.5 25.8 12.8" pathLength="100" />
                  </svg>
                </span>
                <template v-else>
                  <QButton v-for="operation in availableLifecycleOperations(module)" :key="operation" :variant="isPrimaryLifecycleOperation(operation) ? 'primary' : 'secondary'" :disabled="!hostOperationsAvailable || !!store.operations[module.id] || module.isBusy" @click="operate(module, operation)"><span v-if="operation === 'load' && store.operations[module.id] === 'load'" class="module-operation-spinner" aria-hidden="true" />{{ operationLabel(module, operation) }}</QButton>
                </template>
              </div>
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
        <section class="module-startup"><h3>{{ t('modules.details.startup') }}</h3><p>{{ t('modules.details.startupDescription') }}</p><div class="settings-switch-row"><div><strong>{{ t('modules.details.startWithToolbox') }}</strong><small>{{ t('modules.details.startupNextLaunch') }}</small></div><button class="q-switch" type="button" role="switch" :aria-checked="store.selectedModule.isStartupEnabled" :aria-busy="store.operations[store.selectedModule.id] === 'startupAuthorization'" :disabled="!hostOperationsAvailable || !store.selectedModule.canChangeStartupAuthorization || store.selectedModule.isStartupAuthorizationBusy || !!store.operations[store.selectedModule.id] || store.selectedModule.isBusy" @click="setStartupAuthorization(store.selectedModule, !store.selectedModule.isStartupEnabled)"><span /><em>{{ t(store.selectedModule.isStartupEnabled ? 'modules.startup.on' : 'modules.startup.off') }}</em></button></div><p class="module-startup-status">{{ startupMessage(store.selectedModule.startupAuthorizationState) }}</p></section>
        <div class="wpf-detail-divider" />
        <h3>{{ t('modules.details.information') }}</h3>
        <dl class="wpf-detail-grid"><div><dt>{{ t('modules.details.runtime') }}</dt><dd>{{ store.selectedModule.runtimeType }}</dd></div><div><dt>{{ t('modules.details.loadMode') }}</dt><dd>{{ store.selectedModule.loadMode }}</dd></div><div><dt>{{ t('modules.details.author') }}</dt><dd>{{ store.selectedModule.author }}</dd></div><div><dt>{{ t('modules.details.permissions') }}</dt><dd>{{ store.selectedModule.permissions.length ? store.selectedModule.permissions.join(', ') : t('modules.details.noneDeclared') }}</dd></div><div><dt>{{ t('modules.details.moduleId') }}</dt><dd>{{ store.selectedModule.id }}</dd></div><div><dt>{{ t('modules.details.minimumHostVersion') }}</dt><dd>{{ store.selectedModule.minimumHostVersion }}</dd></div><div><dt>{{ t('modules.details.userInstalled') }}</dt><dd>{{ t(store.selectedModule.isUserInstalled ? 'modules.details.yes' : 'modules.details.no') }}</dd></div><div><dt>{{ t('modules.details.valid') }}</dt><dd>{{ t(store.selectedModule.isValid ? 'modules.details.yes' : 'modules.details.no') }}</dd></div></dl>
        <template v-if="store.selectedModule.errors.length"><h3>{{ t('modules.details.issues') }}</h3><ul><li v-for="error in store.selectedModule.errors" :key="error">{{ error }}</li></ul></template>
        <div class="wpf-detail-divider" />
        <section class="module-update">
          <h3>{{ t('modules.update.title') }}</h3>
          <p>{{ t('modules.update.description') }}</p>
          <div class="module-update-overview">
            <div class="module-update-versions">
              <div><span>{{ t('modules.update.currentVersion') }}</span><strong>{{ store.selectedModule.version }}</strong></div>
              <template v-if="store.selectedModule.targetVersion"><span class="module-update-version-arrow" aria-hidden="true">→</span><div><span>{{ t('modules.update.latestVersion') }}</span><strong>{{ store.selectedModule.targetVersion }}</strong></div></template>
            </div>
            <div class="module-update-overall"><span>{{ t('modules.update.statusLabel') }}</span><QBadge :tone="updateTone(store.selectedModule)">{{ t(overallUpdateStatusKey(store.selectedModule)) }}</QBadge></div>
          </div>
          <div v-if="isVerifiedUpdate(store.selectedModule)" class="module-update-detail module-update-verified" role="status"><QIcon name="statusSuccess" /><div><strong>{{ t('modules.update.verifiedTitle') }}</strong><p>{{ t('modules.update.notInstalled') }}</p></div></div>
          <div v-else-if="hasDownloadStatus(store.selectedModule)" class="module-update-detail module-download-status" :class="`is-${updateTone(store.selectedModule)}`" role="status">
            <QIcon :name="updateTone(store.selectedModule) === 'danger' ? 'statusDanger' : updateTone(store.selectedModule) === 'warning' ? 'statusWarning' : 'statusInfo'" />
            <div><strong>{{ t(downloadStatusKey(store.selectedModule.downloadStatus)) }}</strong><span v-if="store.selectedModule.isDownloadActive && downloadProgressText(store.selectedModule)">{{ downloadProgressText(store.selectedModule) }}</span><span v-else-if="store.operations[store.selectedModule.id] === 'downloadUpdate'">{{ t('modules.update.downloading') }}</span></div>
          </div>
          <div v-else-if="store.selectedModule.releaseNotes" class="module-update-detail module-update-notes" tabindex="0"><div><strong>{{ t('modules.update.releaseNotes') }}</strong><p>{{ store.selectedModule.releaseNotes }}</p></div></div>
          <div v-else-if="updateConditionDetailKey(store.selectedModule)" class="module-update-detail module-update-condition" :class="`is-${updateTone(store.selectedModule)}`" role="status"><QIcon :name="updateTone(store.selectedModule) === 'danger' ? 'statusDanger' : 'statusWarning'" /><div><strong>{{ t(updateStatusKey(store.selectedModule.updateStatus)) }}</strong><p>{{ updateConditionDetail(store.selectedModule) }}</p></div></div>
          <div class="module-update-actions">
            <QButton class="module-check-update" variant="secondary" :aria-busy="store.operations[store.selectedModule.id] === 'checkUpdate' || store.selectedModule.isUpdateCheckBusy" :disabled="!hostOperationsAvailable || !store.selectedModule.canCheckForUpdate || !!store.operations[store.selectedModule.id] || store.selectedModule.isBusy || store.selectedModule.isUpdateCheckBusy" @click="checkModuleUpdate(store.selectedModule)"><span v-if="store.operations[store.selectedModule.id] === 'checkUpdate' || store.selectedModule.isUpdateCheckBusy" class="module-operation-spinner" aria-hidden="true" /><QIcon v-else name="refresh" />{{ t(store.operations[store.selectedModule.id] === 'checkUpdate' || store.selectedModule.isUpdateCheckBusy ? 'modules.update.checking' : 'modules.update.check') }}</QButton>
            <QButton v-if="(store.selectedModule.canDownloadUpdate || store.selectedModule.isDownloadActive) && !isVerifiedUpdate(store.selectedModule)" class="module-download-update" variant="primary" :aria-busy="store.operations[store.selectedModule.id] === 'downloadUpdate' || store.selectedModule.isDownloadActive" :disabled="!hostOperationsAvailable || !!store.operations[store.selectedModule.id] || store.selectedModule.isBusy || store.selectedModule.isDownloadActive" @click="downloadModuleUpdate(store.selectedModule)"><span v-if="store.operations[store.selectedModule.id] === 'downloadUpdate' || store.selectedModule.isDownloadActive" class="module-operation-spinner" aria-hidden="true" /><QIcon v-else name="download" />{{ t(store.operations[store.selectedModule.id] === 'downloadUpdate' || store.selectedModule.isDownloadActive ? 'modules.update.downloading' : 'modules.update.download') }}</QButton>
          </div>
        </section>
        <div class="wpf-detail-divider" />
        <section class="module-management">
          <h3>{{ t('modules.management.title') }}</h3>
          <p>{{ t('modules.management.description') }}</p>
          <div class="module-management-actions">
            <QButton class="module-open-directory" variant="secondary" :aria-busy="store.operations[store.selectedModule.id] === 'openDirectory'" :disabled="!hostOperationsAvailable || !!store.operations[store.selectedModule.id] || store.selectedModule.isBusy" @click="openModuleDirectory(store.selectedModule)"><span v-if="store.operations[store.selectedModule.id] === 'openDirectory'" class="module-operation-spinner" aria-hidden="true" /><QIcon v-else name="folder" />{{ t(store.operations[store.selectedModule.id] === 'openDirectory' ? 'modules.management.opening' : 'modules.management.openFolder') }}</QButton>
            <QButton v-if="store.selectedModule.isUserInstalled" class="module-remove-entry" variant="ghost" :aria-expanded="removeConfirmationModuleId === store.selectedModule.id" aria-controls="module-remove-confirmation" :disabled="!hostOperationsAvailable || !store.selectedModule.canRemove || !!store.operations[store.selectedModule.id] || store.selectedModule.isBusy" @click="removeConfirmationModuleId = store.selectedModule.id"><QIcon name="remove" />{{ t('modules.management.removeModule') }}</QButton>
          </div>
          <div v-if="removeConfirmationModuleId === store.selectedModule.id" id="module-remove-confirmation" class="module-remove-confirmation" role="group" aria-labelledby="module-remove-confirmation-title">
            <strong id="module-remove-confirmation-title">{{ t('modules.management.confirmTitle', { name: store.selectedModule.displayName }) }}</strong>
            <p>{{ t('modules.management.confirmDescription') }}</p>
            <small>{{ t('modules.management.reimportHint') }}</small>
            <div>
              <QButton variant="secondary" :disabled="store.operations[store.selectedModule.id] === 'remove'" @click="removeConfirmationModuleId = null">{{ t('modules.management.cancel') }}</QButton>
              <QButton class="module-remove-confirm" variant="secondary" :aria-busy="store.operations[store.selectedModule.id] === 'remove'" :disabled="!store.selectedModule.canRemove || store.operations[store.selectedModule.id] === 'remove'" @click="removeModule(store.selectedModule)"><span v-if="store.operations[store.selectedModule.id] === 'remove'" class="module-operation-spinner" aria-hidden="true" /><QIcon v-else name="remove" />{{ t(store.operations[store.selectedModule.id] === 'remove' ? 'modules.management.removing' : 'modules.management.confirmRemove') }}</QButton>
            </div>
          </div>
        </section>
      </aside>
    </div>
  </QPage>
</template>

<style scoped>
.module-page-actions { display: flex; flex-wrap: wrap; justify-content: flex-end; gap: 8px; min-width: 0; }
.module-page-actions .q-button { flex: 0 0 auto; margin-top: 0; white-space: nowrap; }
.module-import-button { min-width: 136px; gap: 7px; }
.module-import-button.is-primary { border-color: var(--q-brand); background: var(--q-brand); color: #fff; }
.module-import-button.is-primary:hover:not(:disabled) { border-color: color-mix(in srgb, var(--q-brand) 82%, #000); background: color-mix(in srgb, var(--q-brand) 88%, #000); color: #fff; }
.module-actions { display: flex; flex-wrap: wrap; gap: 8px; margin: 10px 0; }
.module-card-actions { align-items: center; min-height: 38px; }
.module-card-lifecycle-actions { display: flex; flex: 1 1 auto; flex-wrap: wrap; gap: 8px; min-width: 0; }
.module-card-actions .q-button { white-space: nowrap; }
.module-operation-spinner { width: 14px; height: 14px; margin-inline-end: 7px; border: 2px solid currentColor; border-right-color: transparent; border-radius: 50%; animation: module-operation-spin 650ms linear infinite; }
.module-lifecycle-success { display: grid; place-items: center; width: 66px; height: 38px; color: var(--q-success); animation: module-lifecycle-success-presence 1400ms cubic-bezier(.2,.75,.25,1) both; }
.module-lifecycle-success.is-unload { color: var(--q-danger); }
.module-lifecycle-success svg { display: block; overflow: visible; }
.module-lifecycle-success-ring,.module-lifecycle-success-check { fill: none; stroke: currentColor; stroke-dasharray: 100; stroke-dashoffset: 100; }
.module-lifecycle-success-ring { stroke-width: 2.1; stroke-dashoffset: -100; transform: rotate(-90deg); transform-box: fill-box; transform-origin: center; animation: module-lifecycle-ring-draw 680ms cubic-bezier(.35,.05,.2,1) 100ms forwards; }
.module-lifecycle-success-check { stroke-width: 3.2; stroke-linecap: round; stroke-linejoin: round; animation: module-lifecycle-check-draw 360ms cubic-bezier(.25,.8,.3,1) 100ms forwards; }
.module-card-lifecycle-actions.is-revealing .q-button { animation: module-lifecycle-actions-reveal 220ms cubic-bezier(.2,.75,.25,1) both; }
.module-card-lifecycle-actions.is-revealing .q-button:nth-child(2) { animation-delay: 30ms; }
.module-card-lifecycle-actions.is-revealing .q-button:nth-child(3) { animation-delay: 60ms; }
.module-card-lifecycle-actions .q-button.is-primary { border-color: var(--q-brand); background: var(--q-brand); color: #fff; }
.module-card-lifecycle-actions .q-button.is-primary:hover { border-color: color-mix(in srgb, var(--q-brand) 82%, #000); background: color-mix(in srgb, var(--q-brand) 88%, #000); color: #fff; }
.module-card-badges { display: flex; flex-wrap: wrap; justify-content: flex-end; gap: 6px; }
.module-card-badges .q-badge { padding: 5px 9px; }
.module-card-meta { display: flex; align-items: center; justify-content: space-between; gap: 12px; margin-top: 13px; color: var(--q-text-2); font-size: 12px; }
.module-card-meta strong { color: var(--q-brand); }
.module-card-meta .issue, .module-operation-blocked { color: var(--q-color-warning, #9a5b00); }
.module-operation-blocked { font-size: 12px; }
.module-startup > p, .module-startup-status { color: var(--q-text-2); font-size: 12px; }
.module-startup .settings-switch-row { margin-top: 8px; }
.module-startup-status { margin-top: 4px; }
.module-update>p { margin: -7px 0 12px; color: var(--q-text-2); font-size: 12px; line-height: 1.45; }
.module-update-overview { display: flex; align-items: flex-end; justify-content: space-between; gap: 12px; }
.module-update-versions { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; min-width: 0; }
.module-update-versions>div { min-width: 78px; }
.module-update-versions span,.module-update-versions strong,.module-update-overall>span { display: block; }
.module-update-versions span,.module-update-overall>span { margin-bottom: 5px; color: var(--q-text-2); font-size: 11px; }
.module-update-version-arrow { color: var(--q-text-3); font-size: 14px; }
.module-update-overall { flex: 0 0 auto; text-align: end; }
.module-update-detail { display: flex; align-items: flex-start; gap: 9px; margin-top: 12px; padding: 10px 12px; border: 1px solid var(--q-border); border-radius: 10px; background: var(--q-surface-soft); }
.module-update-detail>.q-icon { flex: 0 0 18px; width: 18px; height: 18px; margin-top: 1px; }
.module-update-detail>div { min-width: 0; }
.module-update-detail p,.module-update-detail span { display: block; margin: 4px 0 0; color: var(--q-text-2); font-size: 12px; line-height: 1.45; }
.module-update-notes { max-height: 132px; overflow-y: auto; scrollbar-gutter: stable; }
.module-update-notes p { white-space: pre-wrap; overflow-wrap: anywhere; user-select: text; }
.module-download-status.is-danger,.module-update-condition.is-danger { border-color: color-mix(in srgb,var(--q-danger) 28%,var(--q-border)); background: color-mix(in srgb,var(--q-danger) 5%,var(--q-surface)); color: var(--q-danger); }
.module-download-status.is-warning,.module-update-condition.is-warning { border-color: color-mix(in srgb,var(--q-warning) 28%,var(--q-border)); background: color-mix(in srgb,var(--q-warning) 5%,var(--q-surface)); color: var(--q-warning); }
.module-update-verified { border-color: color-mix(in srgb,var(--q-success) 28%,var(--q-border)); background: color-mix(in srgb,var(--q-success) 6%,var(--q-surface)); color: var(--q-success); }
.module-update-actions { display: flex; flex-wrap: wrap; gap: 8px; margin-top: 12px; }
.module-update-actions .q-button { gap: 7px; white-space: nowrap; }
.module-update-actions .module-operation-spinner { flex: 0 0 14px; margin-inline-end: 0; }
.module-check-update { min-width: 132px; }
.module-download-update { min-width: 184px; }
.module-download-update.is-primary { border-color: var(--q-brand); background: var(--q-brand); color: #fff; }
.module-management>p { margin: -7px 0 12px; color: var(--q-text-2); font-size: 12px; }
.module-management-actions { display: flex; flex-wrap: wrap; gap: 8px; }
.module-management-actions .q-button { gap: 7px; white-space: nowrap; }
.module-management .module-operation-spinner { flex: 0 0 14px; margin-inline-end: 0; }
.module-open-directory { min-width: 144px; }
.module-remove-entry { color: var(--q-text-2); }
.module-remove-entry:hover:not(:disabled),.module-remove-entry:focus-visible:not(:disabled) { border-color: color-mix(in srgb,var(--q-danger) 45%,var(--q-border)); background: color-mix(in srgb,var(--q-danger) 7%,var(--q-surface)); color: var(--q-danger); }
.module-remove-entry:disabled { color: var(--q-text-3); }
.module-remove-confirmation { margin-top: 12px; padding: 12px 14px; border: 1px solid color-mix(in srgb,var(--q-danger) 32%,var(--q-border)); border-radius: 11px; background: color-mix(in srgb,var(--q-danger) 6%,var(--q-surface)); animation: module-remove-confirmation-enter 160ms cubic-bezier(.2,.75,.25,1) both; }
.module-remove-confirmation strong,.module-remove-confirmation small { display: block; }
.module-remove-confirmation p { margin: 5px 0; color: var(--q-text-2); font-size: 12px; line-height: 1.45; }
.module-remove-confirmation small { color: var(--q-text-3); }
.module-remove-confirmation>div { display: flex; flex-wrap: wrap; justify-content: flex-end; gap: 8px; margin-top: 12px; }
.module-remove-confirm { min-width: 130px; border-color: var(--q-danger); background: var(--q-danger); color: #fff; }
.module-remove-confirm:hover:not(:disabled) { border-color: color-mix(in srgb,var(--q-danger) 82%,#000); background: color-mix(in srgb,var(--q-danger) 88%,#000); color: #fff; }
@keyframes module-operation-spin { to { transform: rotate(360deg); } }
@keyframes module-remove-confirmation-enter { from { opacity: 0; transform: translateY(-3px); } to { opacity: 1; transform: translateY(0); } }
@keyframes module-lifecycle-ring-draw { to { stroke-dashoffset: 0; } }
@keyframes module-lifecycle-check-draw { to { stroke-dashoffset: 0; } }
@keyframes module-lifecycle-success-presence {
  0% { opacity: 0; transform: scale(.88); }
  10% { opacity: 1; transform: scale(1.04); }
  18%, 78% { opacity: 1; transform: scale(1); }
  100% { opacity: 0; transform: scale(.72); }
}
@keyframes module-lifecycle-actions-reveal {
  from { opacity: 0; transform: translateY(4px) scale(.98); }
  to { opacity: 1; transform: translateY(0) scale(1); }
}
@media (prefers-reduced-motion: reduce) {
  .module-operation-spinner { animation-duration: 1200ms; }
  .module-lifecycle-success { animation-name: module-lifecycle-success-reduced; }
  .module-lifecycle-success-ring,.module-lifecycle-success-check { stroke-dashoffset: 0; animation: none; }
  .module-card-lifecycle-actions.is-revealing .q-button { animation: none; }
  .module-remove-confirmation { animation: none; }
}
@keyframes module-lifecycle-success-reduced { 0%, 82% { opacity: 1; } 100% { opacity: 0; } }
@media (max-width: 650px) {
  .module-page-actions { justify-self: start; justify-content: flex-start; width: 100%; }
  .module-actions { row-gap: 8px; }
  .module-card-lifecycle-actions { flex-basis: 100%; }
  .module-card-badges { grid-column: 1 / -1; justify-content: flex-start; }
}
</style>
