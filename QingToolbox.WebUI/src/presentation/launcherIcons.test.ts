import { beforeEach, describe, expect, it, vi } from 'vitest'

const invoke = vi.hoisted(() => vi.fn())
vi.mock('../../../QingToolbox.Tauri/native-launcher/ui-src/src/bridge', () => ({ invokeModule: invoke }))

describe('Launcher real icon loading', () => {
  beforeEach(() => { vi.resetModules(); invoke.mockReset() })
  it('shares a real PNG across grid, recent and drag ghost', async () => {
    const png = 'data:image/png;base64,test'
    invoke.mockResolvedValue({ dataUrl: png })
    const { loadItemIcon, cachedItemIcon } = await import('../../../QingToolbox.Tauri/native-launcher/ui-src/src/itemIcons')
    const first = loadItemIcon('launcher-icon:app')
    const second = loadItemIcon('launcher-icon:app')
    expect(first).toBe(second)
    expect(await first).toBe(png)
    expect(cachedItemIcon('launcher-icon:app')).toBe(png)
    expect(invoke).toHaveBeenCalledExactlyOnceWith('getItemIcon', { id: 'app' })
  })
  it('limits parallel reads and does not turn a missing icon into an error', async () => {
    const completions: Array<(value: { dataUrl: null }) => void> = []
    invoke.mockImplementation(() => new Promise(resolve => completions.push(resolve)))
    const { loadItemIcon } = await import('../../../QingToolbox.Tauri/native-launcher/ui-src/src/itemIcons')
    const results = ['a', 'b', 'c'].map(id => loadItemIcon(`launcher-icon:${id}`))
    expect(invoke).toHaveBeenCalledTimes(2)
    completions[0]({ dataUrl: null }); completions[1]({ dataUrl: null })
    await vi.waitFor(() => expect(invoke).toHaveBeenCalledTimes(3))
    completions[2]({ dataUrl: null })
    expect(await Promise.all(results)).toEqual([null, null, null])
  })
  it('retries a failed IPC read instead of caching failure permanently', async () => {
    invoke.mockRejectedValueOnce(new Error('unavailable')).mockResolvedValue({ dataUrl: 'data:image/png;base64,retry' })
    const { loadItemIcon } = await import('../../../QingToolbox.Tauri/native-launcher/ui-src/src/itemIcons')
    expect(await loadItemIcon('launcher-icon:retry')).toBeNull()
    expect(await loadItemIcon('launcher-icon:retry')).toBe('data:image/png;base64,retry')
  })
})
