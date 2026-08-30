<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { getContext, getState, hideModuleWindow, invokeModule, openPinWindow } from './bridge'
import type { DisplayBounds, ModuleContext, PinState } from './types'

const emptyBounds: DisplayBounds = { x: 0, y: 0, width: 1920, height: 1080 }
const context = ref<ModuleContext | null>(null)
const state = ref<PinState>({ pins: [], status: 'ready', error: null, displayBounds: emptyBounds })
const x = ref(0); const y = ref(0); const width = ref(640); const height = ref(360)
const loading = ref(true); const busy = ref(false); const error = ref(''); const notice = ref('')
let pollTimer: number | undefined
const boundsText = computed(() => { const b = state.value.displayBounds; return `${b.x}, ${b.y} · ${b.width} × ${b.height}` })

function messageOf(reason: unknown): string {
  if (reason instanceof Error && reason.message) return reason.message
  if (typeof reason === 'object' && reason !== null) { const value = reason as { message?: unknown; error?: unknown }; if (typeof value.message === 'string' && value.message) return value.message; if (typeof value.error === 'string' && value.error) return value.error }
  return '屏幕钉住操作未能完成。'
}
function apply(next: PinState | null | undefined): void { if (next) state.value = next }
async function call(method: string, payload: Record<string, unknown> = {}): Promise<boolean> {
  if (busy.value) return false; busy.value = true; error.value = ''
  try { apply(await invokeModule<PinState>(method, payload)); return true } catch (reason) { error.value = messageOf(reason); return false } finally { busy.value = false }
}
async function capture(): Promise<void> {
  if (await call('captureRegion', { x: x.value, y: y.value, width: width.value, height: height.value })) {
    notice.value = '截图已钉住。'
  }
}
async function floatPin(id: string): Promise<void> {
  try {
    await openPinWindow(id)
    notice.value = '已打开置顶浮窗。'
  } catch (reason) {
    // Browser preview has no native floating-window command; keep the pin
    // board usable and surface real host failures to the user.
    if (!import.meta.env.DEV) error.value = messageOf(reason)
  }
}
async function remove(id: string): Promise<void> { await call('removePin', { pinId: id }) }
async function poll(): Promise<void> { if (busy.value || loading.value) return; try { apply(await getState()) } catch { /* transient restart */ } }
function onKeydown(event: KeyboardEvent): void { if (event.key === 'Escape' && !busy.value) { event.preventDefault(); void hideModuleWindow() } }
onMounted(async () => { window.addEventListener('keydown', onKeydown); try { context.value = await getContext(); apply(await getState()) } catch (reason) { error.value = messageOf(reason) } finally { loading.value = false; pollTimer = window.setInterval(() => { void poll() }, 1000) } })
onBeforeUnmount(() => { window.removeEventListener('keydown', onKeydown); if (pollTimer !== undefined) window.clearInterval(pollTimer) })
</script>

<template>
  <main class="shell">
    <header class="hero"><div class="brand"><div class="brand-mark"><img v-if="context?.iconDataUrl" :src="context.iconDataUrl" alt="" /><span v-else>↗</span></div><div><p class="eyebrow">QING TOOLBOX</p><h1>{{ context?.name || 'Screen Pin' }}</h1><p>截取屏幕区域并在本地面板中钉住。</p></div></div><button class="icon-button" aria-label="关闭" title="关闭" @click="hideModuleWindow">×</button></header>
    <div v-if="error" class="alert danger"><span>{{ error }}</span><button @click="error = ''">×</button></div><div v-else-if="notice" class="alert"><span>{{ notice }}</span><button @click="notice = ''">×</button></div>
    <section class="layout"><article class="card controls"><div class="card-heading"><div><h2>截取区域</h2><p>当前虚拟屏幕：{{ boundsText }}</p></div><button :disabled="loading || busy" @click="call('getDisplayBounds')">刷新范围</button></div><div class="form-grid"><label>左坐标<input v-model.number="x" type="number" /></label><label>上坐标<input v-model.number="y" type="number" /></label><label>宽度<input v-model.number="width" type="number" min="1" max="4096" /></label><label>高度<input v-model.number="height" type="number" min="1" max="4096" /></label></div><p class="tip">坐标使用 Windows 虚拟屏幕像素。单张截图上限约 700 KiB，超出时请缩小区域。</p><button class="primary capture" :disabled="loading || busy" @click="capture">截取并钉住</button></article><article class="card help"><h2>使用提示</h2><p>先填写需要的屏幕坐标和尺寸，然后点击截取。截图由 Rust 后端直接读取并编码，WebView 不接触屏幕 API 或文件系统。</p><div class="limit"><strong>{{ state.pins.length }}</strong><span>/ 8 张截图</span></div><button class="clear" :disabled="loading || busy || state.pins.length === 0" @click="call('clearPins')">清空截图</button></article></section>
    <section class="card pins"><div class="card-heading"><div><h2>已钉住截图</h2><p>截图保存在当前模块会话中，关闭模块后自动释放。</p></div></div><div v-if="loading" class="empty">正在准备…</div><div v-else-if="state.pins.length === 0" class="empty">还没有钉住截图。</div><div v-else class="pin-grid"><article v-for="pin in state.pins" :key="pin.id" class="pin"><div class="pin-image"><img :src="pin.dataUrl" :alt="`${pin.width}×${pin.height}`" /></div><div class="pin-meta"><span>{{ pin.width }} × {{ pin.height }}</span><span>({{ pin.x }}, {{ pin.y }})</span><button class="float-button" aria-label="打开浮窗" title="打开置顶浮窗" @click="floatPin(pin.id)">↗</button><button aria-label="移除截图" title="移除截图" @click="remove(pin.id)">×</button></div></article></div></section>
    <footer class="footer"><span><i :class="{ active: state.pins.length > 0 }" />{{ loading ? '正在准备…' : (state.status === 'captured' ? '截图已钉住。' : state.status === 'cleared' ? '截图已清空。' : '可以截取屏幕区域。') }}</span><span>Esc 关闭窗口</span></footer>
  </main>
</template>
