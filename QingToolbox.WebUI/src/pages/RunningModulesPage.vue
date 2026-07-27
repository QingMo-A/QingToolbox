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

const client = inject<ModuleClient>('moduleClient')!
const app = useAppStore()
const modules = useModuleStore()
const toast = useToastStore()
const router = useRouter()
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
async function operate(module: ModuleSnapshotItem, operation: 'open' | 'deactivate' | 'unload') {
  if (!hostOperationsAvailable.value) return
  if (!modules.beginOperation(module.id, operation)) return
  try {
    const snapshot = operation === 'open' ? await client.open(module.id) : operation === 'deactivate' ? await client.deactivate(module.id) : await client.unload(module.id)
    modules.complete(snapshot)
    toast.show(operation === 'open' ? `${module.displayName} window opened or focused.` : `${module.displayName} ${operation === 'deactivate' ? 'deactivated' : 'unloaded'}.`, 'success')
  } catch {
    toast.show(operation === 'open' ? `The ${module.displayName} window could not be opened.` : `${module.displayName} could not be ${operation === 'deactivate' ? 'deactivated' : 'unloaded'}.`, 'error')
    await resyncAfterOperationFailure()
  } finally { modules.endOperation(module.id) }
}
const hasConfirmedSnapshot = computed(() => modules.lastUpdatedAt !== null)
const hostOperationsAvailable = computed(() => app.bridge === 'Connected' && modules.status === 'ready')
const canRetry = computed(() => app.bridge === 'Connected' && modules.status !== 'loading')
const snapshotStatusMessage = computed(() => {
  if (!hasConfirmedSnapshot.value) return null
  if (app.bridge !== 'Connected') return 'The host is disconnected. Showing the last confirmed running-module snapshot.'
  if (modules.status === 'error') return 'The host could not refresh running modules. Showing the last confirmed snapshot.'
  if (modules.status === 'loading') return 'Refreshing module state. Showing the last confirmed snapshot.'
  return null
})
const staleSnapshot = computed(() => snapshotStatusMessage.value !== null)
</script>

<template>
  <QPage class="running-page">
    <header class="wpf-page-header"><div><h1>Running modules</h1><p>Modules currently active in the host runtime.</p></div></header>
    <section v-if="snapshotStatusMessage" class="wpf-status-strip" role="status" aria-live="polite">{{ snapshotStatusMessage }}</section>
    <section v-if="modules.status === 'loading' && !hasConfirmedSnapshot" class="running-module-stack" aria-label="Loading running modules"><QSkeleton v-for="item in 3" :key="item" /></section>
    <section v-else-if="modules.status === 'error' && !hasConfirmedSnapshot" class="q-empty running-empty"><div class="q-empty-icon"><QIcon name="statusDanger" :size="28" /></div><h3>Running modules are unavailable</h3><p>The host could not provide the current module snapshot.</p><QButton :disabled="!canRetry" @click="refresh"><QIcon name="refresh" /> Retry</QButton></section>
    <section v-else-if="hasConfirmedSnapshot && modules.runningModules.length === 0" class="q-empty running-empty"><div class="q-empty-icon"><QIcon name="running" :size="28" /></div><h3>{{ staleSnapshot ? 'No running modules were present in the last confirmed snapshot.' : 'No modules are currently running' }}</h3><p>Modules will appear here after the host confirms they are running.</p><RouterLink class="q-button" to="/modules">Go to modules</RouterLink></section>
    <section v-else-if="modules.runningModules.length" class="running-module-stack">
      <article v-for="module in modules.runningModules" :key="module.id" class="running-module-card">
        <span class="module-icon">{{ module.displayName.slice(0, 1).toUpperCase() }}</span>
        <div class="running-module-info">
          <div class="running-module-heading"><h2>{{ module.displayName }} <small>v{{ module.version }}</small></h2><span class="q-badge is-success">{{ module.runtimeState }}</span></div>
          <p>{{ module.displayDescription }}</p>
          <dl><div><dt>Runtime</dt><dd>{{ module.runtimeType }}</dd></div><div><dt>Author</dt><dd>{{ module.author }}</dd></div></dl>
        </div>
        <div class="running-module-action">
          <QButton v-if="module.canOpen && !module.isExecutionBlocked" variant="primary" :disabled="!hostOperationsAvailable || !!modules.operations[module.id] || module.isBusy" @click="operate(module, 'open')">{{ modules.operations[module.id] === 'open' ? 'Opening…' : 'Open' }}</QButton>
          <QButton v-if="module.canDeactivate && !module.isExecutionBlocked" :disabled="!hostOperationsAvailable || !!modules.operations[module.id] || module.isBusy" @click="operate(module, 'deactivate')">{{ modules.operations[module.id] === 'deactivate' ? 'Deactivating…' : 'Deactivate' }}</QButton>
          <QButton v-if="module.canUnload && !module.isExecutionBlocked" :disabled="!hostOperationsAvailable || !!modules.operations[module.id] || module.isBusy" @click="operate(module, 'unload')">{{ modules.operations[module.id] === 'unload' ? 'Unloading…' : 'Unload' }}</QButton>
          <button class="running-details-link" @click="viewDetails(module.id)">View details</button>
        </div>
      </article>
    </section>
  </QPage>
</template>
