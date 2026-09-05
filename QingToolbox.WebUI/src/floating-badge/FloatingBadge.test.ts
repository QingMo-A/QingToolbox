import { mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import FloatingBadge from './FloatingBadge.vue'

const invoke = vi.hoisted(() => vi.fn(async () => undefined))
vi.mock('@tauri-apps/api/core', () => ({ invoke }))

describe('FloatingBadge', () => {
  beforeEach(() => invoke.mockClear())

  it('restores the main window on an undragged pointer release', async () => {
    const wrapper = mount(FloatingBadge)
    await vi.waitFor(() => expect(invoke).toHaveBeenCalledWith('prepare_floating_badge_window'))
    wrapper.get('button').element.dispatchEvent(new PointerEvent('pointerdown', { button: 0, screenX: 20, screenY: 20 }))
    window.dispatchEvent(new PointerEvent('pointerup'))
    await vi.waitFor(() => expect(invoke).toHaveBeenCalledWith('show_main_from_floating_badge'))
    wrapper.unmount()
  })

  it('starts native dragging without restoring after crossing the threshold', async () => {
    const wrapper = mount(FloatingBadge)
    wrapper.get('button').element.dispatchEvent(new PointerEvent('pointerdown', { button: 0, screenX: 20, screenY: 20 }))
    window.dispatchEvent(new PointerEvent('pointermove', { screenX: 30, screenY: 30 }))
    await vi.waitFor(() => expect(invoke).toHaveBeenCalledWith('start_floating_badge_drag'))
    expect(invoke).not.toHaveBeenCalledWith('show_main_from_floating_badge')
    wrapper.unmount()
  })
})
