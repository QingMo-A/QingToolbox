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
  it('formats the incoming file size and byte progress with automatic units', async () => {
    vi.mocked(invoke).mockResolvedValue({
      ...transferState(),
      incomingFile: { name: 'photo.png', size: 20 * 1024 ** 2 },
      transfer: { name: 'photo.png', completed: 1536 * 1024, total: 20 * 1024 ** 2, receiving: true },
    })
    const wrapper = panel(true)
    await flushPromises()
    expect(wrapper.text()).toContain('20 MB')
    expect(wrapper.text()).toContain('1.5 MB / 20 MB')
    expect(wrapper.text()).not.toContain('字节')
    expect(wrapper.find('progress').attributes('value')).toBe(String(1536 * 1024))
    expect(wrapper.find('progress').attributes('max')).toBe(String(20 * 1024 ** 2))
  })

  it('uses the host-authenticated incoming session without rediscovering its endpoint', async () => {
    vi.mocked(invoke).mockImplementation(async (command) => {
      if (command === 'get_device_transfer_target') throw new Error('incoming flow must not resolve discovery again')
      if (command === 'get_device_transfer_state') return transferState()
      return null
    })

    const wrapper = panel(true)
    await flushPromises()

    expect(vi.mocked(invoke)).not.toHaveBeenCalledWith('get_device_transfer_target', expect.anything())
    expect(wrapper.text()).toContain('photo.png')
    expect(wrapper.text()).not.toContain('incoming flow must not resolve discovery again')
    expect(vi.mocked(invoke).mock.calls.some(([command]) => /module/.test(command))).toBe(false)
  })

  it('still resolves a transfer endpoint before an outgoing file selection', async () => {
    vi.mocked(invoke).mockImplementation(async command => {
      if (command === 'get_device_transfer_state') return { ...transferState(), incomingFile: null }
      return { deviceId: 'phone-discovery-id', platform: 'android', addresses: ['192.168.1.20'] }
    })
    vi.mocked(open).mockResolvedValue(null)

    panel(false)
    await flushPromises()

    expect(vi.mocked(invoke)).toHaveBeenCalledWith('get_device_transfer_target', { peerId: 'paired-record-id' })
  })

  it('sends immediately on an existing paired session with no second sender confirmation', async () => {
    vi.mocked(open).mockResolvedValue('C:\\photo.png')
    vi.mocked(invoke).mockImplementation(async command => {
      if (command === 'get_device_transfer_target') return {
        deviceId: 'phone-discovery-id', platform: 'android', addresses: ['192.168.1.20'],
      }
      return { ...transferState(), incomingFile: null }
    })
    panel(false)
    await flushPromises()
    expect(vi.mocked(invoke)).toHaveBeenCalledWith('invoke_device_transfer', {
      peerId: 'paired-record-id', action: { kind: 'sendFile', path: 'C:\\photo.png' },
    })
    expect(vi.mocked(invoke).mock.calls.some(([command]) => /module/.test(command))).toBe(false)
  })

  it('connects using only the paired record id, never a UI-selected service address', async () => {
    vi.mocked(open).mockResolvedValue('C:\\photo.png')
    const next = { ...transferState(), incomingFile: null }
    const idle = { ...next, session: { state: 'Idle', peer: null }, discovery: { running: true, peers: [next.session.peer] } }
    vi.mocked(invoke).mockImplementation(async command => {
      if (command === 'get_device_transfer_target') return {
        deviceId: 'phone-discovery-id', platform: 'android', addresses: ['192.168.1.20'],
      }
      return idle
    })
    panel(false)
    await flushPromises()
    expect(vi.mocked(invoke)).toHaveBeenCalledWith('invoke_device_transfer', {
      peerId: 'paired-record-id', action: { kind: 'connect' },
    })
  })

  it('shows a waiting receive instead of opening a picker or sending into its occupied session', async () => {
    const device = { id: 'paired-record-id', name: 'PHONE' }
    vi.mocked(invoke).mockResolvedValue({ ...transferState(), incomingRequest: device })
    const wrapper = panel(false)
    await flushPromises()
    expect(wrapper.emitted('incoming')).toEqual([[device]])
    expect(open).not.toHaveBeenCalled()
    expect(vi.mocked(invoke).mock.calls.some(([command]) => command === 'invoke_device_transfer')).toBe(false)
  })

  it('handles an offer arriving while the outgoing file picker is open without trying to send', async () => {
    const device = { id: 'paired-record-id', name: 'PHONE' }
    let picked = false
    vi.mocked(open).mockImplementation(async () => { picked = true; return 'C:\\photo.png' })
    vi.mocked(invoke).mockImplementation(async command => {
      if (command === 'get_device_transfer_target') return {
        deviceId: 'phone-discovery-id', platform: 'android', addresses: ['192.168.1.20'],
      }
      return { ...transferState(), incomingFile: picked ? transferState().incomingFile : null,
        incomingRequest: picked ? device : null }
    })
    const wrapper = panel(false)
    await flushPromises()
    expect(wrapper.emitted('incoming')).toEqual([[device]])
    expect(vi.mocked(invoke).mock.calls.some(([command]) => command === 'invoke_device_transfer')).toBe(false)
  })
})
