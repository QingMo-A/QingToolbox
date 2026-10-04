import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { invoke } from '@tauri-apps/api/core'
import InfoPopupSurface from './InfoPopupSurface.vue'

const events = vi.hoisted(() => ({ changed: (() => {}) as () => void, unlisten: vi.fn() }))
vi.mock('@tauri-apps/api/core', () => ({ invoke: vi.fn() }))
vi.mock('@tauri-apps/api/event', () => ({ listen: vi.fn(async (_name: string, changed: () => void) => {
  events.changed = changed
  return events.unlisten
}) }))
const message = { id: 'one', deviceName: 'Phone', appName: 'Messages', title: 'Title', body: 'Hello' }
let current: typeof message | null
let wrapper: VueWrapper | null = null
const dismissCalls = () => vi.mocked(invoke).mock.calls.filter(([command]) => command === 'dismiss_info_popup_item')

describe('popup card surface', () => {
  beforeEach(() => {
    vi.useFakeTimers(); vi.mocked(invoke).mockReset(); events.unlisten.mockClear()
    current = { ...message }
    vi.mocked(invoke).mockImplementation(async command => {
      if (command === 'get_info_popup_dismiss_seconds') return 15
      if (command === 'get_info_popup_item') return current
      return undefined
    })
  })
  afterEach(() => { wrapper?.unmount(); wrapper = null; vi.useRealTimers() })

  it('pauses on hover, and same-card reflow events do not reset its timer', async () => {
    wrapper = mount(InfoPopupSurface)
    await flushPromises()
    await vi.advanceTimersByTimeAsync(5_000)
    await wrapper.get('.q-info-popup').trigger('mouseenter')
    expect(invoke).toHaveBeenCalledWith('set_info_popup_hover', { id: 'one', hovered: true })
    await vi.advanceTimersByTimeAsync(30_000)
    expect(dismissCalls()).toHaveLength(0)
    events.changed(); await flushPromises()
    await wrapper.get('.q-info-popup').trigger('mouseleave')
    expect(invoke).toHaveBeenCalledWith('set_info_popup_hover', { id: 'one', hovered: false })
    await vi.advanceTimersByTimeAsync(9_999)
    expect(dismissCalls()).toHaveLength(0)
    await vi.advanceTimersByTimeAsync(1)
    expect(dismissCalls()).toEqual([['dismiss_info_popup_item', { id: 'one' }]])
    expect(wrapper.get('button').attributes('disabled')).toBeDefined()
  })

  it('dismisses only its own card and starts a fresh timer when the base window is reused', async () => {
    wrapper = mount(InfoPopupSurface)
    await flushPromises()
    await wrapper.get('button').trigger('click'); await flushPromises()
    expect(dismissCalls()).toEqual([['dismiss_info_popup_item', { id: 'one' }]])
    current = null; events.changed(); await flushPromises()
    expect(wrapper.find('.q-info-popup').exists()).toBe(false)
    current = { ...message, id: 'two' }; events.changed(); await flushPromises()
    expect(wrapper.get('button').attributes('disabled')).toBeUndefined()
    await vi.advanceTimersByTimeAsync(15_000)
    expect(dismissCalls().at(-1)).toEqual(['dismiss_info_popup_item', { id: 'two' }])
  })

  it('cleans up listeners and timers when a transient window closes', async () => {
    wrapper = mount(InfoPopupSurface)
    await flushPromises()
    wrapper.unmount(); wrapper = null
    await vi.advanceTimersByTimeAsync(30_000)
    expect(dismissCalls()).toHaveLength(0)
    expect(events.unlisten).toHaveBeenCalledOnce()
  })
})
