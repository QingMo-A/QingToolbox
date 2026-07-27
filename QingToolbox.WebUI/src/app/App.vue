<script setup lang="ts">
import { inject, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useThemeStore } from './themeStore'
import { useAppStore } from './store'
import { useSettingsStore } from './settingsStore'
import type { SettingsClient } from '../bridge/clients/SettingsClient'
import QSidebarLayout from '../design-system/layouts/QSidebarLayout.vue'
import QCommandPalette from '../design-system/components/QCommandPalette.vue'
import QToast from '../design-system/components/QToast.vue'

const theme = useThemeStore()
const app = useAppStore()
const settings = useSettingsStore()
const client = inject<SettingsClient>('settingsClient')!
const commandPaletteOpen = ref(false)

async function loadSettings() {
  settings.begin()
  try { settings.complete(await client.getSnapshot()) }
  catch (error) { settings.fail(error) }
}
function onGlobalKeydown(event: KeyboardEvent) {
  if ((event.ctrlKey || event.metaKey) && !event.altKey && event.key.toLocaleLowerCase() === 'k') {
    event.preventDefault()
    commandPaletteOpen.value = true
  }
}

onMounted(() => {
  theme.set(theme.mode)
  window.addEventListener('keydown', onGlobalKeydown)
})
onBeforeUnmount(() => window.removeEventListener('keydown', onGlobalKeydown))
watch(() => app.bridge, bridge => {
  if (bridge === 'Connected' && settings.status === 'idle') void loadSettings()
}, { immediate: true })
</script>

<template>
  <QSidebarLayout @open-command-palette="commandPaletteOpen = true">
    <router-view />
  </QSidebarLayout>
  <QCommandPalette :open="commandPaletteOpen" @close="commandPaletteOpen = false" />
  <QToast />
</template>
<style src="../styles/sidebar.css"></style>
