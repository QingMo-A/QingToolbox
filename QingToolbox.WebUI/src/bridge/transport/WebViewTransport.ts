import type { BridgeEvent, BridgeRequest, BridgeResponse } from '../../contracts/app'
import type { Transport } from './Transport'

interface WebViewApi {
  postMessage(message: unknown): void
  addEventListener(type: 'message', listener: (event: MessageEvent) => void): void
  removeEventListener(type: 'message', listener: (event: MessageEvent) => void): void
}
declare global { interface Window { chrome?: { webview?: WebViewApi } } }

export class WebViewTransport implements Transport {
  readonly mode = 'WebView' as const
  private readonly pending = new Map<string, { resolve: (value: BridgeResponse) => void; reject: (reason: Error) => void; timer: number }>()
  private readonly listeners = new Set<(event: BridgeEvent) => void>()
  private readonly api: WebViewApi
  private readonly onMessage = (event: MessageEvent) => {
    const message = event.data as BridgeResponse | BridgeEvent
    if ('requestId' in message) {
      const pending = this.pending.get(message.requestId)
      if (!pending) return
      clearTimeout(pending.timer); this.pending.delete(message.requestId); pending.resolve(message)
    } else if ('event' in message) this.listeners.forEach(listener => listener(message))
  }

  static isAvailable() { return Boolean(window.chrome?.webview) }

  constructor() {
    const api = window.chrome?.webview
    if (!api) throw new Error('WebView transport is unavailable.')
    this.api = api; api.addEventListener('message', this.onMessage)
  }

  request(message: BridgeRequest, timeoutMs = 5000): Promise<BridgeResponse> {
    return new Promise((resolve, reject) => {
      const timer = window.setTimeout(() => { this.pending.delete(message.requestId); reject(new Error('Bridge request timed out.')) }, timeoutMs)
      this.pending.set(message.requestId, { resolve, reject, timer }); this.api.postMessage(message)
    })
  }
  subscribe(listener: (event: BridgeEvent) => void) { this.listeners.add(listener); return () => this.listeners.delete(listener) }
}
