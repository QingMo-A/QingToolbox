<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { getContext, getState, hideModuleWindow, invokeModule } from './bridge'
import type { ModuleContext, PowerState, Settings } from './types'

const defaults: Settings = {
  guardEnabled: false,
  startupGraceSeconds: 120,
  offlineConfirmationSeconds: 60,
  shutdownCountdownSeconds: 600,
  recoveryConfirmationSeconds: 30,
  showRecoveryNotification: true,
}
const blank: PowerState = {
  settings: { ...defaults }, guardEnabled: false, state: 'disabled', isOnline: false,
  lastProbeEpochMillis: null, lastSuccessfulProbeEpochMillis: null,
  consecutiveProbeFailures: 0, countdownRemainingSeconds: null, testRemainingSeconds: null,
  isSuppressed: false, lastProbe: [], events: [],
}
const context = ref<ModuleContext | null>(null)
const state = ref<PowerState>({ ...blank, settings: { ...defaults } })
const settings = ref<Settings>({ ...defaults })
const loading = ref(true)
const busy = ref(false)
const error = ref('')
const notice = ref('')
let pollTimer: number | undefined

const stateLabel = computed(() => ({
  disabled: '已停用', startupGrace: '启动宽限', online: '在线监测', suspectedOffline: '疑似断网',
  countdown: '确认断网，准备关机', suppressed: '本次断网已抑制', recovering: '等待网络恢复确认',
  executingShutdown: '正在执行关机', actionFailed: '关机执行失败', test: '测试倒计时',
}[state.value.state] ?? state.value.state))
const canCancel = computed(() => state.value.state === 'countdown')
const canRearm = computed(() => state.value.state === 'suppressed')
const canExtend = computed(() => state.value.state === 'countdown')
const canShutdown = computed(() => state.value.state === 'countdown' || state.value.state === 'actionFailed')
const statusText = computed(() => state.value.testRemainingSeconds !== null
  ? `测试倒计时 ${formatSeconds(state.value.testRemainingSeconds)}`
  : state.value.countdownRemainingSeconds !== null
    ? `关机倒计时 ${formatSeconds(state.value.countdownRemainingSeconds)}`
    : stateLabel.value)

function formatSeconds(value: number): string {
  const minutes = Math.floor(Math.max(0, value) / 60).toString().padStart(2, '0')
  const seconds = (Math.max(0, value) % 60).toString().padStart(2, '0')
  return `${minutes}:${seconds}`
}

function messageOf(reason: unknown): string {
  if (reason instanceof Error && reason.message) return reason.message
  if (typeof reason === 'object' && reason !== null) {
    const value = reason as { message?: unknown; error?: unknown }
    if (typeof value.message === 'string' && value.message) return value.message
    if (typeof value.error === 'string' && value.error) return value.error
  }
  return '断网守护操作未能完成。'
}

function apply(next: PowerState | null | undefined): void {
  if (!next) return
  state.value = next
  settings.value = { ...next.settings }
}

async function call(method: string, payload: Record<string, unknown> = {}): Promise<boolean> {
  if (busy.value) return false
  busy.value = true
  error.value = ''
  try {
    apply(await invokeModule<PowerState>(method, payload))
    return true
  } catch (reason) {
    error.value = messageOf(reason)
    return false
  } finally {
    busy.value = false
  }
}

async function saveSettings(): Promise<void> {
  if (settings.value.startupGraceSeconds < 0 || settings.value.startupGraceSeconds > 600
    || settings.value.offlineConfirmationSeconds < 15 || settings.value.offlineConfirmationSeconds > 300
    || settings.value.shutdownCountdownSeconds < 60 || settings.value.shutdownCountdownSeconds > 3600
    || settings.value.recoveryConfirmationSeconds < 5 || settings.value.recoveryConfirmationSeconds > 120) {
    error.value = '请输入范围内的设置。'
    return
  }
  if (await call('setSettings', { ...settings.value })) notice.value = '设置已保存。'
}

async function immediateShutdown(): Promise<void> {
  if (!canShutdown.value || !window.confirm('确定要立即关机吗？此操作由系统执行，无法撤销。')) return
  await call('shutdownNow', { confirm: 'SHUTDOWN' })
}

function formatTime(epoch: number | null): string {
  return epoch ? new Date(epoch).toLocaleString() : '无'
}

async function poll(): Promise<void> {
  if (busy.value || loading.value) return
  try { apply(await getState()) } catch { /* transient restart */ }
}

function onKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape' && !busy.value) { event.preventDefault(); void hideModuleWindow() }
}

onMounted(async () => {
  window.addEventListener('keydown', onKeydown)
  try { context.value = await getContext(); apply(await getState()) }
  catch (reason) { error.value = messageOf(reason) }
  finally { loading.value = false; pollTimer = window.setInterval(() => { void poll() }, 1000) }
})
onBeforeUnmount(() => { window.removeEventListener('keydown', onKeydown); if (pollTimer !== undefined) window.clearInterval(pollTimer) })
</script>

<template>
  <main class="shell">
    <header class="hero">
      <div class="brand"><div class="brand-mark"><img v-if="context?.iconDataUrl" :src="context.iconDataUrl" alt="" /><span v-else>⚡</span></div><div><p class="eyebrow">QING TOOLBOX</p><h1>{{ context?.name || 'PowerGuard' }}</h1><p>监测网络连接，并在确认中断后安全倒计时。</p></div></div>
      <button class="icon-button" aria-label="关闭" title="关闭" @click="hideModuleWindow">×</button>
    </header>
    <div v-if="error" class="alert danger"><span>{{ error }}</span><button @click="error = ''">×</button></div>
    <div v-else-if="notice" class="alert"><span>{{ notice }}</span><button @click="notice = ''">×</button></div>

    <section class="summary-grid">
      <article class="summary-card"><span>守护状态</span><strong :class="{ good: state.guardEnabled }">{{ state.guardEnabled ? '已启用' : '未启用' }}</strong><small>{{ stateLabel }}</small></article>
      <article class="summary-card"><span>网络状态</span><strong :class="{ good: state.isOnline }">{{ state.isOnline ? '在线' : '离线' }}</strong><small>连续失败 {{ state.consecutiveProbeFailures }} 次</small></article>
      <article class="summary-card countdown"><span>当前倒计时</span><strong>{{ statusText }}</strong><small>最近探测：{{ formatTime(state.lastProbeEpochMillis) }}</small></article>
    </section>

    <section class="layout-grid">
      <article class="card settings-card">
        <div class="card-heading"><div><h2>监测设置</h2><p>所有时间均由 Rust 后端校验并持久化。</p></div><label class="switch"><input v-model="settings.guardEnabled" type="checkbox" /><span />启用守护</label></div>
        <div class="form-grid">
          <label>启动宽限（秒）<input v-model.number="settings.startupGraceSeconds" type="number" min="0" max="600" /></label>
          <label>断网确认（秒）<input v-model.number="settings.offlineConfirmationSeconds" type="number" min="15" max="300" /></label>
          <label>关机倒计时（秒）<input v-model.number="settings.shutdownCountdownSeconds" type="number" min="60" max="3600" /></label>
          <label>恢复确认（秒）<input v-model.number="settings.recoveryConfirmationSeconds" type="number" min="5" max="120" /></label>
        </div>
        <p class="notice">默认关闭守护。启用前请确认网络判定和自动关机策略符合你的使用场景。</p>
        <div class="button-row"><button class="primary" :disabled="loading || busy" @click="saveSettings">保存设置</button><button :disabled="loading || busy" @click="call('probeNow')">立即探测</button><button :disabled="loading || busy" @click="call('testWarning')">测试倒计时</button></div>
      </article>
      <article class="card actions-card"><h2>当前操作</h2><p class="muted">真实倒计时只会在连续探测确认断网后出现。</p><div class="button-stack"><button :disabled="!canCancel || busy" @click="call('cancelCurrent')">取消当前倒计时</button><button :disabled="!canRearm || busy" @click="call('rearmCurrent')">重新武装</button><button :disabled="!canExtend || busy" @click="call('extendCountdown')">延长 10 分钟</button><button class="danger-button" :disabled="!canShutdown || busy" @click="immediateShutdown">立即关机</button></div></article>
    </section>

    <section class="lower-grid"><article class="card"><div class="card-heading"><div><h2>最近探测</h2><p>固定的公共 TCP 端点，仅用于判断网络可达性。</p></div></div><div v-if="state.lastProbe.length === 0" class="empty">尚未执行探测。</div><div v-for="endpoint in state.lastProbe" :key="endpoint.name" class="endpoint"><span :class="['endpoint-dot', { good: endpoint.succeeded }]" /><strong>{{ endpoint.name }}</strong><span>{{ endpoint.succeeded ? '成功' : endpoint.failureCategory || '失败' }}</span><small>{{ endpoint.elapsedMillis }} ms</small></div></article><article class="card"><div class="card-heading"><div><h2>最近事件</h2><p>最多保留 20 条，由模块后端维护。</p></div><button class="link-button" :disabled="busy" @click="call('clearEvents')">清空</button></div><div v-if="state.events.length === 0" class="empty">暂无事件。</div><div v-for="event in [...state.events].reverse()" :key="`${event.timestampEpochMillis}-${event.kind}`" class="event"><span>{{ formatTime(event.timestampEpochMillis) }}</span><strong>{{ event.kind }}</strong><small>{{ event.detail || '' }}</small></div></article></section>
    <footer class="footer"><span><i :class="{ good: state.guardEnabled }" />{{ loading ? '正在准备…' : statusText }}</span><span>最近在线：{{ formatTime(state.lastSuccessfulProbeEpochMillis) }}</span></footer>
  </main>
</template>
