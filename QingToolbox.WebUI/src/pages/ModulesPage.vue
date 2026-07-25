<script setup lang="ts">
import { computed, inject, onBeforeUnmount, onMounted, watch } from 'vue'
import type { ModuleClient } from '../bridge/clients/ModuleClient'
import { useAppStore } from '../app/store'
import { useModuleStore, type ModuleFilter } from '../app/moduleStore'
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
async function refresh(showToast = false) { store.begin(); try { store.complete(await client.getSnapshot()); if (showToast) toast.show('Module snapshot refreshed.', 'success') } catch (error) { store.fail(error); toast.show('The host could not refresh modules.', 'error') } }
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

<template><QPage class="modules-page">
  <header class="wpf-page-header"><div><h1>Modules</h1><p>Discover, browse, and inspect toolbox modules.</p></div><QButton @click="refresh(true)" :disabled="store.status==='loading'"><QIcon name="refresh"/> {{store.status==='loading'?'Refreshing…':'Refresh modules'}}</QButton></header>
  <section class="wpf-status-strip"><span v-if="store.status==='loading'">Refreshing module state…</span><span v-else-if="store.status==='error'">Module state is unavailable.</span><span v-else>Found {{store.modules.length}} module{{store.modules.length===1?'':'s'}}.</span></section>
  <section v-if="store.modules.length" class="wpf-module-summary"><article><label>Total</label><strong>{{store.modules.length}}</strong></article><article><label>Valid</label><strong class="success">{{store.modules.filter(x=>x.isValid).length}}</strong></article><article><label>Failed</label><strong class="warning">{{failed}}</strong></article><article><label>Not loaded</label><strong class="accent">{{notLoaded}}</strong></article><article><label>Loaded</label><strong class="info">{{loaded}}</strong></article><article><label>Running</label><strong class="success">{{running}}</strong></article></section>
  <section class="wpf-module-tools"><label><span class="sr-only">Search modules</span><span class="wpf-search-icon"><QIcon name="search"/></span><input v-model="store.searchQuery" type="search" placeholder="Search modules…"></label><div class="filters" aria-label="Module state filter"><button v-for="filter in filters" :key="filter.key" :class="{active:store.stateFilter===filter.key}" @click="store.stateFilter=filter.key">{{filter.label}}</button></div></section>
  <div class="wpf-module-workspace" :class="{'has-selection':!!store.selectedModule}"><section class="wpf-module-list"><div v-if="store.status==='loading'&&store.modules.length===0" class="wpf-module-stack"><QSkeleton v-for="n in 3" :key="n"/></div><QEmptyState v-else-if="store.status==='error'" title="Modules are unavailable" :description="store.error"><QButton @click="refresh()">Try again</QButton></QEmptyState><QEmptyState v-else-if="store.visibleModules.length===0" :title="store.modules.length?'No modules found':'No modules installed'" :description="store.modules.length?'Try another search or state filter.':'Imported modules will appear here without being loaded automatically.'"/><div v-else class="wpf-module-stack"><article v-for="module in store.visibleModules" :key="module.id" class="wpf-module-card" :class="{selected:store.selectedModuleId===module.id}" tabindex="0" role="button" @click="store.selectedModuleId=module.id" @keydown.enter="store.selectedModuleId=module.id"><header><span class="module-icon">{{module.displayName.slice(0,1).toUpperCase()}}</span><div><h2>{{module.displayName}} <small>v{{module.version}}</small></h2></div><QBadge :tone="tone(module)">{{module.isValid?module.runtimeState:'Invalid'}}</QBadge></header><p>{{module.displayDescription}}</p><div class="wpf-readonly-note">Read-only module information</div><button class="wpf-details-button" @click.stop="store.selectedModuleId=module.id">Details</button><dl><dt>Runtime state:</dt><dd>{{module.runtimeState}}</dd></dl><footer :class="{issue:module.errorCount}">{{module.errorCount?`${module.errorCount} issue${module.errorCount===1?'':'s'}`:'No issues'}}</footer></article></div></section>
    <aside v-if="store.selectedModule" class="wpf-module-details"><button class="wpf-back" aria-label="Back to module list" @click="store.selectedModuleId=null"><QIcon name="back"/></button><header><span class="module-icon">{{store.selectedModule.displayName.slice(0,1).toUpperCase()}}</span><div><h2>{{store.selectedModule.displayName}}</h2><small>v{{store.selectedModule.version}}</small></div></header><QBadge :tone="tone(store.selectedModule)">{{store.selectedModule.isValid?store.selectedModule.runtimeState:'Invalid'}}</QBadge><p>{{store.selectedModule.displayDescription}}</p><div class="wpf-detail-divider"/><h3>Details</h3><dl class="wpf-detail-grid"><div><dt>Runtime</dt><dd>{{store.selectedModule.runtimeType}}</dd></div><div><dt>Load mode</dt><dd>{{store.selectedModule.loadMode}}</dd></div><div><dt>Author</dt><dd>{{store.selectedModule.author}}</dd></div><div><dt>Permissions</dt><dd>{{store.selectedModule.permissions.length?store.selectedModule.permissions.join(', '):'None declared'}}</dd></div><div><dt>Module ID</dt><dd>{{store.selectedModule.id}}</dd></div><div><dt>Minimum host version</dt><dd>{{store.selectedModule.minimumHostVersion}}</dd></div><div><dt>User installed</dt><dd>{{store.selectedModule.isUserInstalled?'Yes':'No'}}</dd></div><div><dt>Valid</dt><dd>{{store.selectedModule.isValid?'Yes':'No'}}</dd></div></dl><template v-if="store.selectedModule.errors.length"><h3>Issues</h3><ul><li v-for="error in store.selectedModule.errors" :key="error">{{error}}</li></ul></template></aside>
  </div>
</QPage></template>
