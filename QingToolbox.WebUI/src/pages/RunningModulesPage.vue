<script setup lang="ts">
import { computed, inject, watch } from 'vue'
import { useRouter } from 'vue-router'
import type { ModuleClient } from '../bridge/clients/ModuleClient'
import type { ModuleSnapshotItem } from '../contracts/modules'
import { useAppStore } from '../app/store'
import { useModuleStore } from '../app/moduleStore'
import { useToastStore } from '../app/toastStore'
import QPage from '../design-system/components/QPage.vue'
import QButton from '../design-system/components/QButton.vue'
import QIcon from '../design-system/components/QIcon.vue'
import QSkeleton from '../design-system/components/QSkeleton.vue'
import { useLocalization } from '../localization/localization'
import {
  moduleOperationFailureKey, moduleOperationLabelKey, moduleOperationSuccessKey,
  moduleRuntimeStateKey, type LifecycleModuleOperation,
} from '../modules/modulePresentation'

const client = inject<ModuleClient>('moduleClient')!
const app = useAppStore()
const modules = useModuleStore()
const toast = useToastStore()
const router = useRouter()
const { t } = useLocalization()
async function refresh() {
  if (app.bridge !== 'Connected' || modules.status === 'loading') return
  modules.begin()
  try { modules.complete(await client.getSnapshot()) }
  catch (error) { modules.fail(error) }
}
async function resyncAfterOperationFailure() {
  try {
    modules.complete(await client.getSnapshot())
  } catch {
    modules.fail(new Error('The host could not confirm the current module state.'))
  }
}
watch(() => app.bridge, bridge => { if (bridge === 'Connected' && modules.status === 'idle') void refresh() }, { immediate: true })
async function viewDetails(id: string) { modules.selectedModuleId = id; await router.push('/modules') }
async function operate(module: ModuleSnapshotItem, operation: Extract<LifecycleModuleOperation, 'open' | 'deactivate' | 'unload'>) {
  if (!hostOperationsAvailable.value) return
  if (!modules.beginOperation(module.id, operation)) return
  try {
    const snapshot = operation === 'open' ? await client.open(module.id) : operation === 'deactivate' ? await client.deactivate(module.id) : await client.unload(module.id)
    modules.complete(snapshot)
    toast.show(t(moduleOperationSuccessKey(operation), { name: module.displayName }), 'success')
  } catch {
    toast.show(t(moduleOperationFailureKey(operation), { name: module.displayName }), 'error')
    await resyncAfterOperationFailure()
  } finally { modules.endOperation(module.id) }
}
const hasConfirmedSnapshot = computed(() => modules.lastUpdatedAt !== null)
const hostOperationsAvailable = computed(() => app.bridge === 'Connected' && modules.status === 'ready')
const canRetry = computed(() => app.bridge === 'Connected' && modules.status !== 'loading')
const snapshotStatusMessage = computed(() => {
  if (!hasConfirmedSnapshot.value) return null
  if (app.bridge !== 'Connected') return t('running.status.disconnected')
  if (modules.status === 'error') return t('running.status.refreshFailedStale')
  if (modules.status === 'loading') return t('running.status.refreshingStale')
  return null
})
const staleSnapshot = computed(() => snapshotStatusMessage.value !== null)
const operationLabel = (module: ModuleSnapshotItem, operation: Extract<LifecycleModuleOperation, 'open' | 'deactivate' | 'unload'>) =>
  t(moduleOperationLabelKey(operation, modules.operations[module.id] === operation))
const runtimeLabel = (runtimeState: string) => {
  const key = moduleRuntimeStateKey(runtimeState)
  return key ? t(key) : runtimeState
}
</script>

<template>
  <QPage class="running-page">
    <header class="wpf-page-header"><div><h1>{{ t('running.page.title') }}</h1><p>{{ t('running.page.description') }}</p></div></header>
    <section v-if="snapshotStatusMessage" class="wpf-status-strip" role="status" aria-live="polite">{{ snapshotStatusMessage }}</section>
    <section v-if="modules.status === 'loading' && !hasConfirmedSnapshot" class="running-module-stack" :aria-label="t('running.loading.label')"><QSkeleton v-for="item in 3" :key="item" /></section>
    <section v-else-if="modules.status === 'error' && !hasConfirmedSnapshot" class="q-empty running-empty"><div class="q-empty-icon"><QIcon name="statusDanger" :size="28" /></div><h3>{{ t('running.empty.unavailable') }}</h3><p>{{ t('running.empty.unavailableDescription') }}</p><QButton :disabled="!canRetry" @click="refresh"><QIcon name="refresh" /> {{ t('running.empty.retry') }}</QButton></section>
    <section v-else-if="hasConfirmedSnapshot && modules.runningModules.length === 0" class="q-empty running-empty"><div class="q-empty-icon"><QIcon name="running" :size="28" /></div><h3>{{ t(staleSnapshot ? 'running.empty.stale' : 'running.empty.live') }}</h3><p>{{ t('running.empty.description') }}</p><RouterLink class="q-button" to="/modules">{{ t('running.empty.goToModules') }}</RouterLink></section>
    <section v-else-if="modules.runningModules.length" class="running-module-stack">
      <article v-for="module in modules.runningModules" :key="module.id" class="running-module-card">
        <span class="module-icon">{{ module.displayName.slice(0, 1).toUpperCase() }}</span>
        <div class="running-module-info">
          <div class="running-module-heading"><h2>{{ module.displayName }} <small>v{{ module.version }}</small></h2><span class="q-badge is-success">{{ runtimeLabel(module.runtimeState) }}</span></div>
          <p>{{ module.displayDescription }}</p>
          <dl><div><dt>{{ t('running.card.runtime') }}</dt><dd>{{ module.runtimeType }}</dd></div><div><dt>{{ t('running.card.author') }}</dt><dd>{{ module.author }}</dd></div></dl>
        </div>
        <div class="running-module-action">
          <QButton v-if="module.canOpen && !module.isExecutionBlocked" variant="primary" :disabled="!hostOperationsAvailable || !!modules.operations[module.id] || module.isBusy" @click="operate(module, 'open')">{{ operationLabel(module, 'open') }}</QButton>
          <QButton v-if="module.canDeactivate && !module.isExecutionBlocked" :disabled="!hostOperationsAvailable || !!modules.operations[module.id] || module.isBusy" @click="operate(module, 'deactivate')">{{ operationLabel(module, 'deactivate') }}</QButton>
          <QButton v-if="module.canUnload && !module.isExecutionBlocked" :disabled="!hostOperationsAvailable || !!modules.operations[module.id] || module.isBusy" @click="operate(module, 'unload')">{{ operationLabel(module, 'unload') }}</QButton>
          <button class="running-details-link" @click="viewDetails(module.id)">{{ t('running.card.viewDetails') }}</button>
        </div>
      </article>
    </section>
  </QPage>
</template>
