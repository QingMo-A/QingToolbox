/** @vitest-environment jsdom */
import { mount } from '@vue/test-utils'
import { nextTick } from 'vue'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { EverythingSearchResponse, State } from './types'

const bridge = vi.hoisted(() => {
  const pending = new Map<number, (value: EverythingSearchResponse) => void>()
  const state: State = {
    sortMode: 'custom',
    items: [{ id: 'launcher-minecraft', name: 'Minecraft Launcher', iconKey: null, lastLaunchedAt: null }],
    recent: [{ id: 'launcher-minecraft', name: 'Minecraft Launcher', iconKey: null, lastLaunchedAt: '2026-01-01T00:00:00Z' }],
    hotkey: { ctrl: true, alt: true, shift: false, win: false, virtualKey: 32, keyLabel: 'Space' },
    hotkeyStatus: 'Registered', active: true,
  }
  const invoke = vi.fn((method: string, payload?: { requestId?: number }) => {
    if (method === 'getState') return Promise.resolve(structuredClone(state))
    if (method === 'searchEverything') return new Promise(resolve => pending.set(payload!.requestId!, resolve))
    return Promise.resolve(null)
  })
  return {
    pending, invoke,
    onStateChanged: (callback: (value: State) => void) => { callback(structuredClone(state)); return () => {} }, onDropResult: () => () => {}, onPresentationChanged: () => () => {},
    waitForPresentation: async () => ({ appearancePresetId: 'qing-default', languageCode: 'en-US' }),
  }
})

vi.mock('./bridge', () => bridge)
import App from './App.vue'

describe('Launcher Everything search isolation', () => {
  afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals(); document.body.innerHTML = ''; bridge.pending.clear(); bridge.invoke.mockClear() })

  it('renders only Everything results and ignores stale replies', async () => {
    vi.useFakeTimers()
    vi.stubGlobal('fetch', vi.fn(async () => ({ json: async () => ({}) })))
    const wrapper = mount(App, { attachTo: document.body })
    await nextTick(); await Promise.resolve(); await Promise.resolve(); await nextTick()
    expect(wrapper.text()).toContain('Minecraft Launcher')

    const input = wrapper.find('input.search-input')
    await input.setValue('/e:f old*.exe')
    await vi.advanceTimersByTimeAsync(101)
    const firstCall = bridge.invoke.mock.calls.find(([method]) => method === 'searchEverything')
    expect(firstCall).toBeDefined()
    const firstRequest = firstCall![1]!.requestId!
    expect(wrapper.find('[data-launcher-item-id]').exists()).toBe(false)
    expect(wrapper.find('.recent-section').exists()).toBe(false)
    expect(wrapper.text()).toContain('Everything · File')

    await input.setValue('/e:f new*.exe')
    await vi.advanceTimersByTimeAsync(101)
    const searches = bridge.invoke.mock.calls.filter(([method]) => method === 'searchEverything')
    expect(searches).toHaveLength(2)
    const secondRequest = searches[1]![1]!.requestId!
    bridge.pending.get(secondRequest)?.({ requestId: secondRequest, status: 'Ready', error: null, stale: false,
      results: [{ id: 'new-id', name: 'new.exe', parentPath: 'C:\\New', type: 'file' }] })
    await nextTick(); await Promise.resolve(); await nextTick()
    bridge.pending.get(firstRequest)?.({ requestId: firstRequest, status: 'Ready', error: null, stale: false,
      results: [{ id: 'old-id', name: 'old.exe', parentPath: 'C:\\Old', type: 'file' }] })
    await nextTick(); await Promise.resolve(); await nextTick()
    expect(wrapper.text()).toContain('new.exe')
    expect(wrapper.text()).not.toContain('old.exe')

    await input.setValue('minecraft')
    await nextTick()
    expect(wrapper.find('[data-launcher-item-id="launcher-minecraft"]').exists()).toBe(true)
    expect(wrapper.find('.everything-area').exists()).toBe(false)
    wrapper.unmount()
  })
})
