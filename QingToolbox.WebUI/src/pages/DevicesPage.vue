<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref } from 'vue'
import { invoke } from '@tauri-apps/api/core'
import { useAppStore } from '../app/store'
import QPage from '../design-system/components/QPage.vue'
import QIcon from '../design-system/components/QIcon.vue'
import QButton from '../design-system/components/QButton.vue'
import QModal from '../design-system/components/QModal.vue'
import QModalLabel from '../design-system/components/QModalLabel.vue'
import QModalInput from '../design-system/components/QModalInput.vue'
import { useLocalization } from '../localization/localization'
import { TauriTransport } from '../bridge/transport/TauriTransport'
import DeviceReceiveSettings from './DeviceReceiveSettings.vue'

type DeviceGroup = 'intimate' | 'connected'
type NearbyDevice = { id: string; name: string; platform: 'windows' | 'android'; status: 'Unverified' }
type PairedDevice = { id: string; discoveryId: string; name: string; remark?: string | null; platform: 'windows' | 'android'; relationship: 'Connected' | 'Intimate' }
type PendingPair = { sessionId: string; discoveryId: string; name: string; platform: 'windows' | 'android'; code: string; incoming: boolean; localApproved: boolean }
type DeviceAction = 'upgrade' | 'disconnect' | 'demote'
type PendingDeviceAction = { sessionId: string; peerId: string; name: string; action: DeviceAction; localApproved: boolean }
type DeviceNotice = { id: string; peerName: string; action: DeviceAction }
type DeviceBattery = { peerId: string; percent: number; charging: boolean; receivedAtMs: number }
type PairingSnapshot = { error: string | null; pending: PendingPair[]; paired: PairedDevice[]; actions?: PendingDeviceAction[]; notices?: DeviceNotice[]; batteries?: DeviceBattery[]; revocations?: PairedDevice[]; online?: string[] }
type DeviceSnapshot = { enabled: boolean; error: string | null; nearby: NearbyDevice[]; pairing?: PairingSnapshot }

const app = useAppStore()
const { t } = useLocalization()
const selectedGroup = ref<DeviceGroup>('intimate')
const receiveSettingsOpen = ref(false)
const pairedScroll = ref<HTMLElement | null>(null)
const groups: DeviceGroup[] = ['intimate', 'connected']
const deviceName = computed(() => app.snapshot?.deviceName?.trim() || null)
const deviceSnapshot = ref<DeviceSnapshot | null>(null)
const discoveryBusy = ref(false)
const actionBusy = ref<string | null>(null)
const actionError = ref<string | null>(null)
const dismissedError = ref<string | null>(null)
const remarkDevice = ref<PairedDevice | null>(null)
const remarkDraft = ref('')
const remarkBusy = ref(false)
const remarkError = ref<string | null>(null)
const seenNotices = ref<string[]>([])
const rejectedSessions = ref<string[]>([])
const currentNotice = computed(() => deviceSnapshot.value?.pairing?.notices?.find(notice => !seenNotices.value.includes(notice.id)))
const currentPair = computed(() => deviceSnapshot.value?.pairing?.pending.find(pending => !rejectedSessions.value.includes(pending.sessionId)) ?? null)
const currentAction = computed(() => deviceSnapshot.value?.pairing?.actions?.find(pending => !rejectedSessions.value.includes(pending.sessionId)) ?? null)
const currentError = computed(() => actionError.value || deviceSnapshot.value?.pairing?.error || null)
const dialogKind = computed(() => remarkDevice.value ? 'remark' : currentPair.value ? 'pair' : currentAction.value ? 'action' : currentNotice.value ? 'notice' : currentError.value && currentError.value !== dismissedError.value ? 'error' : null)
let refreshTimer: ReturnType<typeof setInterval> | null = null
let refreshGeneration = 0
const localStatus = computed(() => {
  if (deviceSnapshot.value?.error) return t('devices.local.error')
  if (deviceSnapshot.value?.enabled) return t('devices.local.discovering')
  return app.bridge === 'Connected' ? t('devices.local.disabled') : t('devices.local.connecting')
})
const emptyTitle = computed(() => t(`devices.list.${selectedGroup.value}Empty`))
const pairedDevices = computed(() => deviceSnapshot.value?.pairing?.paired.filter(device => device.relationship === (selectedGroup.value === 'intimate' ? 'Intimate' : 'Connected')) ?? [])
const groupCounts = computed(() => ({
  intimate: deviceSnapshot.value?.pairing?.paired.filter(device => device.relationship === 'Intimate').length ?? 0,
  connected: deviceSnapshot.value?.pairing?.paired.filter(device => device.relationship === 'Connected').length ?? 0,
}))
const onlineIds = computed(() => new Set(deviceSnapshot.value?.pairing?.online ?? []))
const nearbyDevices = computed(() => deviceSnapshot.value?.nearby.filter(device => !deviceSnapshot.value?.pairing?.paired.some(paired => paired.discoveryId === device.id)) ?? [])
function batteryFor(peerId: string) {
  const battery = deviceSnapshot.value?.pairing?.batteries?.find(value => value.peerId === peerId)
  return battery && Date.now() - battery.receivedAtMs < 130_000 ? battery : null
}
function applySnapshot(snapshot: DeviceSnapshot) {
  const previous = deviceSnapshot.value?.pairing?.paired ?? []
  if (deviceSnapshot.value && snapshot.pairing?.paired.some(device => device.relationship === 'Connected' && !previous.some(known => known.id === device.id))) {
    selectedGroup.value = 'connected'
    if (pairedScroll.value) pairedScroll.value.scrollTop = 0
  }
  deviceSnapshot.value = snapshot
  if (!snapshot.pairing?.error && !actionError.value) dismissedError.value = null
}
function closeDialog() {
  if (dialogKind.value === 'pair' && currentPair.value && !currentPair.value.localApproved) {
    const sessionId = currentPair.value.sessionId
    rejectedSessions.value.push(sessionId)
    void deviceAction('decide_device_pairing', { sessionId, approve: false }, sessionId)
  } else if (dialogKind.value === 'action' && currentAction.value && !currentAction.value.localApproved) {
    const sessionId = currentAction.value.sessionId
    rejectedSessions.value.push(sessionId)
    void deviceAction('decide_device_action', { sessionId, approve: false }, sessionId)
  } else if (dialogKind.value === 'notice' && currentNotice.value) {
    const id = currentNotice.value.id
    seenNotices.value.push(id)
    // Consume in the process-owned inbox, not a page-scoped cache.
    void invoke('acknowledge_device_notice', { noticeId: id }).catch(() => {
      seenNotices.value = seenNotices.value.filter(value => value !== id)
    })
  } else if (dialogKind.value === 'error') {
    dismissedError.value = currentError.value
    actionError.value = null
  }
}
async function deviceAction(command: string, args: Record<string, unknown>, key: string) {
  if (actionBusy.value) return
  actionBusy.value = key
  actionError.value = null
  const generation = ++refreshGeneration
  try {
    const snapshot = await invoke<DeviceSnapshot>(command, args)
    if (generation === refreshGeneration) applySnapshot(snapshot)
  } catch (error) {
    if (generation === refreshGeneration) {
      actionError.value = typeof error === 'object' && error !== null && 'message' in error && typeof error.message === 'string'
        ? error.message : t('devices.pairing.failed')
    }
  } finally {
    actionBusy.value = null
  }
}
async function refreshDevices() {
  if (remarkBusy.value || actionBusy.value || discoveryBusy.value) return
  const generation = ++refreshGeneration
  try {
    const snapshot = await invoke<DeviceSnapshot>('get_devices_snapshot')
    if (generation === refreshGeneration) applySnapshot(snapshot)
  } catch {
    if (generation === refreshGeneration) deviceSnapshot.value = { enabled: false, error: t('devices.local.error'), nearby: [] }
  }
}
async function toggleDiscovery() {
  if (discoveryBusy.value) return
  discoveryBusy.value = true
  const generation = ++refreshGeneration
  try {
    const snapshot = await invoke<DeviceSnapshot>('set_devices_discovery_enabled', { enabled: !deviceSnapshot.value?.enabled })
    if (generation === refreshGeneration) applySnapshot(snapshot)
  } catch {
    if (generation === refreshGeneration) deviceSnapshot.value = { enabled: false, error: t('devices.local.error'), nearby: [] }
  } finally {
    discoveryBusy.value = false
  }
}
onMounted(() => {
  if (!TauriTransport.isAvailable()) return
  void refreshDevices()
  refreshTimer = setInterval(() => void refreshDevices(), 2500)
})
onUnmounted(() => {
  refreshGeneration++
  if (refreshTimer) clearInterval(refreshTimer)
})
function selectGroup(group: DeviceGroup) {
  if (selectedGroup.value === group) return
  selectedGroup.value = group
  if (pairedScroll.value) pairedScroll.value.scrollTop = 0
}
function onTabKeydown(event: KeyboardEvent, group: DeviceGroup) {
  const step = event.key === 'ArrowRight' ? 1 : event.key === 'ArrowLeft' ? -1 : 0
  if (!step && event.key !== 'Home' && event.key !== 'End') return
  event.preventDefault()
  const index = event.key === 'Home' ? 0 : event.key === 'End' ? groups.length - 1 : (groups.indexOf(group) + step + groups.length) % groups.length
  selectGroup(groups[index])
  void nextTick(() => document.getElementById(`device-tab-${selectedGroup.value}`)?.focus())
}
function openTransfer(device: PairedDevice) {
  window.dispatchEvent(new CustomEvent('qing:open-device-transfer', { detail: { id: device.id, name: displayDeviceName(device) } }))
}
function displayDeviceName(device: PairedDevice) {
  return device.remark ? `${device.remark}(${device.name})` : device.name
}
function editRemark(device: PairedDevice) {
  if (actionBusy.value || dialogKind.value) return
  remarkDevice.value = device
  remarkDraft.value = device.remark ?? ''
  remarkError.value = null
}
function closeRemark() {
  if (!remarkBusy.value) remarkDevice.value = null
}
async function saveRemark() {
  if (!remarkDevice.value || remarkBusy.value) return
  const remark = remarkDraft.value.trim()
  if (Array.from(remark).length > 80 || /[\u0000-\u001f\u007f-\u009f]/.test(remark)) {
    remarkError.value = t('devices.remark.invalid')
    return
  }
  remarkBusy.value = true
  remarkError.value = null
  const generation = ++refreshGeneration
  try {
    const snapshot = await invoke<DeviceSnapshot>('set_device_remark', { peerId: remarkDevice.value.id, remark })
    if (generation === refreshGeneration) {
      applySnapshot(snapshot)
      remarkDevice.value = null
    }
  } catch {
    if (generation === refreshGeneration) remarkError.value = t('devices.remark.failed')
  } finally {
    remarkBusy.value = false
  }
}
</script>

<template>
  <QPage class="devices-page">
    <div class="devices-workspace">
      <header class="devices-heading">
        <div>
          <span class="devices-eyebrow">QINGTRANSFER</span>
          <h1>{{ t('devices.page.title') }}</h1>
        </div>
        <QButton @click="receiveSettingsOpen = true">{{ t('devices.transfer.receiveSettings') }}</QButton>
      </header>

      <div class="devices-upper">
        <section class="devices-local" :aria-label="deviceName || t('devices.local.nameUnavailable')">
          <div class="devices-local-main">
            <div class="devices-local-mark"><QIcon name="desktopDevice" :size="38" /></div>
            <div class="devices-local-content">
              <h2 :data-selectable="deviceName ? '' : undefined">{{ deviceName || t('devices.local.nameUnavailable') }}</h2>
              <span class="devices-platform">{{ t('devices.local.platform') }}</span>
            </div>
          </div>
          <div class="devices-local-state" role="status">
            <span class="devices-state-dot" :class="{ online: deviceSnapshot?.enabled }" />{{ localStatus }}
          </div>
          <button v-if="deviceSnapshot" class="devices-discovery-button" type="button" :disabled="discoveryBusy" @click="toggleDiscovery">
            {{ t(deviceSnapshot.enabled ? 'devices.local.disableDiscovery' : 'devices.local.enableDiscovery') }}
          </button>
          <p v-if="deviceSnapshot?.error" class="devices-error" role="alert">{{ deviceSnapshot.error }}</p>
        </section>

        <section class="devices-paired" :aria-label="t('devices.list.pairedTitle')">
          <div class="devices-tabs" role="tablist" :aria-label="t('devices.list.pairedTitle')">
            <button
              v-for="group in groups"
              :key="group"
              type="button"
              role="tab"
              :id="`device-tab-${group}`"
              :aria-selected="selectedGroup === group"
              aria-controls="device-group-panel"
              :tabindex="selectedGroup === group ? 0 : -1"
              :class="{ active: selectedGroup === group }"
              @click="selectGroup(group)"
              @keydown="onTabKeydown($event, group)"
            >{{ t(`devices.list.${group}`) }} ({{ groupCounts[group] }})</button>
          </div>
          <div ref="pairedScroll" class="devices-paired-scroll">
            <Transition name="devices-swap" mode="out-in">
              <div
                :key="selectedGroup"
                id="device-group-panel"
                :class="pairedDevices.length ? 'devices-paired-list' : 'devices-empty devices-empty-compact'"
                role="tabpanel"
                :aria-labelledby="`device-tab-${selectedGroup}`"
              >
                <template v-if="pairedDevices.length">
                  <div v-for="device in pairedDevices" :key="device.id" class="devices-paired-item">
                    <span class="devices-nearby-icon" :data-platform="device.platform"><QIcon :name="device.platform === 'android' ? 'phoneDevice' : 'desktopDevice'" :size="20" /></span>
                    <div class="devices-nearby-details">
                      <div class="devices-peer-name">
                        <strong :title="displayDeviceName(device)">{{ displayDeviceName(device) }}</strong>
                        <button class="devices-remark-action" type="button" :title="t('devices.remark.title')" :aria-label="t('devices.remark.edit', { name: displayDeviceName(device) })" :disabled="Boolean(actionBusy || dialogKind)" @click="editRemark(device)"><QIcon name="edit" :size="16" /></button>
                      </div>
                      <small class="devices-peer-meta"><span>{{ t(device.platform === 'android' ? 'devices.list.android' : 'devices.list.windows') }}</span><span class="devices-presence" :class="{ online: onlineIds.has(device.id) }" :title="t('devices.presence.hint')"><i aria-hidden="true" />{{ t(onlineIds.has(device.id) ? 'devices.presence.online' : 'devices.presence.offline') }}</span><span v-if="batteryFor(device.id)">{{ t('devices.battery.status', { percent: batteryFor(device.id)?.percent ?? 0, charging: batteryFor(device.id)?.charging ? t('devices.battery.charging') : '' }) }}</span></small>
                    </div>
                    <div class="devices-peer-actions">
                    <button class="devices-small-action" type="button" :disabled="Boolean(actionBusy)" @click="deviceAction('set_device_relationship', { peerId: device.id, intimate: selectedGroup !== 'intimate' }, device.id)">
                      {{ t(selectedGroup === 'intimate' ? 'devices.pairing.demote' : 'devices.pairing.promote') }}
                    </button>
                    <button class="devices-small-action devices-revoke" type="button" :disabled="Boolean(actionBusy)" @click="deviceAction('revoke_device_pairing', { peerId: device.id }, device.id)">{{ t('devices.pairing.revoke') }}</button>
                    <button class="devices-small-action devices-transfer-action" type="button" :disabled="!onlineIds.has(device.id)" :title="t('devices.transfer.action')" :aria-label="t('devices.transfer.action')" @click="openTransfer(device)"><QIcon name="folder" :size="17" /></button>
                    </div>
                  </div>
                </template>
                <template v-else>
                  <div class="devices-empty-icon"><QIcon name="devices" :size="25" /></div>
                  <p>{{ emptyTitle }}</p>
                </template>
              </div>
            </Transition>
          </div>
        </section>
      </div>

      <section class="devices-nearby" aria-labelledby="nearby-devices-title">
        <header class="devices-nearby-heading">
          <h2 id="nearby-devices-title">{{ t('devices.list.stranger') }}</h2>
          <span>{{ t('devices.list.unverifiedHint') }}</span>
        </header>
        <div class="devices-nearby-scroll">
          <p v-if="deviceSnapshot?.pairing?.revocations?.length" class="devices-pair-error" role="status">{{ t('devices.action.pendingOffline', { count: deviceSnapshot.pairing.revocations.length }) }}</p>
          <ul v-if="nearbyDevices.length" class="devices-nearby-list">
            <li v-for="device in nearbyDevices" :key="device.id" class="devices-nearby-item">
              <span class="devices-nearby-icon" :data-platform="device.platform"><QIcon :name="device.platform === 'android' ? 'phoneDevice' : 'desktopDevice'" :size="22" /></span>
              <span class="devices-nearby-details"><strong>{{ device.name }}</strong><small>{{ t(device.platform === 'android' ? 'devices.list.android' : 'devices.list.windows') }}</small></span>
              <span class="devices-unverified">{{ t('devices.list.unverified') }}</span>
              <button v-if="!deviceSnapshot?.pairing?.error" type="button" class="devices-small-action" :disabled="Boolean(actionBusy)" @click="deviceAction('request_device_pairing', { deviceId: device.id }, device.id)">{{ t('devices.pairing.request') }}</button>
            </li>
          </ul>
          <div v-else-if="!deviceSnapshot?.pairing?.pending.length" class="devices-empty">
            <div class="devices-empty-icon"><QIcon name="devices" :size="32" /></div>
            <p>{{ t(deviceSnapshot?.enabled ? 'devices.list.searching' : 'devices.list.strangerEmpty') }}</p>
          </div>
        </div>
      </section>
    </div>
  </QPage>
  <DeviceReceiveSettings v-if="receiveSettingsOpen" @close="receiveSettingsOpen = false" />
  <QModal :open="dialogKind === 'remark'" :title="t('devices.remark.title')" :busy="remarkBusy" :close-label="t('devices.transfer.close')" @close="closeRemark">
    <QModalInput v-model="remarkDraft" :label="t('devices.remark.label')" :placeholder="remarkDevice?.name" :disabled="remarkBusy" @keydown.enter.prevent="saveRemark" />
    <QModalLabel v-if="remarkError" class="devices-remark-error" role="alert">{{ remarkError }}</QModalLabel>
    <template #actions><QButton variant="primary" :disabled="remarkBusy" @click="saveRemark">{{ t('devices.remark.confirm') }}</QButton></template>
  </QModal>
  <QModal :open="dialogKind === 'pair'" :title="currentPair ? t(currentPair.incoming ? 'devices.pairing.incoming' : 'devices.pairing.outgoing') : ''" :busy="Boolean(currentPair?.localApproved || actionBusy)" :close-label="t('devices.pairing.reject')" @close="closeDialog">
    <QModalLabel>{{ currentPair?.name }}</QModalLabel>
    <QModalLabel>{{ t('devices.pairing.compare') }}</QModalLabel>
    <div v-if="currentPair" class="devices-pair-code" :aria-label="t('devices.pairing.code')">{{ currentPair.code.slice(0, 4) }} {{ currentPair.code.slice(4) }}</div>
    <template #actions>
      <span v-if="currentPair?.localApproved" class="devices-modal-waiting">{{ t('devices.pairing.waiting') }}</span>
      <template v-else-if="currentPair">
        <QButton :disabled="Boolean(actionBusy)" @click="closeDialog">{{ t('devices.pairing.reject') }}</QButton>
        <QButton variant="primary" :disabled="Boolean(actionBusy)" @click="deviceAction('decide_device_pairing', { sessionId: currentPair.sessionId, approve: true }, currentPair.sessionId)">{{ t('devices.pairing.confirm') }}</QButton>
      </template>
    </template>
  </QModal>
  <QModal :open="dialogKind === 'action'" :title="currentAction ? t(`devices.action.request.${currentAction.action}`) : ''" :busy="Boolean(currentAction?.localApproved || actionBusy)" :close-label="t('devices.pairing.reject')" @close="closeDialog">
    <QModalLabel>{{ currentAction?.name }}</QModalLabel>
    <QModalLabel>{{ t('devices.action.confirmHint') }}</QModalLabel>
    <template #actions>
      <span v-if="currentAction?.localApproved" class="devices-modal-waiting">{{ t('devices.pairing.waiting') }}</span>
      <template v-else-if="currentAction">
        <QButton :disabled="Boolean(actionBusy)" @click="closeDialog">{{ t('devices.pairing.reject') }}</QButton>
        <QButton variant="primary" :disabled="Boolean(actionBusy)" @click="deviceAction('decide_device_action', { sessionId: currentAction.sessionId, approve: true }, currentAction.sessionId)">{{ t('devices.action.approve') }}</QButton>
      </template>
    </template>
  </QModal>
  <QModal :open="dialogKind === 'notice'" :title="t('devices.action.noticeTitle')" :close-label="t('devices.action.acknowledge')" @close="closeDialog">
    <QModalLabel v-if="currentNotice">{{ t(`devices.action.done.${currentNotice.action}`, { name: currentNotice.peerName }) }}</QModalLabel>
    <template #actions><QButton variant="primary" @click="closeDialog">{{ t('devices.action.acknowledge') }}</QButton></template>
  </QModal>
  <QModal :open="dialogKind === 'error'" :title="t('devices.action.errorTitle')" :close-label="t('devices.action.acknowledge')" @close="closeDialog">
    <QModalLabel>{{ currentError }}</QModalLabel>
    <template #actions><QButton variant="primary" @click="closeDialog">{{ t('devices.action.acknowledge') }}</QButton></template>
  </QModal>
</template>

<style scoped>
.devices-page { padding: clamp(24px, 3vh, 36px) clamp(24px, 3vw, 42px); }
.devices-workspace { width: 100%; height: 100%; min-height: 690px; display: grid; grid-template-rows: auto minmax(260px, .9fr) minmax(320px, 1fr); gap: 22px; }
.devices-heading { display:flex;align-items:end;justify-content:space-between;gap:14px; animation: devices-enter 420ms both; }
.devices-eyebrow { color: var(--q-brand); font-size: 11px; font-weight: 750; letter-spacing: .13em; }
.devices-heading h1 { margin: 6px 0 0; font-size: clamp(27px, 3vw, 34px); letter-spacing: -.035em; }
.devices-upper { display: grid; grid-template-columns: minmax(280px, .8fr) minmax(0, 1.6fr); gap: 22px; min-height: 0; align-items: stretch; }
.devices-local, .devices-paired, .devices-nearby { border: 1px solid var(--q-border); border-radius: var(--q-radius-lg); background: var(--q-surface); box-shadow: 0 9px 25px rgba(43,76,120,.045); }
.devices-local { display: flex; flex-direction: column; justify-content: space-between; gap: 16px; min-width: 0; min-height: 260px; padding: clamp(24px, 2.4vw, 34px); border-color: color-mix(in srgb, var(--q-brand) 20%, var(--q-border)); background: linear-gradient(135deg, var(--q-surface), color-mix(in srgb, var(--q-brand-soft) 38%, var(--q-surface))); animation: devices-enter 480ms 50ms both; }
.devices-local-main { display: flex; align-items: center; gap: 18px; min-width: 0; }
.devices-local-mark { flex: 0 0 auto; width: 72px; height: 72px; display: grid; place-items: center; border-radius: 20px; background: var(--q-brand-soft); color: var(--q-brand); }
.devices-local-content { min-width: 0; }
.devices-local h2 { margin: 0 0 6px; font-size: clamp(23px, 2.8vw, 31px); font-weight: 730; letter-spacing: -.025em; overflow-wrap: anywhere; }
.devices-platform { color: var(--q-text-2); font-size: 13px; }
.devices-local-state { align-self: flex-start; display: inline-flex; align-items: center; gap: 8px; max-width: 100%; padding: 7px 11px; border: 1px solid var(--q-border); border-radius: 999px; background: var(--q-surface); color: var(--q-text-2); font-size: 12px; font-weight: 650; }
.devices-state-dot { flex: 0 0 auto; width: 7px; height: 7px; border-radius: 50%; background: var(--q-text-3); }
.devices-state-dot.online { background: #12a86b; box-shadow: 0 0 0 3px rgba(18,168,107,.12); }
.devices-discovery-button { align-self: flex-start; border: 1px solid var(--q-border); border-radius: 9px; padding: 7px 11px; background: var(--q-surface); color: var(--q-brand); font-size: 12px; font-weight: 650; cursor: pointer; }
.devices-discovery-button:hover { border-color: var(--q-brand); background: var(--q-brand-soft); }
.devices-discovery-button:disabled { opacity: .55; cursor: wait; }
.devices-error { margin: 0; color: #d13b49; font-size: 12px; overflow-wrap: anywhere; }
.devices-paired { min-width: 0; min-height: 0; display: flex; flex-direction: column; overflow: hidden; container-type: inline-size; animation: devices-enter 500ms 95ms both; }
.devices-tabs { flex: 0 0 auto; display: flex; gap: 6px; margin: 0 20px; padding: 14px 0 10px; border-bottom: 1px solid var(--q-border); }
.devices-tabs button { border: 0; border-radius: 9px; padding: 9px 15px; background: transparent; color: var(--q-text-2); font-size: 13px; font-weight: 650; cursor: pointer; }
.devices-tabs button:hover { background: var(--q-surface-soft); color: var(--q-text); }
.devices-tabs button.active { background: var(--q-brand-soft); color: var(--q-brand); }
.devices-paired-scroll, .devices-nearby-scroll { min-height: 0; flex: 1; overflow-y: auto; overscroll-behavior: contain; scrollbar-width: thin; scrollbar-color: color-mix(in srgb, var(--q-brand) 36%, var(--q-border)) transparent; }
.devices-paired-scroll::-webkit-scrollbar, .devices-nearby-scroll::-webkit-scrollbar { width: 7px; }
.devices-paired-scroll::-webkit-scrollbar-thumb, .devices-nearby-scroll::-webkit-scrollbar-thumb { border-radius: 7px; background: color-mix(in srgb, var(--q-brand) 36%, var(--q-border)); }
.devices-nearby { display: flex; flex-direction: column; min-height: 0; overflow: hidden; animation: devices-enter 500ms 140ms both; }
.devices-nearby-heading { flex: 0 0 auto; display: flex; align-items: baseline; justify-content: space-between; gap: 16px; padding: 23px 25px 17px; border-bottom: 1px solid var(--q-border); }
.devices-nearby-heading h2 { margin: 0; font-size: 18px; }
.devices-nearby-heading span { color: var(--q-text-3); font-size: 12px; }
.devices-nearby-scroll > .devices-empty { height: 100%; }
.devices-nearby-list { list-style: none; margin: 0; padding: 10px 15px; }
.devices-paired-list { padding: 7px 13px; }
.devices-paired-item { display: grid; grid-template-columns: 38px minmax(0, 1fr) auto; align-items: center; gap: 10px; min-height: 76px; padding: 10px 7px; }
.devices-peer-name { display: flex; align-items: center; min-width: 0; gap: 5px; }
.devices-paired-item .devices-peer-name strong { font-size: 15px; }
.devices-remark-action { display: inline-grid; place-items: center; flex: 0 0 auto; width: 27px; height: 27px; padding: 0; border: 1px solid transparent; border-radius: 8px; background: transparent; color: var(--q-text-3); cursor: pointer; }
.devices-remark-action:hover { background: var(--q-brand-soft); color: var(--q-brand); transform: translateY(-1px); }
.devices-remark-action:active { transform: scale(.92); }
.devices-remark-action:disabled { opacity: .45; cursor: default; transform: none; }
.devices-remark-error { color: var(--q-danger); }
.devices-peer-actions { display: flex; align-items: center; justify-content: flex-end; flex-wrap: wrap; gap: 8px; }
.devices-peer-meta { display: flex; align-items: center; flex-wrap: wrap; gap: 5px 9px; }
.devices-presence { display: inline-flex; align-items: center; gap: 5px; white-space: nowrap; color: var(--q-text-3); }
.devices-presence i { width: 7px; height: 7px; border-radius: 50%; background: currentColor; }
.devices-presence.online { color: #12976a; }
.devices-pair-code { font-variant-numeric: tabular-nums; letter-spacing: .14em; font-size: 27px; font-weight: 750; }
.devices-modal-waiting { color: var(--q-text-2); font-size: 13px; }
.devices-pair-error { margin: 10px 16px; color: #d13b49; font-size: 12px; }
.devices-small-action { flex: 0 0 auto; border: 1px solid var(--q-border); border-radius: 8px; padding: 6px 9px; background: var(--q-surface); color: var(--q-brand); font-size: 11px; font-weight: 650; cursor: pointer; }
.devices-small-action:hover { border-color: var(--q-brand); }
.devices-transfer-action { display: inline-grid; place-items: center; width: 32px; height: 32px; padding: 0; }
.devices-small-action:disabled { opacity: .5; cursor: wait; }
.devices-revoke { color: var(--q-text-2); }
.devices-nearby-item { display: flex; align-items: center; gap: 12px; min-height: 66px; padding: 9px 12px; border-bottom: 1px solid var(--q-border); animation: devices-enter 240ms both; }
.devices-nearby-item:last-child { border-bottom: 0; }
.devices-nearby-icon { display: grid; place-items: center; flex: 0 0 auto; width: 38px; height: 38px; border-radius: 11px; background: var(--q-brand-soft); color: var(--q-brand); }
.devices-nearby-details { display: flex; flex-direction: column; min-width: 0; gap: 3px; }
.devices-nearby-details strong { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: 13px; }
.devices-nearby-details small { color: var(--q-text-2); font-size: 11px; }
.devices-unverified { margin-left: auto; flex: 0 0 auto; color: var(--q-text-3); font-size: 11px; }
.devices-empty { min-height: 250px; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 12px; padding: 24px; }
.devices-empty-compact { min-height: 133px; height: 100%; padding: 12px; gap: 9px; }
.devices-empty-icon { width: 62px; height: 62px; display: grid; place-items: center; border: 1px solid var(--q-border); border-radius: 19px; background: var(--q-surface-soft); color: var(--q-text-3); }
.devices-empty-compact .devices-empty-icon { width: 42px; height: 42px; border-radius: 13px; }
.devices-empty p { margin: 0; color: var(--q-text-2); font-size: 14px; font-weight: 600; }
.devices-empty-compact p { font-size: 13px; }
.devices-swap-enter-active, .devices-swap-leave-active { transition: opacity 130ms ease, transform 130ms ease; }
.devices-swap-enter-from { opacity: 0; transform: translateY(7px); }
.devices-swap-leave-to { opacity: 0; transform: translateY(-5px); }
@keyframes devices-enter { from { opacity: 0; transform: translateY(12px); } to { opacity: 1; transform: translateY(0); } }
@container (max-width: 590px) { .devices-paired-item { grid-template-columns: 38px minmax(0, 1fr); gap: 5px 10px; } .devices-peer-actions { grid-column: 2; } }
@media (max-width: 850px) { .devices-workspace { height: auto; min-height: 100%; grid-template-rows: auto auto minmax(340px, 1fr); } .devices-upper { grid-template-columns: 1fr; } .devices-local { min-height: 200px; } .devices-paired { height: clamp(260px, 38vh, 440px); } }
@media (max-width: 720px) { .devices-page { padding: 19px 16px; } .devices-local { padding: 21px; } .devices-local-mark { width: 64px; height: 64px; } .devices-nearby-heading { display: block; padding: 20px 18px 14px; } .devices-nearby-heading span { display: block; margin-top: 6px; } .devices-tabs { margin: 0 18px; flex-wrap: wrap; } }
@media (max-width: 420px) { .devices-local-main { gap: 13px; } .devices-local-mark { width: 54px; height: 54px; } }
@media (prefers-reduced-motion: reduce) { .devices-heading, .devices-local, .devices-paired, .devices-nearby { animation: none; } .devices-swap-enter-active, .devices-swap-leave-active, .devices-remark-action { transition: none; } .devices-remark-action:hover, .devices-remark-action:active { transform: none; } }
</style>
