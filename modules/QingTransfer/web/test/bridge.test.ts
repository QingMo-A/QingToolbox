import { beforeEach, describe, expect, it, vi } from 'vitest'

type Handler = (event: MessageEvent) => void

function setupWebView() {
  let handler: Handler | undefined
  const messages: unknown[] = []
  const webView = {
    postMessage: (message: unknown) => messages.push(message),
    addEventListener: (_name: string, next: Handler) => { handler = next },
  }
  vi.stubGlobal('chrome', { webview: webView })
  vi.stubGlobal('document', { documentElement: { dataset: {} as Record<string, string>, lang: '' } })
  return { messages, emit: (message: unknown) => handler?.({ data: message } as MessageEvent) }
}

describe('QingTransfer web bridge', () => {
  beforeEach(() => { vi.resetModules(); vi.unstubAllGlobals() })

  it('replays hostReady presentation to a late listener and updates document', async () => {
    const web = setupWebView()
    const bridge = await import('../src/bridge')
    web.emit({ type: 'hostReady', appearancePresetId: 'qing-nova', languageCode: 'zh-CN' })
    const listener = vi.fn()
    bridge.onPresentationChanged(listener)
    expect(listener).toHaveBeenCalledWith({ appearancePresetId: 'qing-nova', languageCode: 'zh-CN' })
    expect(document.documentElement.dataset.appearancePreset).toBe('qing-nova')
    expect(document.documentElement.lang).toBe('zh-CN')
  })

  it('replays hostReady through waitForPresentation and handles changes', async () => {
    const web = setupWebView()
    const bridge = await import('../src/bridge')
    const ready = bridge.waitForPresentation()
    web.emit({ type: 'hostReady', appearancePresetId: 'greenline', languageCode: 'en-US' })
    await expect(ready).resolves.toEqual({ appearancePresetId: 'greenline', languageCode: 'en-US' })
    const listener = vi.fn()
    bridge.onPresentationChanged(listener)
    web.emit({ type: 'presentationChanged', appearancePresetId: 'aurora-flow', languageCode: 'zh-CN' })
    expect(listener).toHaveBeenLastCalledWith({ appearancePresetId: 'aurora-flow', languageCode: 'zh-CN' })
  })

  it('posts invoke and resolves/rejects results while dispatching state', async () => {
    const web = setupWebView()
    const bridge = await import('../src/bridge')
    const stateListener = vi.fn()
    bridge.onStateChanged(stateListener)
    const pending = bridge.invoke<{ ok: boolean }>('getState', { value: 1 })
    expect(web.messages).toEqual([{ type: 'invoke', id: 'request-1', method: 'getState', payload: { value: 1 } }])
    web.emit({ type: 'event', name: 'stateChanged', payload: { discovery: { running: true, peers: [] } } })
    expect(stateListener).toHaveBeenCalled()
    web.emit({ type: 'result', id: 'request-1', ok: true, payload: { ok: true } })
    await expect(pending).resolves.toEqual({ ok: true })
    const failed = bridge.invoke('refresh')
    web.emit({ type: 'result', id: 'request-2', ok: false, error: 'failed' })
    await expect(failed).rejects.toThrow('failed')
    web.emit({ type: 'result', id: 'request-2', ok: false, error: 'ignored duplicate' })
  })
})
