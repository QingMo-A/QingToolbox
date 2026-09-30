import { defineStore } from 'pinia'
import type { HostUpdateSnapshot } from '../contracts/hostUpdate'

export const useHostUpdateStore = defineStore('hostUpdate', {
  state: () => ({
    snapshot: null as HostUpdateSnapshot | null,
    busy: false,
    error: '',
    startupCheckAttempted: false,
    revision: 0,
  }),
  actions: {
    complete(snapshot: HostUpdateSnapshot) {
      this.snapshot = snapshot
      this.error = ''
      this.revision++
    },
    fail(error: unknown) {
      this.error = error instanceof Error ? error.message : String(error)
      this.revision++
    },
  },
})
