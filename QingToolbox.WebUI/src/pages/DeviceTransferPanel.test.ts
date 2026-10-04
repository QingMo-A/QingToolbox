import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { invoke } from '@tauri-apps/api/core'
import { PhysicalPosition } from '@tauri-apps/api/dpi'
import type { DragDropEvent } from '@tauri-apps/api/webviewWindow'
import { open, save } from '@tauri-apps/plugin-dialog'
import { createPinia } from 'pinia'
import DeviceTransferPanel from './DeviceTransferPanel.vue'

const native = vi.hoisted(() => ({ listen: vi.fn(), cleanup: vi.fn() }))
vi.mock('@tauri-apps/api/core', () => ({ invoke: vi.fn() }))
vi.mock('@tauri-apps/api/webviewWindow', () => ({ getCurrentWebviewWindow: () => ({ onDragDropEvent: native.listen }) }))
vi.mock('@tauri-apps/plugin-dialog', () => ({ open: vi.fn(), save: vi.fn() }))

const wrappers: VueWrapper[] = []
let dropHandler: (event: { payload: DragDropEvent }) => void
beforeEach(() => {
  vi.useFakeTimers()
  vi.stubGlobal('devicePixelRatio', 1)
  native.listen.mockImplementation(async handler => { dropHandler = handler; return native.cleanup })
})
afterEach(() => {
  wrappers.splice(0).forEach(wrapper => wrapper.unmount())
  vi.mocked(invoke).mockReset()
  vi.mocked(open).mockReset()
  vi.mocked(save).mockReset()
  native.listen.mockReset()
  native.cleanup.mockReset()
  vi.useRealTimers()
  vi.unstubAllGlobals()
})

function transferState() {
  const peer = {
    serviceName: 'QingTransfer Android', displayName: 'PHONE', deviceId: 'phone-discovery-id',
    platform: 'android', addresses: ['192.168.1.20'], online: true,
  }
  return {
    discovery: { running: true, peers: [peer] },
    session: { state: 'Connected', peer },
    incomingConnection: null,
    incomingFile: { name: 'photo.png', size: 1024 } as { name: string; size: number } | null,
    receive: { defaultDirectory: null, useDefaultDirectory: false, autoAccept: false },
    transfer: null,
    lastCompleted: null,
    lastError: null,
  }
}

function outgoingState() { return { ...transferState(), incomingFile: null } }
const device = { id: 'paired-record-id', name: 'PHONE' }
const target = { deviceId: 'phone-discovery-id', platform: 'android', addresses: ['192.168.1.20'] }

function mockOutgoing() {
  vi.mocked(invoke).mockImplementation(async command => command === 'get_device_transfer_target' ? target : outgoingState())
}

function panel(incoming: boolean) {
  const wrapper = mount(DeviceTransferPanel, {
    props: { device, incoming },
    global: {
      plugins: [createPinia()],
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

function setDropBounds(wrapper: VueWrapper) {
  vi.spyOn(wrapper.get('.transfer-drop-zone').element, 'getBoundingClientRect').mockReturnValue({
    x: 100, y: 100, left: 100, top: 100, right: 300, bottom: 250, width: 200, height: 150, toJSON: () => ({}),
  })
}

function actions() { return vi.mocked(invoke).mock.calls.filter(([command]) => command === 'invoke_device_transfer') }

describe('DeviceTransferPanel', () => {
  it('opens a drop zone without a picker, second pairing, or technical connection status', async () => {
    vi.mocked(invoke).mockResolvedValue({ ...outgoingState(), lastError: 'old rejection', lastCompleted: 'previous.png' })
    const wrapper = panel(false)
    await flushPromises()
    expect(wrapper.get('.transfer-drop-zone').text()).toContain('Drop a file here')
    expect(open).not.toHaveBeenCalled()
    expect(invoke).not.toHaveBeenCalledWith('get_device_transfer_target', expect.anything())
    expect(actions()).toHaveLength(0)
    expect(wrapper.text()).not.toMatch(/Connected|Searching|old rejection|Transfer successful/)
  })

  it('keeps the drop zone open when the user cancels the file picker', async () => {
    mockOutgoing()
    vi.mocked(open).mockResolvedValue(null)
    const wrapper = panel(false)
    await flushPromises()
    await wrapper.get('.transfer-drop-zone').trigger('click')
    await flushPromises()
    expect(open).toHaveBeenCalledWith(expect.objectContaining({ multiple: false, directory: false }))
    expect(wrapper.find('.transfer-drop-zone').exists()).toBe(true)
    expect(wrapper.emitted('close')).toBeUndefined()
    expect(actions()).toHaveLength(0)
  })

  it('sends the chosen file immediately to the paired identity with no sender confirmation', async () => {
    mockOutgoing()
    vi.mocked(open).mockResolvedValue('C:\\photo.png')
    const wrapper = panel(false)
    await flushPromises()
    await wrapper.get('.transfer-drop-zone').trigger('click')
    await flushPromises()
    expect(invoke).toHaveBeenCalledWith('get_device_transfer_target', { peerId: device.id })
    expect(invoke).toHaveBeenCalledWith('invoke_device_transfer', {
      peerId: device.id, action: { kind: 'sendFile', path: 'C:\\photo.png' },
    })
    expect(wrapper.text()).not.toContain('Send file')
    expect(vi.mocked(invoke).mock.calls.some(([command]) => /module/.test(command))).toBe(false)
  })

  it('accepts a native file drop only inside the drop zone and accounts for DPI', async () => {
    mockOutgoing()
    vi.stubGlobal('devicePixelRatio', 2)
    const wrapper = panel(false)
    await flushPromises()
    setDropBounds(wrapper)
    dropHandler({ payload: { type: 'enter', paths: ['D:\\报告.pdf'], position: new PhysicalPosition(300, 300) } })
    await flushPromises()
    expect(wrapper.get('.transfer-drop-zone').classes()).toContain('is-drag-over')
    expect(wrapper.text()).toContain('Release to send')
    dropHandler({ payload: { type: 'drop', paths: ['D:\\报告.pdf'], position: new PhysicalPosition(300, 300) } })
    await flushPromises()
    expect(invoke).toHaveBeenCalledWith('invoke_device_transfer', {
      peerId: device.id, action: { kind: 'sendFile', path: 'D:\\报告.pdf' },
    })
    expect(open).not.toHaveBeenCalled()
  })

  it('ignores drops outside the area and browser drag names, and clears hover on leave', async () => {
    mockOutgoing()
    const wrapper = panel(false)
    await flushPromises()
    setDropBounds(wrapper)
    dropHandler({ payload: { type: 'over', position: new PhysicalPosition(150, 150) } })
    await flushPromises()
    expect(wrapper.get('.transfer-drop-zone').classes()).toContain('is-drag-over')
    dropHandler({ payload: { type: 'leave' } })
    dropHandler({ payload: { type: 'drop', paths: ['C:\\outside.txt'], position: new PhysicalPosition(10, 10) } })
    await wrapper.get('.transfer-drop-zone').trigger('drop', { dataTransfer: { files: [{ name: 'untrusted.txt' }] } })
    await flushPromises()
    expect(wrapper.get('.transfer-drop-zone').classes()).not.toContain('is-drag-over')
    expect(actions()).toHaveLength(0)
  })

  it('rejects multiple dropped files instead of silently sending only the first one', async () => {
    mockOutgoing()
    const wrapper = panel(false)
    await flushPromises()
    setDropBounds(wrapper)
    dropHandler({ payload: { type: 'drop', paths: ['C:\\a.txt', 'C:\\b.txt'], position: new PhysicalPosition(150, 150) } })
    await flushPromises()
    expect(wrapper.text()).toContain('Please choose one file at a time')
    expect(actions()).toHaveLength(0)
  })

  it('opens the temporary channel using only the paired record id, then sends automatically', async () => {
    let connected = false
    const next = outgoingState()
    const idle = { ...next, session: { state: 'Idle', peer: null } }
    vi.mocked(open).mockResolvedValue('C:\\photo.png')
    vi.mocked(invoke).mockImplementation(async (command, args) => {
      if (command === 'get_device_transfer_target') return target
      if (command === 'invoke_device_transfer' && (args as { action: { kind: string } }).action.kind === 'connect') connected = true
      return connected ? next : idle
    })
    const wrapper = panel(false)
    await flushPromises()
    await wrapper.get('.transfer-drop-zone').trigger('click')
    await flushPromises()
    expect(invoke).toHaveBeenCalledWith('invoke_device_transfer', { peerId: device.id, action: { kind: 'connect' } })
    expect(wrapper.text()).toContain('Preparing transfer')
    expect(wrapper.text()).not.toMatch(/Connected|Searching/)
    await vi.advanceTimersByTimeAsync(500)
    expect(invoke).toHaveBeenCalledWith('invoke_device_transfer', { peerId: device.id, action: { kind: 'sendFile', path: 'C:\\photo.png' } })
  })

  it('does not use or disconnect a temporary session belonging to a different device', async () => {
    const other = { ...outgoingState(), session: { state: 'Connecting', peer: { ...transferState().session.peer, deviceId: 'other-device' } } }
    vi.mocked(invoke).mockImplementation(async command => command === 'get_device_transfer_target' ? target : other)
    vi.mocked(open).mockResolvedValue('C:\\photo.png')
    const wrapper = panel(false)
    await flushPromises()
    await wrapper.get('.transfer-drop-zone').trigger('click')
    await flushPromises()
    expect(wrapper.find('[role="alert"]').exists()).toBe(true)
    expect(actions()).toHaveLength(0)
    wrapper.unmount()
    await flushPromises()
    expect(actions()).toHaveLength(0)
  })

  it('uses the host-authenticated incoming offer without rediscovering or opening a picker', async () => {
    vi.mocked(invoke).mockResolvedValue(transferState())
    const wrapper = panel(true)
    await flushPromises()
    expect(invoke).not.toHaveBeenCalledWith('get_device_transfer_target', expect.anything())
    expect(wrapper.text()).toContain('photo.png')
    expect(wrapper.find('.transfer-drop-zone').exists()).toBe(false)
    expect(open).not.toHaveBeenCalled()
  })

  it('preserves receiver approval and its save-file selector', async () => {
    vi.mocked(invoke).mockResolvedValue(transferState())
    vi.mocked(save).mockResolvedValue('D:\\received.png')
    const wrapper = panel(true)
    await flushPromises()
    const accept = wrapper.findAll('button').find(button => button.text() === 'Save')!
    await accept.trigger('click')
    await flushPromises()
    expect(save).toHaveBeenCalledWith(expect.objectContaining({ defaultPath: 'photo.png' }))
    expect(invoke).toHaveBeenCalledWith('invoke_device_transfer', { peerId: device.id, action: { kind: 'acceptIncomingFile', destinationPath: 'D:\\received.png' } })
  })

  it('shows a waiting incoming offer instead of starting an outgoing send', async () => {
    vi.mocked(invoke).mockResolvedValue({ ...transferState(), incomingRequest: device })
    const wrapper = panel(false)
    await flushPromises()
    expect(wrapper.emitted('incoming')).toEqual([[device]])
    expect(open).not.toHaveBeenCalled()
    expect(actions()).toHaveLength(0)
  })

  it('handles an offer arriving while the outgoing picker is open without sending into it', async () => {
    let picked = false
    vi.mocked(open).mockImplementation(async () => { picked = true; return 'C:\\photo.png' })
    vi.mocked(invoke).mockImplementation(async command => {
      if (command === 'get_device_transfer_target') return target
      return { ...outgoingState(), incomingFile: picked ? transferState().incomingFile : null, incomingRequest: picked ? device : null }
    })
    const wrapper = panel(false)
    await flushPromises()
    await wrapper.get('.transfer-drop-zone').trigger('click')
    await flushPromises()
    expect(wrapper.emitted('incoming')).toEqual([[device]])
    expect(actions()).toHaveLength(0)
  })

  it('shows recipient acceptance separately from byte transfer and suppresses additional drops', async () => {
    let started = false
    vi.mocked(open).mockResolvedValue('C:\\photo.png')
    vi.mocked(invoke).mockImplementation(async (command, args) => {
      if (command === 'get_device_transfer_target') return target
      if (command === 'invoke_device_transfer' && (args as { action: { kind: string } }).action.kind === 'sendFile') started = true
      return { ...outgoingState(), transfer: started ? { name: 'photo.png', completed: 0, total: 1024, receiving: false, phase: 'WaitingAcceptance' } : null }
    })
    const wrapper = panel(false)
    await flushPromises()
    await wrapper.get('.transfer-drop-zone').trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('Waiting for the recipient')
    expect(wrapper.text()).toContain('0 B / 1 KB')
    expect(wrapper.text()).not.toContain('Connected')
    dropHandler({ payload: { type: 'drop', paths: ['C:\\second.txt'], position: new PhysicalPosition(150, 150) } })
    await flushPromises()
    expect(actions()).toHaveLength(1)
  })

  it('formats receiving progress with automatic units', async () => {
    vi.mocked(invoke).mockResolvedValue({
      ...outgoingState(),
      transfer: { name: 'photo.png', completed: 1536 * 1024, total: 20 * 1024 ** 2, receiving: true, phase: 'Transferring' },
    })
    const wrapper = panel(true)
    await flushPromises()
    expect(wrapper.text()).toContain('1.5 MB / 20 MB')
    expect(wrapper.text()).not.toContain('字节')
    expect(wrapper.get('progress').attributes('value')).toBe(String(1536 * 1024))
    expect(wrapper.get('progress').attributes('max')).toBe(String(20 * 1024 ** 2))
  })

  it('shows only the success outcome after this transfer completes', async () => {
    let started = false
    vi.mocked(open).mockResolvedValue('C:\\photo.png')
    vi.mocked(invoke).mockImplementation(async (command, args) => {
      if (command === 'get_device_transfer_target') return target
      if (command === 'invoke_device_transfer' && (args as { action: { kind: string } }).action.kind === 'sendFile') started = true
      return { ...outgoingState(), lastCompleted: started ? 'photo.png' : null, lastError: 'stale connection detail' }
    })
    const wrapper = panel(false)
    await flushPromises()
    await wrapper.get('.transfer-drop-zone').trigger('click')
    await flushPromises()
    await vi.advanceTimersByTimeAsync(500)
    expect(wrapper.get('.transfer-success').text()).toContain('Transfer successful')
    expect(wrapper.text()).not.toMatch(/Connected|photo.png|stale connection detail|File receiving settings/)
  })

  it('does not send if the picker resolves after unmounting and stops native listening', async () => {
    mockOutgoing()
    let resolvePicker!: (path: string) => void
    vi.mocked(open).mockImplementation(() => new Promise(resolve => { resolvePicker = resolve }))
    const wrapper = panel(false)
    await flushPromises()
    await wrapper.get('.transfer-drop-zone').trigger('click')
    wrapper.unmount()
    resolvePicker('C:\\late.png')
    await flushPromises()
    expect(actions()).toHaveLength(0)
    expect(native.cleanup).toHaveBeenCalledTimes(1)
  })

  it('cleans up a native listener whose registration resolves after unmounting', async () => {
    mockOutgoing()
    let resolveListen!: (cleanup: () => void) => void
    native.listen.mockImplementation(() => new Promise(resolve => { resolveListen = resolve }))
    const wrapper = panel(false)
    await flushPromises()
    wrapper.unmount()
    resolveListen(native.cleanup)
    await flushPromises()
    expect(native.cleanup).toHaveBeenCalledTimes(1)
  })
})
