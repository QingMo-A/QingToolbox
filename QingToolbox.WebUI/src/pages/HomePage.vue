<script setup lang="ts">
import { computed, inject, watch } from 'vue'
import { useAppStore } from '../app/store'
import { useModuleStore } from '../app/moduleStore'
import { useToastStore } from '../app/toastStore'
import type { ModuleClient } from '../bridge/clients/ModuleClient'
import QPage from '../design-system/components/QPage.vue'
import QEmptyState from '../design-system/components/QEmptyState.vue'
import QIcon from '../design-system/components/QIcon.vue'

type HomeStatusIcon = 'statusSuccess'|'statusInfo'|'statusWarning'|'statusDanger'

const app = useAppStore()
const modules = useModuleStore()
const toast = useToastStore()
const client = inject<ModuleClient>('moduleClient')!

async function refresh() {
  modules.begin()
  try {
    modules.complete(await client.getSnapshot())
    toast.show('Module snapshot refreshed.', 'success')
  } catch (error) {
    modules.fail(error)
    toast.show('The host could not refresh modules.', 'error')
  }
}

watch(() => app.bridge, bridge => {
  if (bridge === 'Connected' && modules.status === 'idle') void refresh()
}, { immediate: true })

const running = computed(() => modules.modules.filter(module => module.runtimeState === 'Running').length)
const loaded = computed(() => modules.modules.filter(module => module.runtimeState === 'Loaded').length)
const failed = computed(() => modules.modules.filter(module => module.errorCount > 0 || !module.isValid).length)
const modulePreview = computed(() => modules.modules.slice(0, 5))
const homeStatus = computed<{ title: string; description: string; tone: string; icon: HomeStatusIcon }>(() => {
  if (app.bridge !== 'Connected' || modules.status === 'error') {
    return { title: 'Host status unavailable', description: 'Unable to read the current module state.', tone: 'danger', icon: 'statusDanger' }
  }
  if (modules.status === 'idle' || modules.status === 'loading') {
    return { title: 'Checking system status', description: 'Reading module state from the host.', tone: 'information', icon: 'statusInfo' }
  }
  if (failed.value > 0) {
    return { title: 'Modules need attention', description: 'Some modules have validation or state issues.', tone: 'warning', icon: 'statusWarning' }
  }
  return { title: 'Running normally', description: 'All discovered modules report a healthy state.', tone: 'success', icon: 'statusSuccess' }
})

function openModule(id: string) {
  modules.selectedModuleId = id
}
</script>

<template><QPage class="home-page"><QEmptyState v-if="modules.status==='ready'&&modules.modules.length===0" title="Your toolbox is empty" description="Modules you add will appear here without being loaded automatically."/><template v-else><section class="wpf-home-hero" :class="`is-${homeStatus.tone}`"><div><small>System status</small><h1>{{homeStatus.title}} <QIcon :name="homeStatus.icon" :size="22"/></h1><p>{{homeStatus.description}}</p><RouterLink class="q-button wpf-light-button" to="/modules">View modules</RouterLink></div><div class="wpf-hero-icon"><QIcon name="modules" :size="58"/></div></section><template v-if="modules.modules.length"><section class="wpf-home-summary"><article><label>Installed modules</label><strong>{{modules.modules.length}}</strong></article><article><label>Running modules</label><strong class="success">{{running}}</strong></article><article><label>Loaded modules</label><strong class="accent">{{loaded}}</strong></article><article><label>Issues</label><strong class="warning">{{failed}}</strong></article></section><section class="wpf-recent"><header><h2>Modules overview</h2><RouterLink v-if="modules.modules.length>5" to="/modules">View all modules</RouterLink></header><button v-for="module in modulePreview" :key="module.id" @click="openModule(module.id);$router.push('/modules')"><span class="module-icon">{{module.displayName.slice(0,1).toUpperCase()}}</span><span><strong>{{module.displayName}}</strong><small>{{module.displayDescription}}</small></span><em>{{module.runtimeState}}</em></button></section></template><section class="wpf-home-actions"><RouterLink to="/modules"><b><QIcon name="modules"/></b><span>View modules</span></RouterLink><button @click="refresh"><b><QIcon name="refresh"/></b><span>Refresh modules</span></button></section></template></QPage></template>
