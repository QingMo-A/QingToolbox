import { mount, flushPromises } from '@vue/test-utils'
import { createPinia } from 'pinia'
import { describe, it, expect, vi } from 'vitest'
import QTitleBar from './QTitleBar.vue'

const invoke = vi.hoisted(() => vi.fn(async () => undefined))
vi.mock('@tauri-apps/api/core', () => ({ invoke }))

describe('native title bar', () => {
  it('dispatches the four window actions without initiating window drag', async () => {
    invoke.mockClear()
    const wrapper = mount(QTitleBar, { global: { plugins: [createPinia()] } })
    for (const button of wrapper.findAll('button')) await button.trigger('click')
    await flushPromises()
    expect(invoke.mock.calls).toEqual([
      ['control_main_window', { action: 'floatingBadge' }],
      ['control_main_window', { action: 'minimize' }],
      ['control_main_window', { action: 'toggleMaximize' }],
      ['control_main_window', { action: 'close' }],
    ])
    wrapper.unmount()
  })
})
