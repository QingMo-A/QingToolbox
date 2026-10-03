<script setup lang="ts">
import { onBeforeUnmount, onMounted, watch } from 'vue'
import { listen, type UnlistenFn } from '@tauri-apps/api/event'
import { useModuleRepositoryStore } from '../../app/moduleRepositoryStore'
import { repositoryDownloadActive } from '../../contracts/moduleRepository'
import { moduleRepositoryClient } from '../../bridge/clients/ModuleRepositoryClient'
import { useToastStore } from '../../app/toastStore'
import { useLocalization } from '../../localization/localization'
const store = useModuleRepositoryStore()
const toast = useToastStore()
const { t } = useLocalization()
let timer: ReturnType<typeof setTimeout> | undefined
let disposed = false
let polling = false
let pollQueued = false
let reported = 0
let unlisten: UnlistenFn | null = null
async function poll() {
  if (disposed) return
  if (polling) { pollQueued = true; return }
  polling = true
  try {
    const snapshot = await moduleRepositoryClient.snapshot()
    if (!disposed) {
      store.complete(snapshot)
      if (store.snapshot?.jobId === snapshot.jobId && store.snapshot.status === snapshot.status) {
        if (snapshot.status === 'Failed' && reported !== snapshot.jobId) { reported = snapshot.jobId; toast.show(t('modules.repository.failed'), 'error') }
        if (snapshot.status === 'InstallFailed' && reported !== snapshot.jobId) { reported = snapshot.jobId; toast.show(t('modules.repository.installFailed'), 'error') }
      }
    }
  } catch { /* A transient poll failure must not make an active download disappear. */ }
  finally {
    polling = false
    if (!disposed && (pollQueued || store.active)) {
      const delay = pollQueued ? 0 : 500
      pollQueued = false; clearTimeout(timer); timer = setTimeout(poll, delay)
    }
  }
}
watch(() => store.active, active => { if (active) void poll(); else clearTimeout(timer) })
onMounted(() => {
  void poll()
  if ((window as Window & { __TAURI_INTERNALS__?: unknown }).__TAURI_INTERNALS__) void listen('qmod:module-repository-changed', () => { void poll() }).then(cleanup => { if (disposed) cleanup(); else unlisten = cleanup }).catch(() => {})
})
onBeforeUnmount(() => { disposed = true; clearTimeout(timer); unlisten?.() })
</script>
<template>
  <Transition name="module-download-label"><span v-if="repositoryDownloadActive(store.snapshot) && store.snapshot" class="q-module-download-status" role="status" aria-live="polite" :title="store.snapshot.name">{{ store.snapshot.status === 'Installing' ? t('modules.repository.installing', { name: store.snapshot.name }) : t('modules.repository.progress', { name: store.snapshot.name, percent: store.percentage }) }}</span></Transition>
</template>
<style scoped>
.q-module-download-status { max-width: min(330px,35vw); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; color: var(--q-brand); font-size: 12px; }
.module-download-label-enter-active,.module-download-label-leave-active { transition: opacity 180ms ease,transform 180ms ease; }
.module-download-label-enter-from,.module-download-label-leave-to { opacity:0; transform:translateY(-3px); }
@media(prefers-reduced-motion:reduce) { .module-download-label-enter-active,.module-download-label-leave-active { transition:none; } }
</style>
