<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { open, save } from '@tauri-apps/plugin-dialog'
import { getContext, getState, hideModuleWindow, invokeModule } from './bridge'
import type { ModuleContext, Peer, State } from './types'

const fallback: State = {
  discovery: { running: false, peers: [] },
  session: { state: 'Idle', peer: null },
  incomingConnection: null,
  incomingFile: null,
  receive: { defaultDirectory: null, useDefaultDirectory: false, autoAccept: false },
  transfer: null,
  lastError: null,
}

const context = ref<ModuleContext | null>(null)
const state = ref<State>(structuredClone(fallback))
const loading = ref(true)
const busy = ref(false)
const error = ref('')
const notice = ref('')
let pollTimer: number | undefined
let requestSequence = 0
let latestRequest = 0

const sessionActive = computed(() => state.value.session.state !== 'Idle')
const connected = computed(() => state.value.session.state === 'Connected')
const progressPercent = computed(() => {
  const transfer = state.value.transfer
  return transfer && transfer.total > 0 ? Math.min(100, transfer.completed / transfer.total * 100) : 0
})
const receiveReady = computed(() => Boolean(state.value.receive.defaultDirectory && state.value.receive.useDefaultDirectory))
const sessionLabel = computed(() => ({
  Idle: '未连接',
  Connecting: '连接中…',
  WaitingApproval: '等待确认',
  Connected: '已连接',
}[state.value.session.state] ?? state.value.session.state))

function apply(next: State | null | undefined): void {
  if (next) state.value = next
}

function reasonMessage(reason: unknown): string {
  if (reason instanceof Error && reason.message) return reason.message
  if (typeof reason === 'object' && reason !== null) {
    const value = reason as { message?: unknown; error?: unknown }
    if (typeof value.message === 'string' && value.message) return value.message
    if (typeof value.error === 'string' && value.error) return value.error
  }
  return '请求未能完成。'
}

async function call(method: string, payload: Record<string, unknown> = {}): Promise<State | null> {
  if (busy.value && method !== 'getState') return null
  const sequence = ++requestSequence
  latestRequest = sequence
  if (method !== 'getState') {
    busy.value = true
    error.value = ''
  }
  try {
    const next = await invokeModule<State>(method, payload)
    if (sequence >= latestRequest) apply(next)
    return next
  } catch (reason) {
    if (method !== 'getState') error.value = reasonMessage(reason)
    return null
  } finally {
    if (method !== 'getState' || sequence === latestRequest) busy.value = false
  }
}

async function refresh(): Promise<void> {
  await call('refresh')
}

async function connect(peer: Peer): Promise<void> {
  await call('connect', { serviceName: peer.serviceName })
}

async function chooseAndSend(): Promise<void> {
  if (!connected.value || busy.value) return
  try {
    const picked = await open({
      title: '选择要发送的文件',
      multiple: false,
      directory: false,
    })
    if (typeof picked === 'string' && picked) await call('sendFile', { path: picked })
  } catch (reason) {
    error.value = reasonMessage(reason)
  }
}

async function chooseReceiveDirectory(): Promise<void> {
  if (busy.value) return
  try {
    const picked = await open({ title: '选择接收文件夹', directory: true, multiple: false })
    if (typeof picked === 'string' && picked) {
      await call('setReceivePreferences', { defaultDirectory: picked })
    }
  } catch (reason) {
    error.value = reasonMessage(reason)
  }
}

async function setPreference(name: 'useDefaultDirectory' | 'autoAccept', event: Event): Promise<void> {
  const checked = (event.target as HTMLInputElement).checked
  await call('setReceivePreferences', { [name]: checked })
}

async function acceptIncomingFile(): Promise<void> {
  if (!state.value.incomingFile || busy.value) return
  try {
    const picked = await save({
      title: '保存接收文件',
      defaultPath: state.value.incomingFile.name,
    })
    if (typeof picked === 'string' && picked) await call('acceptIncomingFile', { destinationPath: picked })
  } catch (reason) {
    error.value = reasonMessage(reason)
  }
}

function formatBytes(value: number): string {
  if (value < 1024) return `${value} B`
  const units = ['KB', 'MB', 'GB', 'TB']
  let amount = value
  let index = -1
  do { amount /= 1024; index += 1 } while (amount >= 1024 && index < units.length - 1)
  return `${amount.toFixed(amount >= 10 ? 0 : 1)} ${units[index]}`
}

function endpoint(peer: Peer): string {
  return `${peer.addresses.join(', ')}:${peer.port}`
}

function platform(peer: Peer): string {
  return peer.platform.toLowerCase() === 'android' ? 'Android' : 'Windows'
}

function dismissError(): void {
  error.value = ''
  notice.value = ''
  void call('dismissError')
}

function onKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape' && !busy.value) {
    event.preventDefault()
    if (state.value.incomingFile) void call('rejectIncomingFile')
    else if (state.value.incomingConnection) void call('rejectIncomingConnection')
    else void hideModuleWindow()
  }
}

async function poll(): Promise<void> {
  if (busy.value || loading.value) return
  const next = await call('getState')
  if (next?.lastError) notice.value = next.lastError
}

onMounted(async () => {
  window.addEventListener('keydown', onKeydown)
  try {
    context.value = await getContext()
    apply(await getState())
  } catch (reason) {
    error.value = reasonMessage(reason)
  } finally {
    loading.value = false
    pollTimer = window.setInterval(() => { void poll() }, 450)
  }
})

onUnmounted(() => {
  window.removeEventListener('keydown', onKeydown)
  if (pollTimer !== undefined) window.clearInterval(pollTimer)
})
</script>

<template>
  <main class="shell">
    <header class="hero">
      <div class="hero-brand">
        <div class="brand-mark"><img v-if="context?.iconDataUrl" :src="context.iconDataUrl" alt="" /><span v-else>⇄</span></div>
        <div>
          <p class="eyebrow">QING TOOLBOX</p>
          <h1>{{ context?.name || 'QingTransfer' }}</h1>
          <p class="subtitle">在局域网设备之间安全传输文件。</p>
        </div>
      </div>
      <div class="hero-actions">
        <span class="runtime-pill" :class="{ live: state.discovery.running }"><i />{{ loading ? '正在准备…' : state.discovery.running ? '局域网发现中' : '发现未启动' }}</span>
        <button class="icon-button" :disabled="busy" aria-label="刷新设备" title="刷新设备" @click="refresh">↻</button>
      </div>
    </header>

    <div v-if="error" class="alert danger"><span>{{ error }}</span><button aria-label="关闭" @click="dismissError">×</button></div>
    <div v-if="notice && !error" class="alert"><span>{{ notice }}</span><button aria-label="关闭" @click="notice = ''">×</button></div>

    <section v-if="sessionActive" class="session-card" :class="{ pending: !connected }">
      <div class="session-main"><span class="session-dot" :class="{ connected, pulse: !connected }" /><div><span class="label">当前会话</span><strong>{{ sessionLabel }}</strong><small>{{ state.session.peer?.displayName || '附近设备' }}<template v-if="state.session.peer"> · {{ platform(state.session.peer) }}</template></small></div></div>
      <div class="actions"><button v-if="connected" class="primary" :disabled="busy || Boolean(state.transfer)" @click="chooseAndSend">发送文件</button><button class="secondary" :disabled="busy" @click="call('disconnect')">断开</button></div>
    </section>

    <section v-if="state.transfer" class="progress-card">
      <div class="progress-heading"><div><span class="label">{{ state.transfer.receiving ? '正在接收' : '正在发送' }}</span><strong>{{ state.transfer.name }}</strong></div><span>{{ formatBytes(state.transfer.completed) }} / {{ formatBytes(state.transfer.total) }}</span></div>
      <div class="progress-track" role="progressbar" :aria-valuenow="state.transfer.completed" :aria-valuemax="state.transfer.total"><i :style="{ width: `${progressPercent}%` }" /></div>
    </section>

    <div class="content-grid">
      <section class="card devices-card">
        <div class="card-heading"><div><span class="label">附近设备</span><h2>附近 QingTransfer 设备</h2></div><span class="searching" :class="{ active: state.discovery.running }"><i />{{ state.discovery.running ? '正在查找附近设备…' : '发现已暂停' }}</span></div>
        <div v-if="!state.discovery.peers.length" class="empty"><div class="empty-mark">⌁</div><strong>暂未发现可用设备</strong><p>只有完成 QingTransfer 握手探测的端点才会显示在这里。</p></div>
        <div v-else class="peer-list">
          <article v-for="peer in state.discovery.peers" :key="peer.serviceName" class="peer-row" :class="{ active: state.session.peer?.serviceName === peer.serviceName }">
            <div class="peer-icon">{{ peer.platform.toLowerCase() === 'android' ? '▣' : '▤' }}</div>
            <div class="peer-copy"><strong>{{ peer.displayName }}</strong><span>{{ platform(peer) }} · 在线</span><small>{{ endpoint(peer) }}</small></div>
            <span v-if="state.session.peer?.serviceName === peer.serviceName" class="peer-state">{{ sessionLabel }}</span>
            <button v-else class="primary small" :disabled="busy || !peer.online || sessionActive" @click="connect(peer)">连接</button>
          </article>
        </div>
      </section>

      <section class="card receive-card">
        <div class="card-heading"><div><span class="label">接收文件</span><h2>接收设置</h2></div><span class="folder-mark">⌂</span></div>
        <div class="setting-row"><div><strong>默认接收文件夹</strong><small :title="state.receive.defaultDirectory || ''">{{ state.receive.defaultDirectory || '尚未配置' }}</small></div><div class="setting-actions"><button class="secondary small" :disabled="busy" @click="chooseReceiveDirectory">选择</button><button v-if="state.receive.defaultDirectory" class="ghost" :disabled="busy" @click="call('clearReceiveDirectory')">清除</button></div></div>
        <div class="setting-row"><div><strong>使用默认文件夹</strong><small>{{ receiveReady ? '自动保存到已选择的文件夹。' : '先选择一个可写文件夹。' }}</small></div><label class="toggle"><input type="checkbox" :checked="state.receive.useDefaultDirectory" :disabled="busy || !state.receive.defaultDirectory" @change="setPreference('useDefaultDirectory', $event)" /><i /></label></div>
        <div class="setting-row"><div><strong>自动接收文件</strong><small>仅在默认文件夹可用时生效。</small></div><label class="toggle"><input type="checkbox" :checked="state.receive.autoAccept" :disabled="busy || !receiveReady" @change="setPreference('autoAccept', $event)" /><i /></label></div>
      </section>
    </div>

    <div v-if="state.incomingConnection" class="modal-backdrop"><section class="modal" role="dialog" aria-modal="true"><div class="modal-mark">⇄</div><span class="label">连接请求</span><h2>{{ state.incomingConnection.name }}</h2><p>{{ platform({ platform: state.incomingConnection.platform } as Peer) }} 设备请求连接到 QingTransfer。</p><div class="modal-actions"><button class="primary" :disabled="busy" @click="call('acceptIncomingConnection')">接受</button><button class="secondary" :disabled="busy" @click="call('rejectIncomingConnection')">拒绝</button></div></section></div>
    <div v-if="state.incomingFile" class="modal-backdrop"><section class="modal" role="dialog" aria-modal="true"><div class="modal-mark">□</div><span class="label">收到文件</span><h2>{{ state.incomingFile.name }}</h2><span class="file-size">{{ formatBytes(state.incomingFile.size) }}</span><p>选择保存位置后开始接收，文件会先写入临时文件并校验 SHA-256。</p><div class="modal-actions"><button class="primary" :disabled="busy" @click="acceptIncomingFile">选择保存位置</button><button class="secondary" :disabled="busy" @click="call('rejectIncomingFile')">拒绝</button></div></section></div>
  </main>
</template>
