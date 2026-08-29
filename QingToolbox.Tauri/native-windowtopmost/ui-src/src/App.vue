<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { getContext, getState, hideModuleWindow, invokeModule } from './bridge'
import type { ModuleContext, WindowState } from './types'

const emptyState: WindowState = { windows: [], selectedWindowId: null, status: 'ready', error: null }
const context = ref<ModuleContext | null>(null)
const state = ref<WindowState>({ ...emptyState })
const loading = ref(true)
const busy = ref(false)
const error = ref('')
let pollTimer: number | undefined
let sequence = 0

const selected = computed(() => state.value.windows.find((item) => item.id === state.value.selectedWindowId) ?? null)
const canAct = computed(() => !loading.value && !busy.value && Boolean(selected.value))

function messageOf(reason: unknown): string {
  if (reason instanceof Error && reason.message) return reason.message
  if (typeof reason === 'object' && reason !== null) {
    const value = reason as { message?: unknown; error?: unknown }
    if (typeof value.message === 'string' && value.message) return value.message
    if (typeof value.error === 'string' && value.error) return value.error
  }
  return '窗口操作未能完成。'
}

function apply(next: WindowState | null | undefined): void {
  if (next) state.value = next
}

async function call(method: string, payload: Record<string, unknown> = {}): Promise<void> {
  if (busy.value) return
  const current = ++sequence
  busy.value = true
  error.value = ''
  try {
    const next = await invokeModule<WindowState>(method, payload)
    if (current >= sequence) apply(next)
  } catch (reason) {
    error.value = messageOf(reason)
  } finally {
    busy.value = false
  }
}

function select(id: string): void {
  state.value.selectedWindowId = id
}

async function pick(): Promise<void> {
  if (busy.value || loading.value) return
  await call('pickWindow')
}

async function setTopmost(topmost: boolean): Promise<void> {
  if (!canAct.value || !selected.value) return
  await call(topmost ? 'setTopmost' : 'removeTopmost', { windowId: selected.value.id })
}

function statusLabel(status: string): string {
  const labels: Record<string, string> = {
    ready: '请选择一个窗口。',
    refreshed: '窗口列表已刷新。',
    pickPending: '请在 3 秒内将鼠标移到目标窗口。',
    pickFailed: '没有拾取到可操作窗口。',
    selected: '已选择窗口。',
    topmostSet: '窗口已置顶。',
    topmostRemoved: '窗口已取消置顶。',
  }
  return labels[status] ?? status
}

async function poll(): Promise<void> {
  if (busy.value || loading.value) return
  try { apply(await getState()) } catch { /* transient module restart */ }
}

function onKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape' && !busy.value) {
    event.preventDefault()
    void hideModuleWindow()
  }
}

onMounted(async () => {
  window.addEventListener('keydown', onKeydown)
  try {
    context.value = await getContext()
    apply(await getState())
  } catch (reason) {
    error.value = messageOf(reason)
  } finally {
    loading.value = false
    pollTimer = window.setInterval(() => { void poll() }, 1000)
  }
})

onBeforeUnmount(() => {
  window.removeEventListener('keydown', onKeydown)
  if (pollTimer !== undefined) window.clearInterval(pollTimer)
})
</script>

<template>
  <main class="shell">
    <header class="hero">
      <div class="brand">
        <div class="brand-mark"><img v-if="context?.iconDataUrl" :src="context.iconDataUrl" alt="" /><span v-else>↑</span></div>
        <div><p class="eyebrow">QING TOOLBOX</p><h1>{{ context?.name || 'Window Topmost' }}</h1><p>选择可见窗口并切换始终置顶。</p></div>
      </div>
      <button class="icon-button" aria-label="关闭" title="关闭" @click="hideModuleWindow">×</button>
    </header>

    <div v-if="error" class="alert danger"><span>{{ error }}</span><button aria-label="关闭提示" @click="error = ''">×</button></div>

    <section class="toolbar">
      <button :disabled="loading || busy" @click="call('refresh')">刷新</button>
      <button class="primary" :disabled="loading || busy" @click="pick">拾取窗口</button>
      <span class="hint">{{ state.windows.length }} 个可操作窗口</span>
    </section>

    <section class="table-card">
      <div class="table-head"><span>标题</span><span>进程 / PID</span><span>句柄</span><span>置顶</span></div>
      <div v-if="loading" class="empty">正在准备窗口列表…</div>
      <div v-else-if="state.windows.length === 0" class="empty">当前没有可操作的可见窗口。</div>
      <button v-for="item in state.windows" :key="item.id" class="window-row" :class="{ selected: item.id === state.selectedWindowId }" @click="select(item.id)">
        <span class="title-cell" :title="item.title">{{ item.title }}</span>
        <span>{{ item.processName }} <small>({{ item.processId }})</small></span>
        <span class="mono">{{ item.handleText }}</span>
        <span class="pill" :class="{ active: item.isTopmost }">{{ item.isTopmost ? '是' : '否' }}</span>
      </button>
    </section>

    <footer class="footer">
      <span class="status"><i :class="{ active: selected }" />{{ statusLabel(state.status) }}</span>
      <div class="actions">
        <button :disabled="!canAct" @click="setTopmost(false)">取消置顶</button>
        <button class="primary" :disabled="!canAct" @click="setTopmost(true)">设为置顶</button>
        <button :disabled="loading || busy || !state.selectedWindowId" @click="call('clearSelection')">清除选择</button>
      </div>
    </footer>
  </main>
</template>
