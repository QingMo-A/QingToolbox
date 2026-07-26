<script setup lang="ts">
import { inject, watch } from 'vue'
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
async function refresh() { modules.begin(); try { modules.complete(await client.getSnapshot()) } catch (error) { modules.fail(error) } }
watch(() => app.bridge, bridge => { if (bridge === 'Connected' && modules.status === 'idle') void refresh() }, { immediate: true })
async function viewDetails(id: string) { modules.selectedModuleId = id; await router.push('/modules') }
async function operate(module: ModuleSnapshotItem, operation: 'open'|'deactivate'|'unload') {
  if (!modules.beginOperation(module.id, operation)) return
  try {
    const snapshot = operation === 'open' ? await client.open(module.id) : operation === 'deactivate' ? await client.deactivate(module.id) : await client.unload(module.id)
    modules.complete(snapshot)
    toast.show(operation === 'open' ? `${module.displayName} window opened or focused.` : `${module.displayName} ${operation === 'deactivate' ? 'deactivated' : 'unloaded'}.`, 'success')
  }
  catch (error) { toast.show(error instanceof Error ? error.message : String(error), 'error'); try { modules.complete(await client.getSnapshot()) } catch { /* preserve original operation failure */ } }
  finally { modules.endOperation(module.id) }
}
</script>

<template><QPage class="running-page">
  <header class="wpf-page-header"><div><h1>Running modules</h1><p>Modules currently active in the host runtime.</p></div></header>
  <section v-if="modules.status==='loading'" class="running-module-stack" aria-label="Loading running modules"><QSkeleton v-for="item in 3" :key="item"/></section>
  <section v-else-if="modules.status==='error'" class="q-empty running-empty"><div class="q-empty-icon"><QIcon name="statusDanger" :size="28"/></div><h3>Running modules are unavailable</h3><p>The current module state could not be read.</p><QButton @click="refresh"><QIcon name="refresh"/> Retry</QButton></section>
  <section v-else-if="modules.status==='ready'&&modules.runningModules.length===0" class="q-empty running-empty"><div class="q-empty-icon"><QIcon name="running" :size="28"/></div><h3>No modules are currently running</h3><p>Modules will appear here after the host confirms they are running.</p><RouterLink class="q-button" to="/modules">Go to modules</RouterLink></section>
  <section v-else-if="modules.runningModules.length" class="running-module-stack"><article v-for="module in modules.runningModules" :key="module.id" class="running-module-card"><span class="module-icon">{{module.displayName.slice(0,1).toUpperCase()}}</span><div class="running-module-info"><h2>{{module.displayName}} <small>v{{module.version}}</small></h2><p>{{module.displayDescription}}</p><dl><div><dt>Runtime</dt><dd>{{module.runtimeType}}</dd></div><div><dt>Author</dt><dd>{{module.author}}</dd></div></dl></div><div class="running-module-action"><span class="q-badge is-success">{{module.runtimeState}}</span><QButton v-if="module.canOpen&&!module.isExecutionBlocked" variant="primary" :disabled="!!modules.operations[module.id]||module.isBusy" @click="operate(module,'open')">{{modules.operations[module.id]==='open'?'Opening…':'Open'}}</QButton><QButton v-if="module.canDeactivate&&!module.isExecutionBlocked" :disabled="!!modules.operations[module.id]||module.isBusy" @click="operate(module,'deactivate')">{{modules.operations[module.id]==='deactivate'?'Deactivating…':'Deactivate'}}</QButton><QButton v-if="module.canUnload&&!module.isExecutionBlocked" :disabled="!!modules.operations[module.id]||module.isBusy" @click="operate(module,'unload')">{{modules.operations[module.id]==='unload'?'Unloading…':'Unload'}}</QButton><button class="q-button" @click="viewDetails(module.id)">View details</button></div></article></section>
</QPage></template>
