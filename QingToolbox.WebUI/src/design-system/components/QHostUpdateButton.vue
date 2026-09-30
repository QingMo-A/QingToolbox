<script setup lang="ts">
import { computed, inject, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import type { HostUpdateClient } from '../../bridge/clients/HostUpdateClient'
import type { HostUpdateSnapshot } from '../../contracts/hostUpdate'
import { useAppStore } from '../../app/store'
import { useHostUpdateStore } from '../../app/hostUpdateStore'
import { useToastStore } from '../../app/toastStore'
import { useLocalization } from '../../localization/localization'
import QIcon from './QIcon.vue'

const client = inject<HostUpdateClient | null>('hostUpdateClient', null)
const app = useAppStore()
const store = useHostUpdateStore()
const toast = useToastStore()
const { t } = useLocalization()
const autoInstallVersion = ref('')
let mounted = false
let timer: number | undefined
let commandPending = false

const visible = computed(() => app.snapshot?.environmentKind === 'Production'
  && Boolean(store.snapshot?.latestVersion)
  && ['UpdateAvailable', 'Installing'].includes(store.snapshot?.state ?? ''))
const progress = computed(() => {
  const snapshot = store.snapshot
  return snapshot && snapshot.expectedBytes > 0
    ? Math.min(100, Math.round(snapshot.bytesReceived / snapshot.expectedBytes * 100)) : 0
})
const downloading = computed(() => store.snapshot?.downloadState === 'Downloading')
const processing = computed(() => ['Verifying', 'Installing'].includes(store.snapshot?.downloadState ?? ''))
const disabled = computed(() => store.busy || Boolean(autoInstallVersion.value)
  || downloading.value || processing.value
  || (!store.snapshot?.canDownload && !store.snapshot?.canInstall))
const label = computed(() => {
  if (downloading.value) return t('hostUpdate.banner.progress', { progress: progress.value })
  if (store.snapshot?.downloadState === 'Verifying') return t('hostUpdate.button.verifying')
  if (store.snapshot?.downloadState === 'Installing') return t('hostUpdate.button.installing')
  if (store.error || store.snapshot?.downloadState === 'Failed') return t('hostUpdate.button.retry')
  return t('hostUpdate.button.update', { version: store.snapshot?.latestVersion ?? '' })
})

function needsPolling() {
  return store.snapshot?.state === 'Checking'
    || ['Downloading', 'Verifying'].includes(store.snapshot?.downloadState ?? '')
    || commandPending || Boolean(autoInstallVersion.value)
}
function stopPolling() {
  if (timer !== undefined) window.clearTimeout(timer)
  timer = undefined
}
function syncPolling() {
  // A click authorizes the entire download/install operation, including when
  // the main window is subsequently hidden. Idle hosts do not keep polling.
  if (!mounted || !client || !needsPolling()
    || (document.visibilityState === 'hidden' && !autoInstallVersion.value)) {
    stopPolling()
    return
  }
  if (timer !== undefined) return
  timer = window.setTimeout(async () => {
    timer = undefined
    await refreshProgress()
    await advanceInstallation()
    syncPolling()
  }, 1000)
}
async function refreshProgress() {
  if (!client || !mounted) return
  const revision = store.revision
  try {
    const snapshot = await client.getSnapshot()
    // An earlier progress read cannot replace a newer command response.
    if (mounted && revision === store.revision) store.complete(snapshot)
  } catch (error) {
    if (mounted && revision === store.revision) store.fail(error)
  }
}
async function run(action: () => Promise<HostUpdateSnapshot>) {
  if (store.busy || !mounted) return null
  store.busy = true
  commandPending = true
  syncPolling()
  try {
    const snapshot = await action()
    if (mounted) store.complete(snapshot)
    return snapshot
  } catch (error) {
    if (mounted) store.fail(error)
    return null
  } finally {
    store.busy = false
    commandPending = false
    syncPolling()
  }
}
async function initialize() {
  if (!mounted || !client || app.bridge !== 'Connected'
    || app.snapshot?.environmentKind !== 'Production' || store.startupCheckAttempted) return
  store.startupCheckAttempted = true
  const snapshot = await run(() => client.getSnapshot())
  if (mounted && snapshot?.state === 'NotChecked' && snapshot.canCheck) {
    await run(() => client.check())
  }
}
async function install() {
  if (!client) return
  const result = await run(() => client.install())
  if (mounted && result?.downloadState !== 'Installing') {
    toast.show(t('hostUpdate.banner.failed'), 'error')
  }
}
async function advanceInstallation() {
  if (!mounted || !autoInstallVersion.value || store.busy) return
  const snapshot = store.snapshot
  if (!snapshot || snapshot.latestVersion !== autoInstallVersion.value
    || ['Failed', 'Cancelled'].includes(snapshot.downloadState)) {
    autoInstallVersion.value = ''
    if (snapshot?.downloadState === 'Failed') toast.show(t('hostUpdate.banner.failed'), 'error')
    return
  }
  if (snapshot.downloadState !== 'ReadyToInstall') return
  autoInstallVersion.value = ''
  if (snapshot.canInstall && snapshot.installationSupported) await install()
  else toast.show(t('hostUpdate.button.unsupported'), 'error')
}
async function update() {
  if (!client || disabled.value) return
  if (store.snapshot?.canInstall) {
    await install()
    return
  }
  autoInstallVersion.value = store.snapshot!.latestVersion
  const result = await run(() => client.download())
  if (!result) {
    autoInstallVersion.value = ''
    if (mounted) toast.show(t('hostUpdate.banner.failed'), 'error')
  } else await advanceInstallation()
  syncPolling()
}
function onVisibilityChange() {
  syncPolling()
}
watch(() => [app.bridge, app.snapshot?.environmentKind], () => { void initialize() })
watch(() => store.snapshot, () => {
  void advanceInstallation().finally(syncPolling)
})
onMounted(() => {
  mounted = true
  document.addEventListener('visibilitychange', onVisibilityChange)
  void initialize()
  syncPolling()
})
onBeforeUnmount(() => {
  mounted = false
  autoInstallVersion.value = ''
  stopPolling()
  document.removeEventListener('visibilitychange', onVisibilityChange)
})
</script>

<template>
  <Transition name="q-update">
    <button v-if="visible" type="button" class="q-host-update-button"
      :class="{ 'is-downloading': downloading, 'is-processing': processing }"
      :disabled="disabled" :aria-label="label" :title="label" :aria-busy="downloading || processing"
      @mousedown.stop @dblclick.stop @click.stop="update">
      <svg v-if="downloading" class="q-update-progress" viewBox="0 0 32 32" aria-hidden="true">
        <circle class="q-update-track" cx="16" cy="16" r="14" />
        <circle cx="16" cy="16" r="14" pathLength="100" :stroke-dasharray="`${progress} 100`" />
      </svg>
      <QIcon :name="processing ? 'refresh' : 'download'" :size="16" />
      <span class="q-update-status" role="status">{{ label }}</span>
    </button>
  </Transition>
</template>

<style scoped>
.q-host-update-button { position: relative; display: grid; flex: 0 0 28px; place-items: center; width: 28px; height: 28px; padding: 0; border: 0; border-radius: 8px; background: transparent; color: var(--q-brand); cursor: pointer; transition: background 160ms ease, transform 180ms ease; }
.q-host-update-button:hover:not(:disabled) { background: var(--q-brand-soft); transform: translateY(-1px); }
.q-host-update-button:active:not(:disabled) { transform: scale(.92); }
.q-host-update-button:disabled { cursor: default; }
.q-host-update-button.is-processing :deep(.q-icon) { animation: q-update-spin 1.3s linear infinite; }
.q-update-progress { position: absolute; inset: -2px; width: 32px; height: 32px; fill: none; stroke: currentColor; stroke-width: 1.7; transform: rotate(-90deg); pointer-events: none; }
.q-update-progress circle { transition: stroke-dasharray 240ms ease; stroke-linecap: round; }
.q-update-progress .q-update-track { opacity: .15; }
.q-update-status { position: absolute; width: 1px; height: 1px; overflow: hidden; clip-path: inset(50%); white-space: nowrap; }
.q-update-enter-active { transition: opacity 220ms ease, transform 300ms cubic-bezier(.2,.8,.2,1); }
.q-update-leave-active { transition: opacity 150ms ease, transform 150ms ease; }
.q-update-enter-from { opacity: 0; transform: translateY(-4px) scale(.85); }
.q-update-leave-to { opacity: 0; transform: scale(.85); }
@keyframes q-update-spin { to { transform: rotate(360deg); } }
@media (prefers-reduced-motion: reduce) {
  .q-host-update-button, .q-update-progress circle, .q-update-enter-active, .q-update-leave-active { transition: none; }
  .q-host-update-button.is-processing :deep(.q-icon) { animation: none; }
}
</style>
