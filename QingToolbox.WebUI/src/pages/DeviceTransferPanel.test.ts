import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { invoke } from '@tauri-apps/api/core'
import { open } from '@tauri-apps/plugin-dialog'
import { createPinia } from 'pinia'
import DeviceTransferPanel from './DeviceTransferPanel.vue'

vi.mock('@tauri-apps/api/core', () => ({ invoke: vi.fn() }))
vi.mock('@tauri-apps/plugin-dialog', () => ({ open: vi.fn(), save: vi.fn() }))

const wrappers: VueWrapper[] = []
afterEach(() => {
  wrappers.splice(0).forEach(wrapper => wrapper.unmount())
  vi.mocked(invoke).mockReset()
  vi.mocked(open).mockReset()
})

function transferState() {
  const peer = {
    serviceName: 'QingTransfer Android', displayName: 'PHONE', deviceId: 'phone-discovery-id',
    platform: 'android', addresses: ['192.168.1.20'], online: true,
  }
  return {
    discovery: { running: true, peers: [] },
    session: { state: 'Connected' as const, peer },
    incomingConnection: null,
    incomingFile: { name: 'photo.png', size: 1024 },
    receive: { defaultDirectory: null, useDefaultDirectory: false, autoAccept: false },
    transfer: null,
    lastCompleted: null,
    lastError: null,
  }
}

function panel(incoming: boolean) {
  const pinia = createPinia()
  const wrapper = mount(DeviceTransferPanel, {
    props: { device: { id: 'paired-record-id', name: 'PHONE' }, incoming },
    global: {
      plugins: [pinia],
      stubs: {
        QModal: { template: '<section><slot /><footer><slot name="actions" /></footer></section>' },
        QButton: { template: '<button><slot /></button>' },
        DeviceReceiveSettings: true,
      },
    },
  })
  wrappers.push(wrapper)
  return wrapper
}

describe('DeviceTransferPanel', () => {
  it('uses the host-authenticated incoming session without rediscovering its endpoint', async () => {
    vi.mocked(invoke).mockImplementation(async (command) => {
      if (command === 'get_device_transfer_target') throw new Error('incoming flow must not resolve discovery again')
      if (command === 'invoke_module') return transferState()
      return null
    })

    const wrapper = panel(true)
    await flushPromises()

    expect(vi.mocked(invoke)).not.toHaveBeenCalledWith('get_device_transfer_target', expect.anything())
    expect(wrapper.text()).toContain('photo.png')
    expect(wrapper.text()).not.toContain('incoming flow must not resolve discovery again')
  })

  it('still resolves a transfer endpoint before an outgoing file selection', async () => {
    vi.mocked(invoke).mockResolvedValue({
      deviceId: 'phone-discovery-id', platform: 'android', addresses: ['192.168.1.20'],
    })
    vi.mocked(open).mockResolvedValue(null)

    panel(false)
    await flushPromises()

    expect(vi.mocked(invoke)).toHaveBeenCalledWith('get_device_transfer_target', { peerId: 'paired-record-id' })
  })
})
