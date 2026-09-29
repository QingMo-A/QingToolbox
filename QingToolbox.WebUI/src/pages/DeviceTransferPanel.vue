<script setup lang="ts">
import { onMounted, onUnmounted, ref } from 'vue'
import { invoke } from '@tauri-apps/api/core'
import { open, save } from '@tauri-apps/plugin-dialog'
import QButton from '../design-system/components/QButton.vue'
import QModal from '../design-system/components/QModal.vue'
import { useLocalization } from '../localization/localization'
import { matchesDeviceTransferTarget, type DeviceTransferTarget } from './deviceTransferTarget'
import DeviceReceiveSettings from './DeviceReceiveSettings.vue'

type Device = { id: string; name: string }
type Peer = { serviceName: string; displayName: string; deviceId?: string | null; platform: string; addresses: string[]; online: boolean }
type TransferState = {
  discovery: { running: boolean; peers: Peer[] }
  session: { state: 'Idle' | 'Connecting' | 'WaitingApproval' | 'Connected'; peer: Peer | null }
  incomingConnection: { name: string; platform: string } | null
  incomingFile: { name: string; size: number } | null
  receive: { defaultDirectory: string | null; useDefaultDirectory: boolean; autoAccept: boolean }
  transfer: { name: string; completed: number; total: number; receiving: boolean } | null
  lastCompleted: string | null
  lastError: string | null
}

const props = withDefaults(defineProps<{ device: Device; incoming?: boolean }>(), { incoming: false })
const emit = defineEmits<{ close: [] }>()
const moduleId = 'qing.qingtransfer'
const { t } = useLocalization()
const target = ref<DeviceTransferTarget | null>(null)
const state = ref<TransferState | null>(null)
const busy = ref(false)
const error = ref('')
const settingsOpen = ref(false)
let pollTimer: ReturnType<typeof setInterval> | null = null
let pollActive = false
let closed = false
let acceptedIncomingTarget = false
let pendingPath: string | null = null
let connectAttempted = false
let transferStarted = false
const completed = ref(false)

function message(reason: unknown): string {
  if (reason instanceof Error) return reason.message
  if (reason && typeof reason === 'object' && 'message' in reason) return String(reason.message)
  return String(reason)
}

function matchingPeer(): Peer | null {
  const selected = target.value
  if (!selected) return null
  return state.value?.discovery.peers.find(peer => matchesDeviceTransferTarget(peer, selected)) ?? null
}

function sessionMatchesTarget(): boolean {
  const peer = state.value?.session.peer
  if (!peer) return false
  // Incoming offers are emitted by the host only after it has authenticated the
  // transfer session against a paired device. Re-resolving the peer through
  // nearby discovery here is both redundant and racy: discovery can briefly be
  // between announcements while the already-established socket is healthy.
  if (props.incoming && acceptedIncomingTarget) return true
  const selected = target.value
  if (!selected) return false
  if (acceptedIncomingTarget && peer.platform === selected.platform &&
    peer.addresses.some(address => selected.addresses.includes(address))) return true
  return matchesDeviceTransferTarget(peer, selected)
}

async function call(method: string, payload: Record<string, unknown> = {}): Promise<TransferState> {
  const next = await invoke<TransferState>('invoke_module', { moduleId, method, payload })
  if (!next || !next.session || !next.discovery) throw new Error(t('devices.transfer.unavailable'))
  if (!closed) state.value = next
  return next
}

async function poll() {
  if (pollActive || busy.value || closed) return
  pollActive = true
  try {
    const next = await call('getState')
    completed.value = Boolean(next.lastCompleted && (props.incoming || transferStarted))
    if (!props.incoming) await advanceOutgoing(next)
  } catch (reason) { error.value = message(reason) }
  finally { pollActive = false }
}

async function advanceOutgoing(next: TransferState) {
  if (!pendingPath || busy.value || closed) return
  if (next.session.state === 'Idle' && !connectAttempted) {
    const peer = next.discovery.peers.find(item => target.value && matchesDeviceTransferTarget(item, target.value))
    if (peer) {
      connectAttempted = true
      await action('connect', { serviceName: peer.serviceName })
    }
  } else if (next.session.state === 'Connected' && sessionMatchesTarget()) {
    const path = pendingPath
    pendingPath = null
    await action('sendFile', { path })
  }
}

async function action(method: string, payload: Record<string, unknown> = {}) {
  if (busy.value) return
  busy.value = true
  error.value = ''
  try {
    await call(method, payload)
    if (method === 'acceptIncomingConnection') acceptedIncomingTarget = true
    if (method === 'disconnect' || method === 'rejectIncomingConnection') acceptedIncomingTarget = false
  }
  catch (reason) { error.value = message(reason) }
  finally { busy.value = false }
}

async function chooseAndSend() {
  if (busy.value || !target.value) return
  try {
    const path = await open({ multiple: false, directory: false, title: t('devices.transfer.chooseFile') })
    if (typeof path === 'string' && path) {
      pendingPath = path
      completed.value = false
      transferStarted = true
      connectAttempted = false
      if (state.value) await advanceOutgoing(state.value)
      return true
    }
  } catch (reason) { error.value = message(reason) }
  return false
}

async function chooseReceivePath() {
  const offer = state.value?.incomingFile
  if (!offer || busy.value || !sessionMatchesTarget()) return
  try {
    if (state.value?.receive.useDefaultDirectory && state.value.receive.defaultDirectory) {
      await action('acceptIncomingFileDefault')
      return
    }
    const destinationPath = await save({ defaultPath: offer.name, title: t('devices.transfer.saveFile') })
    if (typeof destinationPath === 'string' && destinationPath) await action('acceptIncomingFile', { destinationPath })
    else await action('rejectIncomingFile')
  } catch (reason) { error.value = message(reason) }
}

async function close() {
  if (busy.value || (state.value?.transfer && sessionMatchesTarget())) return
  closed = true
  if (pollTimer) clearInterval(pollTimer)
  try { if (sessionMatchesTarget()) await invoke('invoke_module', { moduleId, method: 'disconnect', payload: {} }) }
  catch { /* The host may already be shutting down. */ }
  emit('close')
}

onMounted(async () => {
  busy.value = true
  try {
    acceptedIncomingTarget = props.incoming
    if (closed) return
    if (!props.incoming) {
      target.value = await invoke<DeviceTransferTarget>('get_device_transfer_target', { peerId: props.device.id })
      if (closed) return
      const path = await open({ multiple: false, directory: false, title: t('devices.transfer.chooseFile') })
      if (closed) return
      if (typeof path !== 'string' || !path) { closed = true; emit('close'); return }
      pendingPath = path
      transferStarted = true
    }
    await invoke('list_modules')
    if (closed) return
    await invoke('start_module', { moduleId })
    if (closed) return
    await invoke('set_module_active', { moduleId, active: true })
    if (closed) return
    const next = await call('getState')
    if (closed) return
    pollTimer = setInterval(() => void poll(), 500)
    if (!props.incoming) await advanceOutgoing(next)
  } catch (reason) { error.value = message(reason) }
  finally { busy.value = false }
})
onUnmounted(() => {
  const cleanup = !closed && !state.value?.transfer && sessionMatchesTarget()
  closed = true
  if (pollTimer) clearInterval(pollTimer)
  if (cleanup) {
    void invoke('invoke_module', { moduleId, method: 'disconnect', payload: {} }).catch(() => undefined)
  }
})
</script>

<template>
  <QModal :open="true" :title="t('devices.transfer.title', { name: device.name })" :busy="busy || (!!state?.transfer && sessionMatchesTarget())" :close-label="t('devices.transfer.close')" @close="close">
    <p v-if="error" class="transfer-error" role="alert">{{ error }}</p>
    <template v-if="state">
      <div v-if="completed" class="transfer-success" role="status">
        <strong>{{ t('devices.transfer.done') }}</strong>
      </div>
      <template v-else>
        <p v-if="state.lastError" class="transfer-error" role="alert">{{ state.lastError }}</p>
        <div v-if="state.incomingFile && sessionMatchesTarget()" class="transfer-section">
          <strong>{{ t('devices.transfer.receiveFile', { name: state.incomingFile.name }) }}</strong>
          <span>{{ t('devices.transfer.fileSize', { size: state.incomingFile.size }) }}</span>
          <div class="transfer-actions"><QButton variant="primary" :disabled="busy" @click="chooseReceivePath">{{ t('devices.transfer.save') }}</QButton><QButton :disabled="busy" @click="action('rejectIncomingFile')">{{ t('devices.transfer.reject') }}</QButton></div>
        </div>
        <p v-if="state.transfer && sessionMatchesTarget()" role="status">{{ t(state.transfer.receiving ? 'devices.transfer.receiving' : 'devices.transfer.sending') }} {{ state.transfer.name }} · {{ state.transfer.completed }} / {{ state.transfer.total }} {{ t('devices.transfer.bytes') }}</p>
        <progress v-if="state.transfer && sessionMatchesTarget()" :value="state.transfer.completed" :max="Math.max(1, state.transfer.total)" />
        <p v-if="state.session.state === 'Connecting' || state.session.state === 'WaitingApproval'" role="status">{{ t(state.session.state === 'Connecting' ? 'devices.transfer.connecting' : 'devices.transfer.waiting') }}</p>
        <p v-else-if="state.session.state === 'Connected' && sessionMatchesTarget()" role="status">{{ t('devices.transfer.connected') }}</p>
        <p v-else-if="state.session.state === 'Connected'" role="status">{{ t('devices.transfer.otherSession') }}</p>
        <p v-else-if="!matchingPeer()" role="status">{{ t(state.discovery.running ? 'devices.transfer.searching' : 'devices.transfer.serviceStopped') }}</p>
        <p v-else-if="pendingPath" role="status">{{ t('devices.transfer.selected') }}</p>
      </template>
    </template>
    <template #actions>
      <QButton v-if="!completed && !incoming && !pendingPath && !state?.transfer" variant="primary" :disabled="busy" @click="chooseAndSend">{{ t(state?.session.state === 'Connected' ? 'devices.transfer.send' : 'devices.transfer.chooseFile') }}</QButton>
      <QButton v-if="!completed" :disabled="busy" @click="settingsOpen = true">{{ t('devices.transfer.receiveSettings') }}</QButton>
      <QButton :disabled="busy || (!!state?.transfer && sessionMatchesTarget())" @click="close">{{ t('devices.transfer.close') }}</QButton>
    </template>
  </QModal>
  <DeviceReceiveSettings v-if="settingsOpen" @close="settingsOpen = false" />
</template>

<style scoped>
.transfer-error{margin:0;color:var(--q-color-danger,#c43f4e);font-size:13px}
.transfer-success{display:flex;min-height:72px;align-items:center;justify-content:center;color:var(--q-color-success,#16825d);font-size:18px}
.transfer-section{display:grid;gap:10px;padding:12px;border:1px solid var(--q-border);border-radius:12px;background:var(--q-surface-soft)}
.transfer-section strong{font-size:14px}.transfer-actions{display:flex;gap:8px}.transfer-peer{margin:0;color:var(--q-text-2);font-size:13px;overflow-wrap:anywhere}
progress{width:100%;accent-color:var(--q-brand)}
</style>
