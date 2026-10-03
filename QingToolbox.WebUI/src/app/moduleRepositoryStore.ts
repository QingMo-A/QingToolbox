import { defineStore } from 'pinia'
import { ref, computed } from 'vue'
import type { ModuleRepositoryDownload } from '../contracts/moduleRepository'
import { repositoryDownloadActive } from '../contracts/moduleRepository'
import { moduleRepositoryClient } from '../bridge/clients/ModuleRepositoryClient'

// Shared with the title bar, so leaving the Modules page cannot stop progress.
export const useModuleRepositoryStore = defineStore('moduleRepository', () => {
  const snapshot = ref<ModuleRepositoryDownload | null>(null)
  const starting = ref(false)
  const active = computed(() => starting.value || repositoryDownloadActive(snapshot.value))
  const percentage = computed(() => snapshot.value?.expectedBytes
    ? Math.min(snapshot.value.status === 'Completed' ? 100 : 99, Math.floor(snapshot.value.bytesReceived * 100 / snapshot.value.expectedBytes)) : 0)
  function complete(value: ModuleRepositoryDownload) {
    // An older poll finishing after a second download started must not win.
    if (snapshot.value && value.jobId < snapshot.value.jobId) return
    if (snapshot.value?.jobId === value.jobId) {
      if (['Completed', 'Failed', 'InstallFailed'].includes(snapshot.value.status)) return
      const phase = { '': 0, Downloading: 1, Verifying: 2, Installing: 3, Completed: 4, Failed: 4, InstallFailed: 4 }
      if (phase[value.status] < phase[snapshot.value.status]) return
    }
    snapshot.value = value
  }
  async function download(id: string) {
    if (active.value) return false
    starting.value = true
    try { complete(await moduleRepositoryClient.download(id)); return true }
    finally { starting.value = false }
  }
  return { snapshot, starting, active, percentage, complete, download }
})
