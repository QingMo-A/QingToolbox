<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { invoke } from '@tauri-apps/api/core'
import { listen, type UnlistenFn } from '@tauri-apps/api/event'
import QInfoPopup from '../design-system/components/QInfoPopup.vue'
import { applyAppearancePreset, readAppearancePreset } from '../design-system/tokens/appearancePresets'

type Notification = { id: string; deviceName: string; appName: string; title: string; body: string }
const item = ref<Notification | null>(null)
const dismissLabel = navigator.language.toLowerCase().startsWith('zh') ? '知道了' : 'Dismiss'
let unlisten: UnlistenFn | null = null
let timeout: ReturnType<typeof setTimeout> | null = null

async function refresh() {
  try {
    const theme = localStorage.getItem('qing.theme')
    document.documentElement.dataset.theme = theme === 'dark' || theme === 'light' ? theme : 'system'
    applyAppearancePreset(readAppearancePreset())
    const dismissSeconds = await invoke<number>('get_info_popup_dismiss_seconds').catch(() => 15)
    const next = await invoke<Notification | null>('get_info_popup_item')
    if (item.value?.id === next?.id) return
    item.value = next
    if (timeout) clearTimeout(timeout)
    timeout = next ? setTimeout(() => { void dismiss() }, Math.max(3, Math.min(60, dismissSeconds)) * 1000) : null
  } catch { item.value = null }
}
async function dismiss() {
  const current = item.value
  if (!current) return
  try { await invoke('dismiss_info_popup_item', { id: current.id }) }
  finally { await refresh() }
}
onMounted(async () => {
  unlisten = await listen('qing:info-popup-changed', () => { void refresh() })
  await refresh()
})
onBeforeUnmount(() => { unlisten?.(); if (timeout) clearTimeout(timeout) })
</script>

<template>
  <QInfoPopup v-if="item" :device-name="item.deviceName" :app-name="item.appName"
    :title="item.title || item.appName" :label="item.body" :button-label="dismissLabel"
    @action="dismiss" />
</template>

<style>
html.info-popup-document, html.info-popup-document body, html.info-popup-document #app { width: 100%; height: 100%; margin: 0; overflow: hidden; background: transparent !important; }
html.info-popup-document #app { padding: 2px; box-sizing: border-box; }
</style>
