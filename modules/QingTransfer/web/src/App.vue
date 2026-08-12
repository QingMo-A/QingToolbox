<script setup lang="ts">
import { computed, onMounted, onUnmounted, reactive, ref } from 'vue'
import { invoke, onPresentationChanged, onStateChanged, waitForPresentation } from './bridge'
import QingIcon from './QingIcon.vue'
import type { Peer, Presentation, State } from './types'

const fallback: State = {
  discovery: { running: false, peers: [] },
  session: { state: 'Idle', peer: null },
  incomingConnection: null,
  incomingFile: null,
  receive: { defaultDirectory: null, useDefaultDirectory: false, autoAccept: false },
  transfer: null,
  lastError: null,
}
const state = reactive<State>(structuredClone(fallback))
const presentation = reactive<Presentation>({ appearancePresetId: 'qing-default', languageCode: 'en-US' })
const resources = ref<Record<string, string>>({})
const busy = ref(false)
const fileDecisionBusy = ref(false)
const notice = ref('')
const sessionActive = computed(() => state.session.state !== 'Idle')
const locale = computed(() => presentation.languageCode === 'zh-CN' ? 'zh-CN' : 'en-US')
const t = (key: string, fallbackText = key) => resources.value[key] ?? fallbackText
const sessionStatus = computed(() => {
  if (state.session.state === 'Connecting') return t('status.connecting', 'Connecting')
  if (state.session.state === 'WaitingApproval') return t('status.waitingApproval', 'Waiting for approval')
  return t('status.connected', 'Connected')
})
const platform = (peer: Peer) => peer.platform.toLowerCase() === 'android' ? t('platform.android', 'Android') : t('platform.windows', 'Windows')
const endpoint = (peer: Peer) => `${peer.addresses.join(', ')}:${peer.port}`
const isActivePeer = (peer: Peer) => state.session.peer?.serviceName === peer.serviceName
const peerCanConnect = (peer: Peer) => state.session.state === 'Idle' && peer.online && peer.port > 0
const progressPercent = computed(() => state.transfer && state.transfer.total > 0 ? Math.min(100, state.transfer.completed / state.transfer.total * 100) : 0)

const apply = (next: State) => {
  Object.assign(state, next)
  if (next.lastError) notice.value = next.lastError
}

const run = async (method: string, payload?: unknown) => {
  const isFileDecision = method === 'acceptIncomingFile' || method === 'rejectIncomingFile'
  if (isFileDecision) fileDecisionBusy.value = true
  busy.value = true
  try {
    const next = await invoke<State>(method, payload)
    if (next) apply(next)
  } catch (error) {
    notice.value = error instanceof Error ? error.message : t('errors.request', 'The request could not be completed.')
  } finally {
    busy.value = false
    if (isFileDecision) fileDecisionBusy.value = false
  }
}

async function loadResources() {
  try { resources.value = await fetch(`../i18n/${locale.value}.json`).then(response => response.json()) }
  catch { resources.value = {} }
}

async function initialLoad() {
  const ready = await waitForPresentation()
  Object.assign(presentation, ready)
  await loadResources()
  const next = await invoke<State>('getState')
  if (next) apply(next)
}

function setPref(name: 'useDefaultDirectory' | 'autoAccept', value: boolean) { void run('setReceivePreferences', { [name]: value }) }
function dismissNotice() { notice.value = ''; void run('dismissError') }
function formatFileSize(bytes: number) {
  if (bytes < 1024) return `${bytes} B`
  const units = ['KB', 'MB', 'GB', 'TB']
  let value = bytes
  let index = -1
  do { value /= 1024; index++ } while (value >= 1024 && index < units.length - 1)
  return `${value.toFixed(value >= 10 ? 0 : 1)} ${units[index]}`
}

onMounted(() => {
  const offState = onStateChanged(apply)
  const offPresentation = onPresentationChanged(next => { Object.assign(presentation, next); void loadResources() })
  void initialLoad()
  onUnmounted(() => { offState(); offPresentation() })
})
</script>

<template>
  <main class="shell">
    <header class="hero">
      <div class="hero-copy"><p class="eyebrow">QING TOOLBOX</p><h1>{{ t('view.title', 'QingTransfer') }}</h1><p class="subtitle">{{ t('view.subtitle', 'Discover nearby QingToolbox devices on this local network.') }}</p></div>
      <button class="secondary icon-button" :disabled="busy" :aria-label="t('actions.refresh', 'Refresh')" :title="t('actions.refresh', 'Refresh')" @click="run('refresh')"><QingIcon name="refresh" /></button>
    </header>

    <section v-if="sessionActive" class="card session-card" :class="{ 'session-pending': state.session.state !== 'Connected' }">
      <div class="session-info"><span class="status-dot" :class="{ pulse: state.session.state !== 'Connected' }"></span><div><span class="section-label">{{ t('view.session', 'Active session') }}</span><strong>{{ sessionStatus }}</strong><p>{{ state.session.peer?.displayName || t('status.incomingPeer', 'Incoming device') }}</p><small v-if="state.session.peer">{{ platform(state.session.peer) }}</small></div></div>
      <div class="actions"><button v-if="state.session.state === 'Connected'" class="primary" :disabled="busy" @click="run('chooseAndSendFile')"><QingIcon name="send" />{{ t('actions.sendFile', 'Send file') }}</button><button class="secondary" @click="run('disconnect')"><QingIcon name="disconnect" />{{ t('actions.disconnect', 'Disconnect') }}</button></div>
    </section>

    <section v-if="state.transfer" class="card progress-card"><div class="card-heading"><div><span class="section-label">{{ state.transfer.receiving ? t('status.receiving', 'Receiving') : t('status.sending', 'Sending') }}</span><h2>{{ state.transfer.name }}</h2></div><strong class="progress-value">{{ formatFileSize(state.transfer.completed) }} <span>/ {{ formatFileSize(state.transfer.total) }}</span></strong></div><div class="progress-track" role="progressbar" :aria-valuenow="state.transfer.completed" :aria-valuemax="state.transfer.total"><span :style="{ width: `${progressPercent}%` }"></span></div></section>

    <div class="content-grid">
      <section class="card devices-card"><div class="card-heading"><div><span class="section-label">{{ t('view.footer', 'Nearby devices') }}</span><h2>{{ t('automation.peerList', 'Nearby QingTransfer devices') }}</h2></div><span class="live-status" :class="{ live: state.discovery.running }"><i></i>{{ state.discovery.running ? t('status.searching', 'Looking for nearby devices...') : t('status.paused', 'Discovery paused.') }}</span></div><div v-if="!state.discovery.peers.length" class="empty"><QingIcon name="nearby" /><strong>{{ t('view.empty', 'No QingToolbox devices are online yet.') }}</strong><p>{{ t('view.hint', 'Discovery uses local DNS-SD. No connection or transfer is started here.') }}</p></div><div v-else class="peer-grid"><article v-for="peer in state.discovery.peers" :key="peer.serviceName" class="peer" :class="{ active: isActivePeer(peer), pending: isActivePeer(peer) && state.session.state !== 'Connected' }"><div class="peer-icon"><QingIcon :name="peer.platform.toLowerCase() === 'android' ? 'phone' : 'desktop'" /></div><div class="peer-main"><h3>{{ peer.displayName }}</h3><span>{{ platform(peer) }} · {{ t('status.online', 'Online') }}</span><small class="technical">{{ endpoint(peer) }}</small></div><span v-if="isActivePeer(peer)" class="peer-state">{{ sessionStatus }}</span><button v-if="peerCanConnect(peer)" class="primary small" :disabled="busy" @click="run('connect', { serviceName: peer.serviceName })">{{ t('actions.connect', 'Connect') }}</button></article></div></section>

      <section class="card settings-card"><div class="card-heading"><div><span class="section-label">{{ t('receive.title', 'Receiving files') }}</span><h2>{{ t('receive.title', 'Receiving files') }}</h2></div><QingIcon name="folder" /></div><div class="setting-row"><div><strong>{{ t('receive.useDefault', 'Use default folder') }}</strong><small class="technical path" :title="state.receive.defaultDirectory || t('receive.noDirectory', 'No default folder configured')">{{ state.receive.defaultDirectory || t('receive.noDirectory', 'No default folder configured') }}</small></div><div class="row-actions"><button class="secondary small" @click="run('chooseReceiveDirectory')">{{ t('actions.choose', 'Choose') }}</button><button v-if="state.receive.defaultDirectory" class="ghost" @click="run('clearReceiveDirectory')">{{ t('actions.clear', 'Clear') }}</button><label class="q-toggle" :aria-label="t('receive.useDefault', 'Use default folder')"><input type="checkbox" :checked="state.receive.useDefaultDirectory" @change="setPref('useDefaultDirectory', ($event.target as HTMLInputElement).checked)" /><span class="q-toggle-track"><span class="q-toggle-knob"></span></span></label></div></div><div class="setting-row"><div><strong>{{ t('receive.autoAccept', 'Automatically accept files') }}</strong><small>{{ state.receive.useDefaultDirectory && state.receive.defaultDirectory ? t('receive.autoAcceptHint', 'Automatic acceptance is enabled when the folder is writable.') : t('receive.noDirectory', 'Configure a default folder first.') }}</small></div><label class="q-toggle" :aria-label="t('receive.autoAccept', 'Automatically accept files')"><input type="checkbox" :checked="state.receive.autoAccept" @change="setPref('autoAccept', ($event.target as HTMLInputElement).checked)" /><span class="q-toggle-track"><span class="q-toggle-knob"></span></span></label></div></section>
    </div>

    <div v-if="state.incomingConnection" class="modal-backdrop"><div class="modal" role="dialog" aria-modal="true" aria-labelledby="incoming-connection-title"><QingIcon name="phone" /><span class="section-label">{{ t('status.incoming', 'Incoming request') }}</span><h2 id="incoming-connection-title">{{ state.incomingConnection.name }}</h2><p>{{ t('status.incoming', 'Incoming request from {0}').replace('{0}', state.incomingConnection.name) }}</p><div class="actions"><button class="primary" :disabled="busy" @click="run('acceptIncomingConnection')">{{ t('actions.accept', 'Accept') }}</button><button class="secondary" :disabled="busy" @click="run('rejectIncomingConnection')">{{ t('actions.reject', 'Reject') }}</button></div></div></div>
    <div v-if="state.incomingFile" class="modal-backdrop"><div class="modal" role="dialog" aria-modal="true" aria-labelledby="incoming-file-title"><QingIcon name="folder" /><span class="section-label">{{ t('receive.incoming', 'Incoming file') }}</span><h2 id="incoming-file-title">{{ state.incomingFile.name }}</h2><span class="file-badge">{{ formatFileSize(state.incomingFile.size) }}</span><p>{{ t('receive.incomingHint', 'Choose where to save this file.') }}</p><div class="actions"><button class="primary" :disabled="fileDecisionBusy" @click="run('acceptIncomingFile')">{{ t('actions.choose', 'Choose') }}</button><button class="secondary" :disabled="fileDecisionBusy" @click="run('rejectIncomingFile')">{{ t('actions.reject', 'Reject') }}</button></div></div></div>
    <div v-if="notice" class="notice" role="status" :class="{ danger: state.lastError }"><span>{{ notice }}</span><button class="toast-close" :aria-label="t('actions.close', 'Close')" @click="dismissNotice"><QingIcon name="close" /></button></div>
  </main>
</template>
