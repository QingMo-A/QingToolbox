/** @vitest-environment jsdom */
import { mount } from '@vue/test-utils'
import { nextTick } from 'vue'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { Item, State } from './types'

const bridge = vi.hoisted(() => {
  const items: Item[] = [
    { id: 'a', name: 'A', iconKey: null, lastLaunchedAt: null },
    { id: 'b', name: 'B', iconKey: null, lastLaunchedAt: null },
    { id: 'c', name: 'C', iconKey: null, lastLaunchedAt: null },
    { id: 'd', name: 'D', iconKey: null, lastLaunchedAt: null },
    { id: 'e', name: 'E', iconKey: null, lastLaunchedAt: null },
  ]
  const state: State = {
    sortMode: 'custom', items, recent: [],
    hotkey: { ctrl: true, alt: true, shift: false, win: false, virtualKey: 32, keyLabel: 'Space' },
    hotkeyStatus: 'Inactive', active: false,
  }
  const invoke = vi.fn(async (method: string, payload?: { ids?: string[] }) => {
    if (method === 'setCustomOrder' && payload?.ids) state.items = payload.ids.map(id => state.items.find(item => item.id === id)!).filter(Boolean)
    return structuredClone(state)
  })
  return {
    state,
    invoke,
    onStateChanged: () => () => {},
    onDropResult: () => () => {},
    onPresentationChanged: () => () => {},
    waitForPresentation: async () => ({ appearancePresetId: 'qing-default', languageCode: 'en-US' }),
  }
})

vi.mock('./bridge', () => bridge)
import App from './App.vue'

function pointerEvent(type: string, values: { pointerId: number; clientX: number; clientY: number; button?: number; timeStamp?: number }) {
  const event = new Event(type, { bubbles: true, cancelable: true }) as PointerEvent
  Object.defineProperties(event, {
    pointerId: { value: values.pointerId },
    clientX: { value: values.clientX },
    clientY: { value: values.clientY },
    button: { value: values.button ?? 0 },
    ...(values.timeStamp === undefined ? {} : { timeStamp: { value: values.timeStamp } }),
  })
  return event
}

describe('Launcher pointer drag projection', () => {
  afterEach(() => { document.body.innerHTML = '' })

  it('removes C immediately, shows only gap candidates, and persists end insertion', async () => {
    const originalRect = HTMLElement.prototype.getBoundingClientRect
    const originalStyle = window.getComputedStyle
    Object.defineProperty(HTMLElement.prototype, 'setPointerCapture', { configurable: true, value: vi.fn() })
    Object.defineProperty(HTMLElement.prototype, 'hasPointerCapture', { configurable: true, value: vi.fn(() => false) })
    Object.defineProperty(HTMLElement.prototype, 'releasePointerCapture', { configurable: true, value: vi.fn() })
    vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(function (this: HTMLElement) {
      if (this.classList.contains('launcher-grid')) return { left: 0, top: 0, width: 340, height: 240, right: 340, bottom: 240, x: 0, y: 0, toJSON: () => ({}) } as DOMRect
      const id = this.dataset.launcherItemId
      const index = id ? bridge.state.items.findIndex(item => item.id === id) : -1
      const left = Math.max(0, index % 3) * 110
      const top = Math.max(0, Math.floor(Math.max(0, index) / 3)) * 110
      return { left, top, width: 100, height: 100, right: left + 100, bottom: top + 100, x: left, y: top, toJSON: () => ({}) } as DOMRect
    })
    vi.spyOn(window, 'getComputedStyle').mockImplementation((element: Element) => {
      const style = originalStyle.call(window, element)
      if (!(element as HTMLElement).classList?.contains('launcher-grid')) return style
      return Object.assign(style, { gridTemplateColumns: '100px 100px 100px', columnGap: '10px', rowGap: '10px', paddingLeft: '0px', paddingTop: '0px', gridAutoColumns: '100px', gridAutoRows: '100px' })
    })

    const wrapper = mount(App, { attachTo: document.body })
    await nextTick()
    await nextTick()
    await new Promise(resolve => setTimeout(resolve, 20))
    await nextTick()
    const tile = () => wrapper.find('[data-launcher-item-id="c"]').element as HTMLElement
    tile().dispatchEvent(pointerEvent('pointerdown', { pointerId: 9, clientX: 250, clientY: 50, timeStamp: 0 }))
    window.dispatchEvent(pointerEvent('pointermove', { pointerId: 9, clientX: 258, clientY: 50, timeStamp: 10 }))
    await nextTick()

    expect(wrapper.findAll('[data-launcher-item-id]').map(node => node.attributes('data-launcher-item-id'))).toEqual(['a', 'b', 'c', 'd', 'e'])
    expect(wrapper.find('[data-launcher-item-id="c"]').classes()).toContain('dragging')
    expect(wrapper.find('[data-launcher-item-id="d"]').attributes('style')).toContain('translate: 220px -110px')
    expect(wrapper.find('.drag-placeholder').exists()).toBe(false)
    expect(wrapper.find('.drag-preview').exists()).toBe(true)
    expect(wrapper.find('[data-launcher-item-id="c"] .app-name').text()).toBe('C')
    expect(wrapper.find('.drag-preview').attributes('style')).toContain('left: 228px')

    window.dispatchEvent(pointerEvent('pointermove', { pointerId: 9, clientX: 150, clientY: 50, timeStamp: 20 }))
    await nextTick()
    expect(wrapper.find('.drag-placeholder').exists()).toBe(false)

    window.dispatchEvent(pointerEvent('pointermove', { pointerId: 9, clientX: 105, clientY: 50, timeStamp: 30 }))
    await nextTick()
    expect(wrapper.find('.drag-placeholder').exists()).toBe(false)
    window.dispatchEvent(pointerEvent('pointermove', { pointerId: 9, clientX: 105, clientY: 50, timeStamp: 103 }))
    await nextTick()
    expect(wrapper.find('[data-launcher-item-id="b"]').attributes('style')).toContain('translate: 110px 0px')
    expect(wrapper.find('[data-launcher-item-id="d"]').attributes('style') ?? '').not.toContain('translate:')

    window.dispatchEvent(pointerEvent('pointermove', { pointerId: 9, clientX: 270, clientY: 50, timeStamp: 120 }))
    window.dispatchEvent(pointerEvent('pointermove', { pointerId: 9, clientX: 270, clientY: 50, timeStamp: 217 }))
    await nextTick()
    expect(wrapper.find('[data-launcher-item-id="b"]').attributes('style') ?? '').not.toContain('translate:')

    window.dispatchEvent(pointerEvent('pointermove', { pointerId: 9, clientX: 105, clientY: 50, timeStamp: 230 }))
    window.dispatchEvent(pointerEvent('pointermove', { pointerId: 9, clientX: 105, clientY: 50, timeStamp: 303 }))
    await nextTick()
    expect(wrapper.find('[data-launcher-item-id="b"]').attributes('style')).toContain('translate: 110px 0px')

    window.dispatchEvent(pointerEvent('pointermove', { pointerId: 9, clientX: 320, clientY: 160, timeStamp: 320 }))
    window.dispatchEvent(pointerEvent('pointermove', { pointerId: 9, clientX: 320, clientY: 160, timeStamp: 393 }))
    await nextTick()
    expect(wrapper.find('.drag-placeholder').exists()).toBe(false)
    expect(wrapper.find('[data-launcher-item-id="d"]').attributes('style')).toContain('translate: 220px -110px')
    expect(wrapper.find('[data-launcher-item-id="e"]').attributes('style')).toContain('translate: -110px 0px')

    window.dispatchEvent(pointerEvent('pointerup', { pointerId: 9, clientX: 320, clientY: 160 }))
    await nextTick()
    expect(wrapper.find('.launcher-grid').classes()).toContain('committing-reorder')
    await nextTick()
    const reorder = bridge.invoke.mock.calls.find(([method]) => method === 'setCustomOrder')
    expect(reorder?.[1]).toEqual({ ids: ['a', 'b', 'd', 'e', 'c'] })

    const persistedCalls = bridge.invoke.mock.calls.filter(([method]) => method === 'setCustomOrder').length
    const dTile = wrapper.find('[data-launcher-item-id="d"]').element as HTMLElement
    dTile.dispatchEvent(pointerEvent('pointerdown', { pointerId: 10, clientX: 270, clientY: 50 }))
    window.dispatchEvent(pointerEvent('pointermove', { pointerId: 10, clientX: 278, clientY: 50 }))
    await nextTick()
    expect(wrapper.find('.drag-placeholder').exists()).toBe(false)
    window.dispatchEvent(pointerEvent('pointercancel', { pointerId: 10, clientX: 105, clientY: 50 }))
    await nextTick()
    expect(wrapper.findAll('[data-launcher-item-id]').map(node => node.attributes('data-launcher-item-id'))).toEqual(['a', 'b', 'd', 'e', 'c'])
    expect(bridge.invoke.mock.calls.filter(([method]) => method === 'setCustomOrder').length).toBe(persistedCalls)
    wrapper.unmount()
    vi.restoreAllMocks()
    HTMLElement.prototype.getBoundingClientRect = originalRect
  })
})
