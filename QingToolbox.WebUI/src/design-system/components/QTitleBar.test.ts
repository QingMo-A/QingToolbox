import { mount, flushPromises } from '@vue/test-utils'
import { createPinia } from 'pinia'
import { describe, it, expect, vi } from 'vitest'
import QTitleBar from './QTitleBar.vue'
import { useAppStore } from '../../app/store'

const invoke = vi.hoisted(() => vi.fn(async (_command: string, _args?: unknown) => undefined))
vi.mock('@tauri-apps/api/core', () => ({ invoke }))

describe('native title bar', () => {
  it('marks the development host without changing the production title', async () => {
    const pinia = createPinia()
    const wrapper = mount(QTitleBar, { global: { plugins: [pinia] } })
    expect(wrapper.get('.q-titlebar-drag span').text()).toBe('QingToolbox')
    useAppStore(pinia).rebuild({ environmentKind: 'Development', environmentDisplayName: 'QingToolbox [Dev]', hostVersion: '1', protocolVersion: 4, totalModuleCount: 0, validModuleCount: 0, runningModuleCount: 0, generatedAt: new Date().toISOString() })
    await wrapper.vm.$nextTick()
    expect(wrapper.get('.q-titlebar-drag span').text()).toBe('QingToolbox [Dev]')
    wrapper.unmount()
  })
  it('dispatches the four window actions without initiating window drag', async () => {
    invoke.mockClear()
    const wrapper = mount(QTitleBar, { global: { plugins: [createPinia()] } })
    for (const button of wrapper.findAll('button')) await button.trigger('click')
    await flushPromises()
    expect(invoke.mock.calls.filter(([command]) => command === 'control_main_window')).toEqual([
      ['control_main_window', { action: 'floatingBadge' }],
      ['control_main_window', { action: 'minimize' }],
      ['control_main_window', { action: 'toggleMaximize' }],
      ['control_main_window', { action: 'close' }],
    ])
    wrapper.unmount()
  })
})
