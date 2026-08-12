import type { Presentation, State } from './types'

type Message = { type: string; id?: string; method?: string; payload?: unknown; ok?: boolean; error?: string; name?: string }
const webView = (globalThis as { chrome?: { webview?: { postMessage: (message: unknown) => void; addEventListener: (name: string, handler: (event: MessageEvent<Message>) => void) => void } } }).chrome?.webview
const pending = new Map<string, { resolve: (value: unknown) => void; reject: (error: Error) => void }>()
let sequence = 0
let stateListener: ((state: State) => void) | undefined
let presentationListener: ((presentation: Presentation) => void) | undefined
let latestPresentation: Presentation | undefined
let presentationReady: Promise<Presentation> | undefined
let resolvePresentationReady: ((presentation: Presentation) => void) | undefined

function normalizePresentation(value: Partial<Presentation> | undefined): Presentation {
  const appearancePresetId = typeof value?.appearancePresetId === 'string' && value.appearancePresetId.trim()
    ? value.appearancePresetId
    : 'qing-default'
  const languageCode = value?.languageCode === 'zh-CN' ? 'zh-CN' : 'en-US'
  return { appearancePresetId, languageCode }
}

function applyDocumentPresentation(presentation: Presentation) {
  if (typeof document === 'undefined') return
  document.documentElement.dataset.appearancePreset = presentation.appearancePresetId
  document.documentElement.lang = presentation.languageCode
}

function publishPresentation(value: Partial<Presentation> | undefined) {
  latestPresentation = normalizePresentation(value)
  applyDocumentPresentation(latestPresentation)
  resolvePresentationReady?.(latestPresentation)
  resolvePresentationReady = undefined
  presentationListener?.(latestPresentation)
}

if (webView) webView.addEventListener('message', (event) => {
  const message = event.data
  if (message.type === 'result' && message.id) {
    const request = pending.get(message.id)
    if (!request) return
    pending.delete(message.id)
    message.ok === false ? request.reject(new Error(message.error || 'The request could not be completed.')) : request.resolve(message.payload)
  } else if (message.type === 'event' && message.name === 'stateChanged' && message.payload) stateListener?.(message.payload as State)
  else if (message.type === 'hostReady' || message.type === 'presentationChanged') publishPresentation(message as unknown as Presentation)
}
)

export function invoke<T>(method: string, payload?: unknown): Promise<T> {
  if (!webView) return Promise.reject(new Error('QingTransfer bridge is unavailable.'))
  const id = `request-${++sequence}`
  return new Promise<T>((resolve, reject) => { pending.set(id, { resolve: resolve as (value: unknown) => void, reject }); webView.postMessage({ type: 'invoke', id, method, payload }) })
}
export function onStateChanged(listener: (state: State) => void) { stateListener = listener; return () => { if (stateListener === listener) stateListener = undefined } }
export function onPresentationChanged(listener: (presentation: Presentation) => void) {
  presentationListener = listener
  if (latestPresentation) {
    applyDocumentPresentation(latestPresentation)
    listener(latestPresentation)
  }
  return () => { if (presentationListener === listener) presentationListener = undefined }
}
export function waitForPresentation(): Promise<Presentation> {
  if (latestPresentation) return Promise.resolve(latestPresentation)
  presentationReady ??= new Promise(resolve => { resolvePresentationReady = resolve })
  return presentationReady
}
