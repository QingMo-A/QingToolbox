import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import type { HostUpdateClient } from '../../bridge/clients/HostUpdateClient'
import type { HostUpdateSnapshot } from '../../contracts/hostUpdate'
import { useAppStore } from '../../app/store'
import QHostUpdateBanner from './QHostUpdateBanner.vue'

const wrappers: VueWrapper[] = []
let visibility: DocumentVisibilityState = 'visible'

const snapshot = (overrides: Partial<HostUpdateSnapshot> = {}): HostUpdateSnapshot => ({
  generatedAt: new Date().toISOString(), state: 'UpdateAvailable', currentVersion: '0.2.7-alpha',
  latestVersion: '0.2.8-alpha', publishedAt: '', lastChecked: '', summary: 'Update', showBanner: true,
  downloadState: 'Idle', bytesReceived: 0, expectedBytes: 100, downloadError: '', canCheck: true,
  canDownload: true, canCancelDownload: false, canInstall: false, installationSupported: true,
  installMessage: '', ...overrides,
})

function render(getSnapshot: () => Promise<HostUpdateSnapshot>, clientOverrides: Record<string, unknown> = {}) {
  const pinia = createPinia(); setActivePinia(pinia)
  useAppStore().rebuild({
    environmentKind: 'Production', environmentDisplayName: 'QingToolbox', hostVersion: '0.2.8-alpha',
    protocolVersion: 4, totalModuleCount: 0, validModuleCount: 0, runningModuleCount: 0,
    generatedAt: new Date().toISOString(),
  })
  const wrapper = mount(QHostUpdateBanner, {
    global: {
      plugins: [pinia],
      provide: { hostUpdateClient: { getSnapshot, ...clientOverrides } as unknown as HostUpdateClient },
      stubs: { QIcon: true, QButton: { template: '<button><slot /></button>' } },
    },
  })
  wrappers.push(wrapper)
  return wrapper
}

afterEach(() => {
  wrappers.splice(0).forEach(wrapper => wrapper.unmount())
  vi.useRealTimers()
  vi.restoreAllMocks()
  visibility = 'visible'
})

describe('QHostUpdateBanner background polling', () => {
  it('does not poll an idle update snapshot every second', async () => {
    vi.useFakeTimers()
    vi.spyOn(document, 'visibilityState', 'get').mockImplementation(() => visibility)
    const getSnapshot = vi.fn().mockResolvedValue(snapshot())
    render(getSnapshot)
    await flushPromises()

    expect(getSnapshot).toHaveBeenCalledTimes(1)
    await vi.advanceTimersByTimeAsync(5_000)
    expect(getSnapshot).toHaveBeenCalledTimes(1)
  })

  it('stops active polling while hidden and refreshes once when visible again', async () => {
    vi.useFakeTimers()
    vi.spyOn(document, 'visibilityState', 'get').mockImplementation(() => visibility)
    const getSnapshot = vi.fn().mockResolvedValue(snapshot({
      downloadState: 'Downloading', canDownload: false, canCancelDownload: true, bytesReceived: 25,
    }))
    render(getSnapshot)
    await flushPromises()

    await vi.advanceTimersByTimeAsync(1_000)
    expect(getSnapshot).toHaveBeenCalledTimes(2)

    visibility = 'hidden'
    document.dispatchEvent(new Event('visibilitychange'))
    await vi.advanceTimersByTimeAsync(5_000)
    expect(getSnapshot).toHaveBeenCalledTimes(2)

    visibility = 'visible'
    document.dispatchEvent(new Event('visibilitychange'))
    await flushPromises()
    expect(getSnapshot).toHaveBeenCalledTimes(3)
    await vi.advanceTimersByTimeAsync(999)
    expect(getSnapshot).toHaveBeenCalledTimes(3)
  })

  it('defers the initial snapshot request until a hidden page becomes visible', async () => {
    vi.useFakeTimers()
    visibility = 'hidden'
    vi.spyOn(document, 'visibilityState', 'get').mockImplementation(() => visibility)
    const getSnapshot = vi.fn().mockResolvedValue(snapshot())
    render(getSnapshot)
    await flushPromises()
    await vi.advanceTimersByTimeAsync(3_000)
    expect(getSnapshot).not.toHaveBeenCalled()

    visibility = 'visible'
    document.dispatchEvent(new Event('visibilitychange'))
    await flushPromises()
    expect(getSnapshot).toHaveBeenCalledTimes(1)
  })

  it('polls while a download command is pending even before its first progress snapshot', async () => {
    vi.useFakeTimers()
    vi.spyOn(document, 'visibilityState', 'get').mockImplementation(() => visibility)
    const getSnapshot = vi.fn()
      .mockResolvedValueOnce(snapshot())
      .mockResolvedValue(snapshot({
        downloadState: 'Downloading', canDownload: false, canCancelDownload: true, bytesReceived: 10,
      }))
    let finishDownload!: (value: HostUpdateSnapshot) => void
    const download = vi.fn(() => new Promise<HostUpdateSnapshot>(resolve => { finishDownload = resolve }))
    const wrapper = render(getSnapshot, { download })
    await flushPromises()
    expect(wrapper.text()).toContain('Download')
    const downloadButton = wrapper.findAll('button')
      .find(button => button.text().includes('Download'))
    expect(downloadButton).toBeDefined()

    await downloadButton!.trigger('click')
    await vi.advanceTimersByTimeAsync(1_000)
    expect(download).toHaveBeenCalledTimes(1)
    expect(getSnapshot).toHaveBeenCalledTimes(2)

    finishDownload(snapshot({
      downloadState: 'ReadyToInstall', canDownload: false, canCancelDownload: false, canInstall: true,
      bytesReceived: 100,
    }))
    await flushPromises()
    await vi.advanceTimersByTimeAsync(3_000)
    expect(getSnapshot).toHaveBeenCalledTimes(2)
  })
})
