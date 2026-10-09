<script setup lang="ts">
import { QButton } from '@qingtoolbox/module-ui'
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { getContext, getState, hideModuleWindow, invokeModule, openPinWindow, selectRegion } from './bridge'
import type { ModuleContext, PinState } from './types'

const context = ref<ModuleContext | null>(null)
const state = ref<PinState>({ pins: [], status: 'ready', error: null, displayBounds: { x: 0, y: 0, width: 1920, height: 1080 } })
const loading = ref(true)
const busy = ref(false)
const error = ref('')
// Presentation-only state: which card just copied, whether "clear" is armed,
// and the short confirmation toast.
const copiedId = ref<string | null>(null)
const confirmClear = ref(false)
const toast = ref<{ text: string; token: number } | null>(null)
let pollTimer: number | undefined
let copiedTimer: number | undefined
let clearTimer: number | undefined
let toastTimer: number | undefined
function messageOf(reason: unknown): string {
  if (reason instanceof Error) return reason.message
  if (typeof reason === 'object' && reason !== null && 'message' in reason) return String(reason.message)
  return String(reason)
}
function notify(text: string): void {
  toast.value = { text, token: Date.now() }
  window.clearTimeout(toastTimer)
  toastTimer = window.setTimeout(() => { toast.value = null }, 1800)
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
  try { await openPinWindow(id); notify('已钉到桌面') } catch (reason) { error.value = messageOf(reason) }
}
async function copyPin(id: string): Promise<void> {
  try {
    await invokeModule('copyPin', { pinId: id })
    copiedId.value = id
    window.clearTimeout(copiedTimer)
    copiedTimer = window.setTimeout(() => { copiedId.value = null }, 1400)
    notify('已复制到剪贴板')
  } catch (reason) { error.value = messageOf(reason) }
}
// Clearing every pin is the one action that cannot be taken back, so the
// first press arms it and only a second press within three seconds clears.
async function clearPins(): Promise<void> {
  if (!confirmClear.value) {
    confirmClear.value = true
    window.clearTimeout(clearTimer)
    clearTimer = window.setTimeout(() => { confirmClear.value = false }, 3000)
    return
  }
  confirmClear.value = false
  window.clearTimeout(clearTimer)
  await call('clearPins')
}
function ratioLabel(width: number, height: number): string {
  const known: Array<[number, string]> = [[16 / 9, '16:9'], [16 / 10, '16:10'], [4 / 3, '4:3'], [3 / 2, '3:2'], [1, '1:1'], [21 / 9, '21:9'], [9 / 16, '9:16'], [3 / 4, '3:4']]
  const ratio = width / height
  return known.find(([value]) => Math.abs(value - ratio) < 0.012)?.[1] ?? ''
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
  window.clearTimeout(copiedTimer); window.clearTimeout(clearTimer); window.clearTimeout(toastTimer)
})
</script>

<template>
  <main class="shell">
    <svg width="0" height="0" class="sprite" aria-hidden="true"><defs>
      <symbol id="i-capture" viewBox="0 0 24 24"><path d="M4 8V5.5A1.5 1.5 0 0 1 5.5 4H8M16 4h2.5A1.5 1.5 0 0 1 20 5.5V8M20 16v2.5a1.5 1.5 0 0 1-1.5 1.5H16M8 20H5.5A1.5 1.5 0 0 1 4 18.5V16M12 9v6M9 12h6" /></symbol>
      <symbol id="i-pin" viewBox="0 0 24 24"><path d="M14.5 3.5l6 6M16.5 5.5l-5 4.5-4.5-.5-2 2 7 7 2-2-.5-4.5 4.5-5M8.5 15.5l-5 5" /></symbol>
      <symbol id="i-copy" viewBox="0 0 24 24"><path d="M8.5 8.5h11v12h-11zM5.5 15.5h-1v-12h11v1" /></symbol>
      <symbol id="i-check" viewBox="0 0 24 24"><path d="M5 12.5l4.5 4.5L19 7.5" /></symbol>
      <symbol id="i-trash" viewBox="0 0 24 24"><path d="M4.5 7h15M9.5 7V4.5h5V7M6.5 7l1 13h9l1-13M10.5 11v5M13.5 11v5" /></symbol>
      <symbol id="i-close" viewBox="0 0 24 24"><path d="M6.5 6.5l11 11M17.5 6.5l-11 11" /></symbol>
    </defs></svg>

    <header class="hero">
      <div class="brand">
        <div class="brand-mark" aria-hidden="true"><img v-if="context?.iconDataUrl" :src="context.iconDataUrl" alt="" /><svg v-else class="i"><use href="#i-capture" /></svg></div>
        <div class="brand-copy">
          <h1>截图钉</h1>
          <p>框选屏幕任意区域，把它钉在所有窗口之上</p>
        </div>
      </div>
      <QButton class="capture-button" variant="primary" :disabled="loading || busy" @click="capture">
        <svg class="i" :class="{ spinning: busy }"><use href="#i-capture" /></svg>{{ busy ? '正在框选…' : '框选截图' }}
      </QButton>
    </header>

    <Transition name="fade">
      <div v-if="error" class="alert danger" role="alert"><span>{{ error }}</span><button type="button" aria-label="关闭提示" @click="error = ''"><svg class="i"><use href="#i-close" /></svg></button></div>
    </Transition>

    <section class="card pins">
      <div class="card-heading">
        <h2>已钉截图 <span class="count">{{ state.pins.length }}</span></h2>
        <QButton class="clear-button" :class="{ armed: confirmClear }" :variant="confirmClear ? 'danger' : undefined" :disabled="loading || busy || !state.pins.length" @click="clearPins">
          <svg class="i"><use href="#i-trash" /></svg>{{ confirmClear ? '再点一次清空' : '清空' }}
        </QButton>
      </div>

      <div v-if="loading" class="pin-grid" aria-hidden="true">
        <div v-for="n in 3" :key="n" class="skeleton"><i /><b /></div>
      </div>
      <div v-else-if="!state.pins.length" class="empty">
        <div class="empty-art" aria-hidden="true">
          <svg viewBox="0 0 160 110"><rect class="marching" x="18" y="14" width="124" height="78" rx="8" /><g class="handles"><rect x="13" y="9" width="10" height="10" rx="2" /><rect x="137" y="9" width="10" height="10" rx="2" /><rect x="13" y="87" width="10" height="10" rx="2" /><rect x="137" y="87" width="10" height="10" rx="2" /></g><path class="cross" d="M80 42v26M67 55h26" /></svg>
        </div>
        <strong>还没有截图</strong>
        <span>框选一块区域，它会立刻浮在屏幕最上层</span>
        <QButton variant="primary" class="empty-cta" :disabled="busy" @click="capture"><svg class="i"><use href="#i-capture" /></svg>开始框选</QButton>
      </div>
      <TransitionGroup v-else name="pin" tag="div" class="pin-grid">
        <article v-for="(pin, index) in state.pins" :key="pin.id" class="pin" :style="{ '--i': index }">
          <div class="pin-image" title="双击再次钉到桌面" @dblclick="floatPin(pin.id)">
            <img :src="pin.dataUrl" :alt="`${pin.width}×${pin.height}`" decoding="async" draggable="false" />
            <span class="pin-index">#{{ index + 1 }}</span>
            <div class="pin-overlay">
              <button type="button" class="overlay-primary" @click="floatPin(pin.id)"><svg class="i"><use href="#i-pin" /></svg>钉到桌面</button>
            </div>
          </div>
          <div class="pin-meta">
            <span class="pin-size">{{ pin.width }} × {{ pin.height }}</span>
            <span v-if="ratioLabel(pin.width, pin.height)" class="pin-ratio">{{ ratioLabel(pin.width, pin.height) }}</span>
            <span class="spacer" />
            <button type="button" :class="{ done: copiedId === pin.id }" aria-label="复制截图" title="复制截图" @click="copyPin(pin.id)"><svg class="i"><use :href="copiedId === pin.id ? '#i-check' : '#i-copy'" /></svg></button>
            <button type="button" class="float-button" aria-label="打开浮窗" title="打开浮窗" @click="floatPin(pin.id)"><svg class="i"><use href="#i-pin" /></svg></button>
            <button type="button" class="remove-button" aria-label="移除截图" title="移除截图" @click="call('removePin', { pinId: pin.id })"><svg class="i"><use href="#i-trash" /></svg></button>
          </div>
        </article>
      </TransitionGroup>
    </section>

    <footer class="tips" aria-label="贴图操作">
      <span><kbd>拖动</kbd>移动贴图</span>
      <span><kbd>滚轮</kbd>缩放</span>
      <span><kbd>Ctrl</kbd>+<kbd>滚轮</kbd>透明度</span>
      <span><kbd>右键</kbd>更多</span>
      <span><kbd>Esc</kbd>关闭</span>
    </footer>

    <Transition name="toast">
      <div v-if="toast" :key="toast.token" class="toast" role="status"><svg class="i"><use href="#i-check" /></svg>{{ toast.text }}</div>
    </Transition>
  </main>
</template>
