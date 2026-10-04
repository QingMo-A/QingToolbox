<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { invoke } from '@tauri-apps/api/core'
import { getCurrentWebviewWindow, type DragDropEvent } from '@tauri-apps/api/webviewWindow'
import type { UnlistenFn } from '@tauri-apps/api/event'
import { open, save } from '@tauri-apps/plugin-dialog'
import QButton from '../design-system/components/QButton.vue'
import QIcon from '../design-system/components/QIcon.vue'
import QModal from '../design-system/components/QModal.vue'
import { useLocalization } from '../localization/localization'
import { matchesDeviceTransferTarget, type DeviceTransferTarget } from './deviceTransferTarget'
import DeviceReceiveSettings from './DeviceReceiveSettings.vue'
import { formatFileSize } from '../presentation/fileSize'

type Device = { id: string; name: string }
type Peer = { serviceName: string; displayName: string; deviceId?: string | null; platform: string; addresses: string[]; online: boolean }
type TransferState = {
  discovery: { running: boolean; peers: Peer[] }
  session: { state: 'Idle' | 'Connecting' | 'WaitingApproval' | 'Connected'; peer: Peer | null }
  incomingConnection: { name: string; platform: string } | null
  incomingFile: { name: string; size: number } | null
  receive: { defaultDirectory: string | null; useDefaultDirectory: boolean; autoAccept: boolean }
  transfer: { name: string; completed: number; total: number; receiving: boolean; phase?: 'WaitingAcceptance' | 'Transferring' } | null
  lastCompleted: string | null
  lastError: string | null
  incomingRequest?: Device | null
}

const props = withDefaults(defineProps<{ device: Device; incoming?: boolean }>(), { incoming: false })
const emit = defineEmits<{ close: []; incoming: [device: Device] }>()
const { t } = useLocalization()
const target = ref<DeviceTransferTarget | null>(null)
const state = ref<TransferState | null>(null)
const busy = ref(false)
const selecting = ref(false)
const error = ref('')
const settingsOpen = ref(false)
const dropZone = ref<HTMLButtonElement | null>(null)
const dragOver = ref(false)
const pendingPath = ref<string | null>(null)
const pendingName = ref('')
const completed = ref(false)
let pollTimer: ReturnType<typeof setInterval> | null = null
let unlistenDrop: UnlistenFn | null = null
let pollActive = false
let closed = false
let acceptedIncomingTarget = false
const connectAttempted = ref(false)
const transferStarted = ref(false)
let queuedAt = 0
const ownTransfer = computed(() => sessionMatchesTarget() ? state.value?.transfer ?? null : null)
const canChoose = computed(() => !props.incoming && !completed.value && !busy.value && !selecting.value &&
  !settingsOpen.value && !pendingPath.value && !state.value?.transfer)
const hostError = computed(() => props.incoming || transferStarted.value || connectAttempted.value ? state.value?.lastError : null)

function message(reason: unknown): string {
  if (reason instanceof Error) return reason.message
  if (reason && typeof reason === 'object' && 'message' in reason) return String(reason.message)
  return String(reason)
}

function sessionMatchesTarget(): boolean {
  const peer = state.value?.session.peer
  if (!peer) return false
  // Incoming offers are already resolved by the host against the saved paired identity.
  if (props.incoming && acceptedIncomingTarget) return true
  return !!target.value && matchesDeviceTransferTarget(peer, target.value)
}

async function call(method: string, payload: Record<string, unknown> = {}): Promise<TransferState> {
  const next = method === 'getState'
    ? await invoke<TransferState>('get_device_transfer_state')
    : await invoke<TransferState>('invoke_device_transfer', {
      peerId: props.device.id, action: { kind: method, ...payload },
    })
  if (!next?.session || !next.discovery) throw new Error(t('devices.transfer.unavailable'))
  if (!closed) state.value = next
  return next
}

function prioritizeIncoming(next: TransferState): boolean {
  if (!next.incomingRequest) return false
  pendingPath.value = null
  emit('incoming', next.incomingRequest)
  return true
}

async function poll() {
  if (pollActive || busy.value || selecting.value || closed) return
  pollActive = true
  try {
    const next = await call('getState')
    if (closed) return
    completed.value = Boolean(next.lastCompleted && (props.incoming || transferStarted.value))
    if (!props.incoming) await advanceOutgoing(next)
  } catch (reason) { if (!closed) error.value = message(reason) }
  finally { pollActive = false }
}

async function advanceOutgoing(next: TransferState) {
  if (!pendingPath.value || busy.value || closed || prioritizeIncoming(next)) return
  if (next.incomingFile || next.transfer) return
  if (next.session.peer && next.session.state !== 'Idle' && !sessionMatchesTarget()) {
    pendingPath.value = null
    error.value = t('devices.transfer.otherSession')
    return
  }
  if (Date.now() - queuedAt >= 15_000 && next.session.state !== 'Connected') {
    pendingPath.value = null
    error.value = t('devices.transfer.unavailable')
    return
  }
  if (next.session.state === 'Idle') {
    if (connectAttempted.value) {
      pendingPath.value = null
      if (!next.lastError) error.value = t('devices.transfer.unavailable')
      return
    }
    const peer = next.discovery.peers.find(item => target.value && matchesDeviceTransferTarget(item, target.value))
    if (peer) {
      connectAttempted.value = true
      if (!await action('connect')) pendingPath.value = null
    }
  } else if (next.session.state === 'Connected') {
    if (!sessionMatchesTarget()) { pendingPath.value = null; error.value = t('devices.transfer.otherSession'); return }
    const path = pendingPath.value
    pendingPath.value = null
    transferStarted.value = await action('sendFile', { path })
  }
}

async function action(method: string, payload: Record<string, unknown> = {}): Promise<boolean> {
  if (busy.value || closed) return false
  busy.value = true
  error.value = ''
  try { await call(method, payload); return !closed }
  catch (reason) { if (!closed) error.value = message(reason); return false }
  finally { busy.value = false }
}

async function submitFile(path: string) {
  if (!canChoose.value || closed || !path) return
  busy.value = true
  error.value = ''
  completed.value = false
  transferStarted.value = false
  connectAttempted.value = false
  try {
    // Incoming offers take precedence over a picker/drop racing with the receiver.
    if (prioritizeIncoming(await call('getState')) || closed) return
    target.value = await invoke<DeviceTransferTarget>('get_device_transfer_target', { peerId: props.device.id })
    if (closed) return
    const current = await call('getState')
    if (closed || prioritizeIncoming(current)) return
    if (current.transfer || current.incomingFile) throw new Error(t('devices.transfer.otherSession'))
    pendingPath.value = path
    pendingName.value = path.split(/[\\/]/).pop() || t('devices.transfer.chooseFile')
    queuedAt = Date.now()
  } catch (reason) { if (!closed) error.value = message(reason) }
  finally { busy.value = false }
  if (!closed && state.value) await advanceOutgoing(state.value)
}

async function chooseFile() {
  if (!canChoose.value || closed) return
  selecting.value = true
  let path: string | string[] | null = null
  try { path = await open({ multiple: false, directory: false, title: t('devices.transfer.chooseFile') }) }
  catch (reason) { if (!closed) error.value = message(reason) }
  finally { selecting.value = false }
  if (!closed && typeof path === 'string' && path) await submitFile(path)
}

function withinDropZone(position: { x: number; y: number }): boolean {
  if (!dropZone.value || !canChoose.value || closed) return false
  const rect = dropZone.value.getBoundingClientRect()
  // Native positions are physical WebView pixels; DOM bounds are CSS pixels including DPI/zoom.
  const scale = window.devicePixelRatio || 1
  const x = position.x / scale, y = position.y / scale
  return x >= rect.left && x <= rect.right && y >= rect.top && y <= rect.bottom
}

function nativeDrop(payload: DragDropEvent) {
  if (closed) return
  if (payload.type === 'leave') { dragOver.value = false; return }
  const inside = withinDropZone(payload.position)
  dragOver.value = payload.type !== 'drop' && inside
  if (payload.type !== 'drop' || !inside) return
  if (payload.paths.length !== 1) { error.value = t('devices.transfer.oneFile'); return }
  // Never derive paths from browser file names or arbitrary HTML drag data.
  void submitFile(payload.paths[0])
}

async function listenForDrops() {
  try {
    const cleanup = await getCurrentWebviewWindow().onDragDropEvent(event => nativeDrop(event.payload))
    if (closed) cleanup(); else unlistenDrop = cleanup
  } catch { /* The picker is still available when native drop listening is unavailable. */ }
}

async function chooseReceivePath() {
  const offer = state.value?.incomingFile
  if (!offer || busy.value || !sessionMatchesTarget()) return
  try {
    if (state.value?.receive.useDefaultDirectory && state.value.receive.defaultDirectory) {
      await action('acceptIncomingFileDefault'); return
    }
    const destinationPath = await save({ defaultPath: offer.name, title: t('devices.transfer.saveFile') })
    if (typeof destinationPath === 'string' && destinationPath) await action('acceptIncomingFile', { destinationPath })
    else await action('rejectIncomingFile')
  } catch (reason) { error.value = message(reason) }
}

async function close() {
  if (busy.value || selecting.value || ownTransfer.value) return
  closed = true
  if (pollTimer) clearInterval(pollTimer)
  unlistenDrop?.()
  unlistenDrop = null
  try { if (sessionMatchesTarget()) await invoke('invoke_device_transfer', { peerId: props.device.id, action: { kind: 'disconnect' } }) }
  catch { /* Closing the temporary file channel never changes device pairing. */ }
  emit('close')
}

onMounted(async () => {
  void listenForDrops()
  busy.value = true
  acceptedIncomingTarget = props.incoming
  pollTimer = setInterval(() => void poll(), 500)
  try {
    const current = await call('getState')
    if (closed || !props.incoming && prioritizeIncoming(current)) return
  } catch (reason) { if (!closed) error.value = message(reason) }
  finally { busy.value = false }
})
onUnmounted(() => {
  const cleanup = !closed && !state.value?.transfer && sessionMatchesTarget()
  closed = true
  unlistenDrop?.()
  if (pollTimer) clearInterval(pollTimer)
  if (cleanup) void invoke('invoke_device_transfer', { peerId: props.device.id, action: { kind: 'disconnect' } }).catch(() => undefined)
})
</script>

<template>
  <QModal :open="true" :title="t('devices.transfer.title', { name: device.name })" :busy="busy || selecting || !!ownTransfer" :close-label="t('devices.transfer.close')" @close="close">
    <p v-if="error" class="transfer-error" role="alert">{{ error }}</p>
    <div v-if="completed" class="transfer-success" role="status">
      <QIcon name="statusSuccess" :size="32" /><strong>{{ t('devices.transfer.done') }}</strong>
    </div>
    <template v-else>
      <p v-if="hostError && hostError !== error" class="transfer-error" role="alert">{{ hostError }}</p>
      <div v-if="state?.incomingFile && sessionMatchesTarget()" class="transfer-section">
        <strong>{{ t('devices.transfer.receiveFile', { name: state.incomingFile.name }) }}</strong>
        <span>{{ t('devices.transfer.fileSize', { size: formatFileSize(state.incomingFile.size) }) }}</span>
        <div class="transfer-actions"><QButton variant="primary" :disabled="busy" @click="chooseReceivePath">{{ t('devices.transfer.save') }}</QButton><QButton :disabled="busy" @click="action('rejectIncomingFile')">{{ t('devices.transfer.reject') }}</QButton></div>
      </div>
      <div v-else-if="ownTransfer" class="transfer-progress" role="status">
        <div class="transfer-file-mark"><QIcon name="folder" :size="25" /></div>
        <strong>{{ ownTransfer.name }}</strong>
        <span>{{ t(ownTransfer.phase === 'WaitingAcceptance' ? 'devices.transfer.awaitingReceive' : ownTransfer.receiving ? 'devices.transfer.receiving' : 'devices.transfer.sending') }} · {{ formatFileSize(ownTransfer.completed) }} / {{ formatFileSize(ownTransfer.total) }}</span>
        <progress :aria-label="t('devices.transfer.progress')" :value="ownTransfer.completed" :max="Math.max(1, ownTransfer.total)" />
      </div>
      <div v-else-if="pendingPath" class="transfer-progress is-preparing" role="status">
        <div class="transfer-file-mark"><QIcon name="folder" :size="25" /></div>
        <strong>{{ pendingName }}</strong><span>{{ t('devices.transfer.preparing') }}</span>
      </div>
      <button v-else-if="!incoming" ref="dropZone" type="button" class="transfer-drop-zone" :class="{ 'is-drag-over': dragOver }" :disabled="!canChoose" :aria-label="t('devices.transfer.chooseFile')" @click="chooseFile" @dragover.prevent @drop.prevent>
        <span class="transfer-drop-icon"><QIcon name="folder" :size="30" /></span>
        <strong>{{ t(dragOver ? 'devices.transfer.dropToSend' : 'devices.transfer.dropFile') }}</strong>
        <span>{{ t('devices.transfer.clickToChoose') }}</span>
      </button>
    </template>
    <template #actions>
      <QButton v-if="!completed" :disabled="busy || selecting" @click="settingsOpen = true">{{ t('devices.transfer.receiveSettings') }}</QButton>
      <QButton :disabled="busy || selecting || !!ownTransfer" @click="close">{{ t('devices.transfer.close') }}</QButton>
    </template>
  </QModal>
  <DeviceReceiveSettings v-if="settingsOpen" @close="settingsOpen = false" />
</template>

<style scoped>
.transfer-error{margin:0;color:var(--q-danger,#c43f4e);font-size:13px;overflow-wrap:anywhere}
.transfer-success{display:flex;min-height:130px;gap:12px;align-items:center;justify-content:center;color:var(--q-success,#16825d);font-size:18px;animation:transfer-enter 220ms ease-out}
.transfer-section{display:grid;gap:10px;padding:14px;border:1px solid var(--q-border);border-radius:14px;background:var(--q-surface-soft)}
.transfer-section strong{font-size:14px;overflow-wrap:anywhere}.transfer-actions{display:flex;gap:8px}
.transfer-drop-zone{display:flex;min-height:185px;width:100%;flex-direction:column;align-items:center;justify-content:center;gap:10px;padding:26px 16px;border:1.5px dashed color-mix(in srgb,var(--q-brand) 36%,var(--q-border));border-radius:15px;background:var(--q-surface-soft);color:var(--q-text);font:inherit;cursor:pointer;transition:background 180ms ease,border-color 180ms ease,transform 180ms ease;animation:transfer-enter 220ms ease-out}
.transfer-drop-zone strong{font-size:15px;font-weight:650}.transfer-drop-zone>span:last-child{color:var(--q-text-2);font-size:12px}
.transfer-drop-icon,.transfer-file-mark{display:grid;place-items:center;width:54px;height:54px;border-radius:16px;background:var(--q-brand-soft);color:var(--q-brand);transition:transform 180ms ease}
.transfer-drop-zone:hover:not(:disabled),.transfer-drop-zone.is-drag-over{border-color:var(--q-brand);background:color-mix(in srgb,var(--q-brand) 8%,var(--q-surface))}
.transfer-drop-zone.is-drag-over .transfer-drop-icon{transform:translateY(-4px) scale(1.06)}
.transfer-drop-zone:focus-visible{outline:2px solid var(--q-brand);outline-offset:3px}.transfer-drop-zone:disabled{opacity:.55;cursor:default}
.transfer-progress{display:grid;grid-template-columns:46px minmax(0,1fr);gap:8px 13px;padding:20px 16px;border:1px solid var(--q-border);border-radius:15px;background:var(--q-surface-soft);animation:transfer-enter 220ms ease-out}
.transfer-file-mark{grid-row:1/3;width:46px;height:46px;border-radius:13px}.transfer-progress strong{font-size:14px;overflow-wrap:anywhere;align-self:end}.transfer-progress>span{font-size:12px;color:var(--q-text-2);font-variant-numeric:tabular-nums}
progress{grid-column:1/-1;margin-top:11px;width:100%;height:7px;border:0;border-radius:10px;overflow:hidden;accent-color:var(--q-brand)}
progress::-webkit-progress-bar{background:var(--q-border);border-radius:10px}progress::-webkit-progress-value{background:var(--q-brand);border-radius:10px;transition:width 180ms ease}
@keyframes transfer-enter{from{opacity:0;transform:translateY(4px)}to{opacity:1;transform:translateY(0)}}
@media(prefers-reduced-motion:reduce){.transfer-drop-zone,.transfer-drop-icon,progress::-webkit-progress-value{transition:none}.transfer-progress,.transfer-success,.transfer-drop-zone{animation:none}}
</style>
