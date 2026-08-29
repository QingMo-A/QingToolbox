<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { getContext, getState, hideModuleWindow, invokeModule } from './bridge'
import type { ModuleContext, State } from './types'

const fallback: State = { input: '', output: '', status: 'ready', error: null }
const context = ref<ModuleContext | null>(null)
const state = ref<State>({ ...fallback })
const loading = ref(true)
const busy = ref(false)
const error = ref('')
const notice = ref('')
let pollTimer: number | undefined
let inputTimer: number | undefined
let sequence = 0

const hasInput = computed(() => state.value.input.length > 0)
const hasOutput = computed(() => state.value.output.length > 0)
const disabled = computed(() => loading.value || busy.value)

function messageOf(reason: unknown): string {
  if (reason instanceof Error && reason.message) return reason.message
  if (typeof reason === 'object' && reason !== null) {
    const value = reason as { message?: unknown; error?: unknown }
    if (typeof value.message === 'string' && value.message) return value.message
    if (typeof value.error === 'string' && value.error) return value.error
  }
  return '请求未能完成。'
}

function apply(next: State | null | undefined): void {
  if (next) state.value = next
}

async function call(method: string, payload: unknown = {}): Promise<boolean> {
  if (busy.value && method !== 'getState') return false
  const current = ++sequence
  if (method !== 'getState') {
    busy.value = true
    error.value = ''
  }
  try {
    const next = await invokeModule<State>(method, payload)
    if (current >= sequence) {
      apply(next)
      if (next.error) notice.value = next.error
    }
    return true
  } catch (reason) {
    if (method !== 'getState') error.value = messageOf(reason)
    return false
  } finally {
    if (method !== 'getState') busy.value = false
  }
}

function updateInput(event: Event): void {
  const text = (event.target as HTMLTextAreaElement).value
  state.value.input = text
  if (inputTimer !== undefined) window.clearTimeout(inputTimer)
  inputTimer = window.setTimeout(() => {
    void call('setInput', { text })
  }, 120)
}

async function run(method: string): Promise<void> {
  if (inputTimer !== undefined) {
    window.clearTimeout(inputTimer)
    inputTimer = undefined
    if (!await call('setInput', { text: state.value.input })) return
  }
  await call(method)
}

function statusLabel(value: string): string {
  return ({ ready: '就绪', done: '完成', copied: '已复制', moved: '已移入输入', swapped: '已交换', input_empty: '请输入文本' } as Record<string, string>)[value] ?? value
}

async function poll(): Promise<void> {
  if (busy.value || loading.value) return
  try {
    const next = await invokeModule<State>('getState')
    apply(next)
  } catch {
    // A transient module restart should not erase the editor contents.
  }
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
    pollTimer = window.setInterval(() => { void poll() }, 600)
  }
})

onBeforeUnmount(() => {
  window.removeEventListener('keydown', onKeydown)
  if (pollTimer !== undefined) window.clearInterval(pollTimer)
  if (inputTimer !== undefined) window.clearTimeout(inputTimer)
})
</script>

<template>
  <main class="shell">
    <header class="hero">
      <div class="brand">
        <div class="brand-mark"><img v-if="context?.iconDataUrl" :src="context.iconDataUrl" alt="" /><span v-else>TT</span></div>
        <div><p class="eyebrow">QING TOOLBOX</p><h1>{{ context?.name || 'Text Tools' }}</h1><p>快速格式化、编码、解码和转换文本。</p></div>
      </div>
      <button class="icon-button" aria-label="关闭" title="关闭" @click="hideModuleWindow">×</button>
    </header>

    <div v-if="error" class="alert danger"><span>{{ error }}</span><button aria-label="关闭提示" @click="error = ''">×</button></div>
    <div v-else-if="notice" class="alert"><span>{{ notice }}</span><button aria-label="关闭提示" @click="notice = ''">×</button></div>

    <section class="editor-grid">
      <label class="editor-card"><span class="field-label">输入</span><textarea :value="state.input" :disabled="loading" spellcheck="false" placeholder="在这里输入文本…" @input="updateInput" /></label>
      <label class="editor-card output"><span class="field-label">输出</span><textarea :value="state.output" readonly spellcheck="false" placeholder="转换结果会显示在这里…" /></label>
    </section>

    <section class="actions-card">
      <div class="action-group">
        <button class="primary" :disabled="disabled || !hasInput" @click="run('formatJson')">格式化 JSON</button>
        <button :disabled="disabled || !hasInput" @click="run('minifyJson')">压缩 JSON</button>
        <button :disabled="disabled || !hasInput" @click="run('base64Encode')">Base64 编码</button>
        <button :disabled="disabled || !hasInput" @click="run('base64Decode')">Base64 解码</button>
        <button :disabled="disabled || !hasInput" @click="run('urlEncode')">URL 编码</button>
        <button :disabled="disabled || !hasInput" @click="run('urlDecode')">URL 解码</button>
        <button :disabled="disabled || !hasInput" @click="run('uppercase')">转大写</button>
        <button :disabled="disabled || !hasInput" @click="run('lowercase')">转小写</button>
        <button :disabled="disabled || !hasInput" @click="run('removeEmptyLines')">删除空行</button>
      </div>
      <div class="utility-group">
        <button :disabled="disabled || !hasOutput" @click="run('copyOutput')">复制结果</button>
        <button :disabled="disabled || !hasOutput" @click="run('copyOutputToInput')">输出到输入</button>
        <button :disabled="disabled || (!hasInput && !hasOutput)" @click="run('swap')">交换</button>
        <button :disabled="disabled || (!hasInput && !hasOutput)" @click="run('clear')">清空</button>
      </div>
    </section>

    <footer class="status" :class="{ error: state.error }"><span class="dot" />{{ state.error || (loading ? '正在准备…' : statusLabel(state.status)) }}<span class="count">{{ state.input.length.toLocaleString() }} 字符</span></footer>
  </main>
</template>
