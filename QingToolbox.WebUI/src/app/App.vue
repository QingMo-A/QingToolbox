<script setup lang="ts">
import { inject, onBeforeUnmount, onMounted, ref, watch, watchEffect } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useThemeStore, normalizeThemeMode } from './themeStore'
import { useAppearancePresetStore } from '../design-system/tokens/appearancePresets'
import { useAppStore } from './store'
import { useSettingsStore } from './settingsStore'
import type { SettingsClient } from '../bridge/clients/SettingsClient'
import QSidebarLayout from '../design-system/layouts/QSidebarLayout.vue'
import QCommandPalette from '../design-system/components/QCommandPalette.vue'
import QToast from '../design-system/components/QToast.vue'
import { useLocalization } from '../localization/localization'
import { routeTitleKeyByPath } from './router'
import { applyFontPresentation } from '../presentation/fontPresentation'
import QTitleBar from '../design-system/components/QTitleBar.vue'
import { TauriTransport } from '../bridge/transport/TauriTransport'
import { listen, type UnlistenFn } from '@tauri-apps/api/event'
import DeviceTransferPanel from '../pages/DeviceTransferPanel.vue'

const nativeTitleBar = TauriTransport.isAvailable()

const theme = useThemeStore()
const appearance = useAppearancePresetStore()
const app = useAppStore()
const settings = useSettingsStore()
const client = inject<SettingsClient>('settingsClient')!
const commandPaletteOpen = ref(false)
const transferRequest = ref<{ device: { id: string; name: string }; incoming: boolean } | null>(null)
let unlistenTransfer: UnlistenFn | null = null
function openDeviceTransfer(event: Event) {
  const device = (event as CustomEvent<{ id: string; name: string }>).detail
  if (device?.id && device.name) transferRequest.value = { device, incoming: false }
}
const route = useRoute()
const router = useRouter()
const { currentLocale, t } = useLocalization()

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
  appearance.set(appearance.id)
  window.addEventListener('keydown', onGlobalKeydown)
  window.addEventListener('qing:open-device-transfer', openDeviceTransfer)
  if (nativeTitleBar) void listen<{ id: string; name: string }>('qing:incoming-device-file', event => {
    if (event.payload?.id && event.payload.name) transferRequest.value = { device: event.payload, incoming: true }
  }).then(unlisten => { unlistenTransfer = unlisten })
})
onBeforeUnmount(() => {
  window.removeEventListener('keydown', onGlobalKeydown)
  window.removeEventListener('qing:open-device-transfer', openDeviceTransfer)
  unlistenTransfer?.()
})
watch(() => app.bridge, bridge => {
  if (bridge === 'Connected' && settings.status === 'idle') void loadSettings()
}, { immediate: true })
watch(() => settings.snapshot?.appearancePresetId, presetId => {
  if (presetId !== undefined) appearance.set(presetId)
}, { immediate: true })
// Applied here rather than only on the settings page: the host owns the mode
// because module windows have to be told about it, so whatever it reports wins
// over the copy this window cached in local storage for its first paint.
watch(() => settings.snapshot?.themeMode, mode => {
  if (mode !== undefined) theme.set(normalizeThemeMode(mode))
}, { immediate: true })
watch(() => settings.snapshot?.font, font => {
  void applyFontPresentation(font)
}, { immediate: true, deep: true })
watchEffect(() => {
  if (route.path === '/diagnostics' && app.snapshot && app.snapshot.environmentKind !== 'Development') {
    void router.replace('/')
  }
  document.documentElement.lang = currentLocale.value
  const titleKey = routeTitleKeyByPath[route.path as keyof typeof routeTitleKeyByPath]
  document.title = `${titleKey ? t(titleKey) : t('app.productName')} · ${t('app.productName')}${app.snapshot?.environmentKind === 'Development' ? ' [Dev]' : ''}`
})
</script>

<template>
  <div class="q-desktop-frame" :class="{ 'with-titlebar': nativeTitleBar }">
  <QTitleBar v-if="nativeTitleBar" />
  <QSidebarLayout @open-command-palette="commandPaletteOpen = true">
    <router-view />
  </QSidebarLayout>
  </div>
  <QCommandPalette :open="commandPaletteOpen" @close="commandPaletteOpen = false" />
  <DeviceTransferPanel v-if="transferRequest" :key="`${transferRequest.device.id}:${transferRequest.incoming}`" :device="transferRequest.device" :incoming="transferRequest.incoming" @close="transferRequest = null" />
  <QToast />
</template>
<style>
.q-desktop-frame { height: 100%; min-height: 0; }
.q-desktop-frame.with-titlebar { display: grid; grid-template-rows: 40px minmax(0, 1fr); }
.q-desktop-frame { user-select: none; -webkit-user-select: none; }
.q-desktop-frame :is(input, textarea, [contenteditable="true"], pre, code, .logs-table, .log-details, [data-selectable]) {
  user-select: text; -webkit-user-select: text;
}
.q-desktop-frame img { -webkit-user-drag: none; }
</style>
<style src="../styles/sidebar.css"></style>
