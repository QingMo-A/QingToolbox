import { ref } from 'vue'
import { invoke } from '@tauri-apps/api/core'
import { listen, type UnlistenFn } from '@tauri-apps/api/event'

export type TransferDevice = { id: string; name: string }
export type TransferRequest = { device: TransferDevice; incoming: boolean }

// Events wake the receiver quickly; snapshots recover requests missed while
// the WebView was starting, hidden, suspended, or registering its listener.
export function createDeviceTransferRequests() {
  const request = ref<TransferRequest | null>(null)
  let stopped = false
  let checking: Promise<TransferDevice | null> | null = null
  let timer: ReturnType<typeof setInterval> | null = null
  let unlisten: UnlistenFn | null = null

  function showIncoming(device: TransferDevice) {
    if (stopped || !device?.id || !device.name) return
    if (request.value?.incoming && request.value.device.id === device.id) return
    request.value = { device, incoming: true }
  }

  function sync(): Promise<TransferDevice | null> {
    if (stopped) return Promise.resolve(null)
    if (checking) return checking
    checking = (async () => {
      try {
        const state = await invoke<{ incomingRequest?: TransferDevice | null }>('get_device_transfer_state')
        const device = state?.incomingRequest
        if (!stopped && device?.id && device.name) {
          showIncoming(device)
          return device
        }
      } catch { /* A transient IPC failure must not stop future receive checks. */ }
      return null
    })().finally(() => { checking = null })
    return checking
  }

  async function openOutgoing(device: TransferDevice) {
    if (stopped || !device?.id || !device.name) return
    if (await sync() || stopped || request.value?.incoming) return
    request.value = { device, incoming: false }
  }

  function start() {
    if (stopped || timer) return
    // Start recovery immediately, even if native listener registration fails.
    timer = setInterval(() => { void sync() }, 500)
    window.addEventListener('focus', onFocus)
    document.addEventListener('visibilitychange', onVisible)
    void sync()
    void listen('qing:incoming-device-file', () => { void sync() }, { target: 'main' })
      .then(cleanup => { if (stopped) cleanup(); else unlisten = cleanup })
      .catch(() => { /* Snapshot recovery is still active. */ })
  }

  function onFocus() { void sync() }
  function onVisible() { if (!document.hidden) void sync() }
  function stop() {
    stopped = true
    if (timer) clearInterval(timer)
    timer = null
    unlisten?.()
    window.removeEventListener('focus', onFocus)
    document.removeEventListener('visibilitychange', onVisible)
  }

  return { request, showIncoming, openOutgoing, start, stop }
}
