import { defineStore } from 'pinia'
import type { AppSnapshot } from '../contracts/app'
export const useAppStore = defineStore('app', {
  state: () => ({ snapshot: null as AppSnapshot | null, bridge: 'Connecting', mode: 'Unknown', lastEvent: 'None', pingMs: null as number | null, error: '' }),
  actions: { rebuild(snapshot: AppSnapshot) { this.snapshot = snapshot; this.bridge = 'Connected'; this.error = '' } }
})
