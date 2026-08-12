import type { Presentation, State } from './types'

type Message = { type: string; id?: string; method?: string; payload?: unknown; ok?: boolean; error?: string; name?: string }
const webView = (globalThis as { chrome?: { webview?: { postMessage: (message: unknown) => void; addEventListener: (name: string, handler: (event: MessageEvent<Message>) => void) => void } } }).chrome?.webview
const pending = new Map<string, { resolve: (value: unknown) => void; reject: (error: Error) => void }>()
let sequence = 0
let stateListener: ((state: State) => void) | undefined
let presentationListener: ((presentation: Presentation) => void) | undefined

if (webView) webView.addEventListener('message', (event) => {
  const message = event.data
  if (message.type === 'result' && message.id) {
    const request = pending.get(message.id)
    if (!request) return
    pending.delete(message.id)
    message.ok === false ? request.reject(new Error(message.error || 'The request could not be completed.')) : request.resolve(message.payload)
  } else if (message.type === 'event' && message.name === 'stateChanged' && message.payload) stateListener?.(message.payload as State)
  else if (message.type === 'presentationChanged') presentationListener?.(message as unknown as Presentation)
}
)

export function invoke<T>(method: string, payload?: unknown): Promise<T> {
  if (!webView) return Promise.reject(new Error('QingTransfer bridge is unavailable.'))
  const id = `request-${++sequence}`
  return new Promise<T>((resolve, reject) => { pending.set(id, { resolve: resolve as (value: unknown) => void, reject }); webView.postMessage({ type: 'invoke', id, method, payload }) })
}
export function onStateChanged(listener: (state: State) => void) { stateListener = listener; return () => { if (stateListener === listener) stateListener = undefined } }
export function onPresentationChanged(listener: (presentation: Presentation) => void) { presentationListener = listener; return () => { if (presentationListener === listener) presentationListener = undefined } }
