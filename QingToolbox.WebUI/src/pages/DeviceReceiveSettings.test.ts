import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia } from 'pinia'
import { invoke } from '@tauri-apps/api/core'
import DeviceReceiveSettings from './DeviceReceiveSettings.vue'

vi.mock('@tauri-apps/api/core', () => ({ invoke: vi.fn() }))
vi.mock('@tauri-apps/plugin-dialog', () => ({ open: vi.fn() }))
afterEach(() => vi.mocked(invoke).mockReset())

describe('host-owned device receive preferences', () => {
  it('works without a module and persists settings through the host API', async () => {
    const receive = { defaultDirectory: 'C:/Downloads', useDefaultDirectory: true, autoAccept: false }
    vi.mocked(invoke).mockResolvedValue({ receive })
    const wrapper = mount(DeviceReceiveSettings, {
      global: { plugins: [createPinia()], stubs: {
        QModal: { template: '<section><slot /><slot name="actions" /></section>' },
        QButton: { template: '<button><slot /></button>' },
      } },
    })
    try {
      await flushPromises()
      expect(wrapper.text()).toContain('C:/Downloads')
      expect(invoke).toHaveBeenCalledWith('get_device_transfer_state')
      await wrapper.findAll('input')[1]!.setValue(true)
      await flushPromises()
      expect(invoke).toHaveBeenCalledWith('update_device_receive_preferences', { preferences: { autoAccept: true } })
      expect(vi.mocked(invoke).mock.calls.some(([command]) => /module/.test(command))).toBe(false)
    } finally { wrapper.unmount() }
  })
})
