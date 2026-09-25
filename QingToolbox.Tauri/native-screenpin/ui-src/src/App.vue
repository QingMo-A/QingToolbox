<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { getContext, getState, hideModuleWindow, invokeModule, openPinWindow, selectRegion } from './bridge'
import type { ModuleContext, PinState } from './types'

const context = ref<ModuleContext | null>(null)
const state = ref<PinState>({ pins: [], status: 'ready', error: null, displayBounds: { x: 0, y: 0, width: 1920, height: 1080 } })
const loading = ref(true)
const busy = ref(false)
const error = ref('')
let pollTimer: number | undefined
function messageOf(reason: unknown): string {
  if (reason instanceof Error) return reason.message
  if (typeof reason === 'object' && reason !== null && 'message' in reason) return String(reason.message)
  return String(reason)
}
async function call(method: string, payload: Record<string, unknown> = {}): Promise<void> {
  if (busy.value) return
  busy.value = true; error.value = ''
  try { state.value = await invokeModule<PinState>(method, payload) }
  catch (reason) { error.value = messageOf(reason) }
  finally { busy.value = false }
}
async function capture(): Promise<void> {
  if (busy.value) return
  busy.value = true; error.value = ''
  try {
    const next = await selectRegion()
    if ('cancelled' in next) return
    state.value = next
    const pin = next.pins.at(-1)
    if (pin) await openPinWindow(pin.id)
  } catch (reason) { error.value = messageOf(reason) }
  finally { busy.value = false }
}
async function floatPin(id: string): Promise<void> {
  try { await openPinWindow(id) } catch (reason) { error.value = messageOf(reason) }
}
async function copyPin(id: string): Promise<void> {
  try { await invokeModule('copyPin', { pinId: id }) } catch (reason) { error.value = messageOf(reason) }
}
async function poll(): Promise<void> {
  if (busy.value || loading.value) return
  try { state.value = await getState() } catch { /* transient module restart */ }
}
function onKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape' && !busy.value) { event.preventDefault(); void hideModuleWindow() }
}
onMounted(async () => {
  window.addEventListener('keydown', onKeydown)
  try { context.value = await getContext(); state.value = await getState() }
  catch (reason) { error.value = messageOf(reason) }
  finally { loading.value = false; pollTimer = window.setInterval(() => { void poll() }, 1500) }
})
onBeforeUnmount(() => {
  window.removeEventListener('keydown', onKeydown)
  if (pollTimer !== undefined) window.clearInterval(pollTimer)
})
</script>

<template>
  <main class="shell">
    <header class="hero">
      <div class="brand"><div class="brand-mark"><img v-if="context?.iconDataUrl" :src="context.iconDataUrl" alt="" /><span v-else>↗</span></div><h1>截图 Pin</h1></div>
      <button class="primary capture" :disabled="loading || busy" @click="capture">{{ busy ? '正在框选…' : '框选截图' }}</button>
    </header>
    <div v-if="error" class="alert danger" role="alert"><span>{{ error }}</span><button aria-label="关闭提示" @click="error = ''">×</button></div>
    <section class="card pins">
      <div class="card-heading"><h2>截图 · {{ state.pins.length }}</h2><button class="clear" :disabled="loading || busy || !state.pins.length" @click="call('clearPins')">清空</button></div>
      <div v-if="loading" class="empty">正在准备…</div>
      <div v-else-if="!state.pins.length" class="empty">还没有截图</div>
      <div v-else class="pin-grid">
        <article v-for="pin in state.pins" :key="pin.id" class="pin">
          <div class="pin-image" @dblclick="floatPin(pin.id)"><img :src="pin.dataUrl" :alt="`${pin.width}×${pin.height}`" /></div>
          <div class="pin-meta"><span>{{ pin.width }} × {{ pin.height }}</span><span />
            <button aria-label="复制截图" title="复制截图" @click="copyPin(pin.id)">⧉</button>
            <button class="float-button" aria-label="打开浮窗" title="打开浮窗" @click="floatPin(pin.id)">↗</button>
            <button aria-label="移除截图" title="移除截图" @click="call('removePin', { pinId: pin.id })">×</button>
          </div>
        </article>
      </div>
    </section>
  </main>
</template>
