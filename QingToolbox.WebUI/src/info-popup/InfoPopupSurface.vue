<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { invoke } from '@tauri-apps/api/core'
import { listen, type UnlistenFn } from '@tauri-apps/api/event'
import QInfoPopup from '../design-system/components/QInfoPopup.vue'
import { applyAppearancePreset, readAppearancePreset } from '../design-system/tokens/appearancePresets'
import { PopupCountdown } from './countdown'

type Notification = { id: string; deviceName: string; appName: string; title: string; body: string }
const item = ref<Notification | null>(null)
const closing = ref(false)
const dismissLabel = navigator.language.toLowerCase().startsWith('zh') ? '知道了' : 'Dismiss'
let unlisten: UnlistenFn | null = null
let hovered = false
let focused = false
let request = 0
let disposed = false
const countdown = new PopupCountdown(() => { void dismiss() })

async function refresh() {
  const currentRequest = ++request
  try {
    const theme = localStorage.getItem('qing.theme')
    document.documentElement.dataset.theme = theme === 'dark' || theme === 'light' ? theme : 'system'
    applyAppearancePreset(readAppearancePreset())
    const dismissSeconds = await invoke<number>('get_info_popup_dismiss_seconds').catch(() => 15)
    // The backend binds the item to this window. No other card's ID is accepted.
    const next = await invoke<Notification | null>('get_info_popup_item')
    if (disposed || currentRequest !== request || item.value?.id === next?.id) return
    countdown.stop()
    item.value = next
    closing.value = false
    if (next) countdown.start(Math.max(3, Math.min(60, dismissSeconds)) * 1000, hovered || focused)
  } catch {
    // A transient IPC failure must not blank a card or reset its remaining time.
  }
}

async function dismiss() {
  const current = item.value
  if (!current || closing.value || disposed) return
  closing.value = true
  countdown.stop()
  try { await invoke('dismiss_info_popup_item', { id: current.id }) }
  catch { closing.value = false }
  finally { if (!disposed) await refresh() }
}

function updatePause() {
  if (closing.value || !item.value) return
  if (hovered || focused) countdown.pause()
  else countdown.resume()
}
function hover(value: boolean) {
  hovered = value
  updatePause()
  const current = item.value
  if (current && !closing.value && !disposed) {
    void invoke('set_info_popup_hover', { id: current.id, hovered: value }).catch(() => {})
  }
}
function focusIn() { focused = true; updatePause() }
function focusOut(event: FocusEvent) {
  if ((event.currentTarget as HTMLElement).contains(event.relatedTarget as Node | null)) return
  focused = false
  updatePause()
}

onMounted(async () => {
  const unsubscribe = await listen('qing:info-popup-changed', () => { void refresh() })
  if (disposed) { unsubscribe(); return }
  unlisten = unsubscribe
  await refresh()
})
onBeforeUnmount(() => { disposed = true; request++; unlisten?.(); countdown.stop() })
</script>

<template>
  <QInfoPopup v-if="item" :device-name="item.deviceName" :app-name="item.appName"
    :title="item.title || item.appName" :label="item.body" :button-label="dismissLabel"
    :button-disabled="closing" @action="dismiss"
    @mouseenter="hover(true)" @mouseleave="hover(false)" @focusin="focusIn" @focusout="focusOut" />
</template>

<style>
html.info-popup-document, html.info-popup-document body, html.info-popup-document #app { width: 100%; height: 100%; margin: 0; overflow: hidden; background: transparent !important; }
html.info-popup-document #app { padding: 2px; box-sizing: border-box; }
</style>
