import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises } from '@vue/test-utils'
import { invoke } from '@tauri-apps/api/core'
import { listen } from '@tauri-apps/api/event'
import { createDeviceTransferRequests } from './deviceTransferRequests'

vi.mock('@tauri-apps/api/core', () => ({ invoke: vi.fn() }))
vi.mock('@tauri-apps/api/event', () => ({ listen: vi.fn() }))

const controllers: ReturnType<typeof createDeviceTransferRequests>[] = []
const device = { id: 'paired-phone', name: 'PHONE' }
function controller() {
  vi.useFakeTimers()
  vi.mocked(listen).mockResolvedValue(vi.fn())
  const value = createDeviceTransferRequests()
  controllers.push(value)
  return value
}
afterEach(() => {
  controllers.splice(0).forEach(value => value.stop())
  vi.useRealTimers()
  vi.clearAllMocks()
})

describe('global incoming file requests', () => {
  it('recovers an offer already pending when the UI starts', async () => {
    const value = controller()
    vi.mocked(invoke).mockResolvedValue({ incomingRequest: device })
    value.start()
    await flushPromises()
    expect(value.request.value).toEqual({ device, incoming: true })
    expect(listen).toHaveBeenCalledWith('qing:incoming-device-file', expect.any(Function), { target: 'main' })
  })

  it('opens an arriving offer without a native event or clicking Send', async () => {
    const value = controller()
    vi.mocked(invoke).mockResolvedValue({ incomingRequest: null })
    value.start()
    await flushPromises()
    expect(value.request.value).toBeNull()
    vi.mocked(invoke).mockResolvedValue({ incomingRequest: device })
    await vi.advanceTimersByTimeAsync(500)
    expect(value.request.value).toEqual({ device, incoming: true })
    const shown = value.request.value
    await vi.advanceTimersByTimeAsync(1500)
    expect(value.request.value).toBe(shown)
  })

  it('still polls when native listener registration fails and recovers transient IPC errors', async () => {
    const value = controller()
    vi.mocked(listen).mockRejectedValue(new Error('listener unavailable'))
    vi.mocked(invoke).mockRejectedValueOnce(new Error('temporarily unavailable'))
      .mockResolvedValue({ incomingRequest: device })
    value.start()
    await flushPromises()
    await vi.advanceTimersByTimeAsync(500)
    expect(value.request.value).toEqual({ device, incoming: true })
  })

  it('resynchronizes immediately when a hidden window regains focus', async () => {
    const value = controller()
    vi.mocked(invoke).mockResolvedValue({ incomingRequest: null })
    value.start()
    await flushPromises()
    vi.mocked(invoke).mockResolvedValue({ incomingRequest: device })
    window.dispatchEvent(new Event('focus'))
    await flushPromises()
    expect(value.request.value?.incoming).toBe(true)
  })

  it('prioritizes a pending receive rather than starting a conflicting outgoing panel', async () => {
    const value = controller()
    vi.mocked(invoke).mockResolvedValue({ incomingRequest: device })
    await value.openOutgoing(device)
    expect(value.request.value).toEqual({ device, incoming: true })
    expect(vi.mocked(invoke).mock.calls.every(([command]) => command === 'get_device_transfer_state')).toBe(true)
  })

  it('does not resurrect a dialog after disposal or leave the timer running', async () => {
    const value = controller()
    let finish!: (state: unknown) => void
    vi.mocked(invoke).mockImplementation(() => new Promise(resolve => { finish = resolve }))
    value.start()
    value.stop()
    finish({ incomingRequest: device })
    await flushPromises()
    await vi.advanceTimersByTimeAsync(1000)
    expect(value.request.value).toBeNull()
    expect(invoke).toHaveBeenCalledTimes(1)
  })
})
