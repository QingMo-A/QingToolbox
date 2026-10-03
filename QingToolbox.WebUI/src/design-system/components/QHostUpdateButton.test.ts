import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import type { HostUpdateClient } from '../../bridge/clients/HostUpdateClient'
import type { HostUpdateSnapshot } from '../../contracts/hostUpdate'
import { useAppStore } from '../../app/store'
import { useHostUpdateStore } from '../../app/hostUpdateStore'
import { useToastStore } from '../../app/toastStore'
import QHostUpdateButton from './QHostUpdateButton.vue'
import QTitleBar from './QTitleBar.vue'
import DevelopmentHomePage from '../../pages/DevelopmentHomePage.vue'

const invoke = vi.hoisted(() => vi.fn(async () => undefined))
const events = vi.hoisted(() => new Map<string, () => void>())
vi.mock('@tauri-apps/api/core', () => ({ invoke }))
vi.mock('@tauri-apps/api/event', () => ({ listen: vi.fn(async (name: string, callback: () => void) => { events.set(name, callback); return () => events.delete(name) }) }))

const wrappers: VueWrapper[] = []
let visibility: DocumentVisibilityState = 'visible'
const snapshot = (overrides: Partial<HostUpdateSnapshot> = {}): HostUpdateSnapshot => ({
  generatedAt: new Date().toISOString(), state: 'UpdateAvailable', currentVersion: '0.3.2-alpha',
  latestVersion: '0.3.3-alpha', publishedAt: '', lastChecked: '', summary: 'Update', showBanner: true,
  downloadState: 'NotDownloaded', bytesReceived: 0, expectedBytes: 100, downloadError: '', canCheck: true,
  canDownload: true, canCancelDownload: false, canInstall: false, installationSupported: false,
  installMessage: '', ...overrides,
})
const ready = () => snapshot({ downloadState: 'ReadyToInstall', bytesReceived: 100,
  canDownload: false, canInstall: true, installationSupported: true })
const downloading = () => snapshot({ downloadState: 'Downloading', bytesReceived: 25,
  canDownload: false, canCancelDownload: true })

function render(overrides: Partial<HostUpdateClient> = {}, options: {
  environment?: 'Production' | 'Development'; connected?: boolean; titlebar?: boolean;
  backgroundUpdateChecks?: boolean;
} = {}) {
  const pinia = createPinia(); setActivePinia(pinia)
  const app = useAppStore()
  const connect = () => app.rebuild({
    environmentKind: options.environment ?? 'Production', environmentDisplayName: 'QingToolbox',
    hostVersion: '0.3.2-alpha', protocolVersion: 4, totalModuleCount: 0, validModuleCount: 0,
    runningModuleCount: 0, generatedAt: new Date().toISOString(),
    backgroundUpdateChecks: options.backgroundUpdateChecks,
  })
  if (options.connected !== false) connect()
  const client = {
    getSnapshot: vi.fn().mockResolvedValue(snapshot()),
    check: vi.fn().mockResolvedValue(snapshot()),
    download: vi.fn().mockResolvedValue(downloading()),
    install: vi.fn().mockResolvedValue(snapshot({ state: 'Installing', downloadState: 'Installing',
      canDownload: false, canInstall: false })),
    ...overrides,
  }
  const wrapper = mount(options.titlebar ? QTitleBar : QHostUpdateButton, {
    global: { plugins: [pinia], provide: { hostUpdateClient: client } },
  })
  wrappers.push(wrapper)
  return { wrapper, client, connect, pinia, store: useHostUpdateStore(), toast: useToastStore() }
}
afterEach(() => {
  wrappers.splice(0).forEach(wrapper => wrapper.unmount())
  vi.useRealTimers(); vi.restoreAllMocks(); invoke.mockClear()
  visibility = 'visible'
  vi.unstubAllGlobals(); events.clear()
})

describe('titlebar host update', () => {
  it('refreshes a backend completion that races the initial hidden startup snapshot', async () => {
    vi.stubGlobal('__TAURI_INTERNALS__', {})
    let resolve!: (value: HostUpdateSnapshot) => void
    const getSnapshot = vi.fn().mockImplementationOnce(() => new Promise(r => { resolve = r })).mockResolvedValue(snapshot())
    const { wrapper, client } = render({ getSnapshot }, { backgroundUpdateChecks: true })
    await flushPromises()
    events.get('qmod:host-update-changed')!()
    resolve(snapshot({ state: 'NotChecked', canDownload: false })); await flushPromises()
    expect(wrapper.find('button').exists()).toBe(true)
    expect(client.check).not.toHaveBeenCalled()
    expect(getSnapshot).toHaveBeenCalledTimes(2)
  })
  it('does not bypass native startup preferences with a duplicate frontend check', async () => {
    const { wrapper, client } = render({ getSnapshot: vi.fn().mockResolvedValue(snapshot({ state: 'NotChecked', canDownload: false })) }, { backgroundUpdateChecks: true })
    await flushPromises()
    expect(client.getSnapshot).toHaveBeenCalledTimes(1)
    expect(client.check).not.toHaveBeenCalled()
    expect(wrapper.find('button').exists()).toBe(false)
  })
  it('checks once on startup, even when the main window starts hidden', async () => {
    vi.useFakeTimers()
    visibility = 'hidden'
    vi.spyOn(document, 'visibilityState', 'get').mockImplementation(() => visibility)
    const { wrapper, client, connect } = render({
      getSnapshot: vi.fn().mockResolvedValue(snapshot({ state: 'NotChecked', latestVersion: '', canDownload: false })),
      check: vi.fn().mockResolvedValue(snapshot({ state: 'UpToDate', latestVersion: '', canDownload: false })),
    }, { connected: false })
    await flushPromises()
    expect(client.getSnapshot).not.toHaveBeenCalled()
    connect(); await flushPromises()
    expect(client.getSnapshot).toHaveBeenCalledTimes(1)
    expect(client.check).toHaveBeenCalledTimes(1)
    expect(wrapper.find('button').exists()).toBe(false)
    visibility = 'visible'; document.dispatchEvent(new Event('visibilitychange'))
    connect(); await vi.advanceTimersByTimeAsync(5000)
    expect(client.check).toHaveBeenCalledTimes(1)
    expect(client.getSnapshot).toHaveBeenCalledTimes(1)
  })

  it.each(['UpToDate', 'Failed', 'DisabledByEnvironment'])('does not add UI for %s', async state => {
    const { wrapper, client, toast } = render({
      getSnapshot: vi.fn().mockResolvedValue(snapshot({ state, canDownload: false })),
    })
    await flushPromises()
    expect(wrapper.find('button').exists()).toBe(false)
    expect(wrapper.text()).toBe('')
    expect(client.check).not.toHaveBeenCalled()
    expect(toast.visible).toBe(false)
  })

  it('keeps a failed startup request silent and does not automatically retry', async () => {
    vi.useFakeTimers()
    const { wrapper, client, connect, toast } = render({ getSnapshot: vi.fn().mockRejectedValue(new Error('offline')) })
    await flushPromises()
    connect(); document.dispatchEvent(new Event('visibilitychange'))
    await vi.advanceTimersByTimeAsync(5000)
    expect(wrapper.find('button').exists()).toBe(false)
    expect(client.getSnapshot).toHaveBeenCalledTimes(1)
    expect(toast.visible).toBe(false)
  })

  it('does not run a production update check in the development environment', async () => {
    const { wrapper, client } = render({}, { environment: 'Development' })
    await flushPromises()
    expect(client.getSnapshot).not.toHaveBeenCalled()
    expect(wrapper.find('button').exists()).toBe(false)
  })

  it('shows the diagnostic preview with the real titlebar button without checking, downloading, or installing', async () => {
    const { wrapper, client, pinia, store } = render({}, { environment: 'Development', titlebar: true })
    const diagnostics = mount(DevelopmentHomePage, {
      global: { plugins: [pinia], provide: { appClient: {} } },
    })
    wrappers.push(diagnostics)
    expect(wrapper.find('.q-host-update-button').exists()).toBe(false)
    await diagnostics.get('[data-test="show-download-button"]').trigger('click')
    const previewButton = wrapper.get('.q-host-update-button')
    expect(previewButton.attributes('disabled')).toBeUndefined()
    await previewButton.trigger('click'); await flushPromises()
    expect(client.getSnapshot).not.toHaveBeenCalled()
    expect(client.check).not.toHaveBeenCalled()
    expect(client.download).not.toHaveBeenCalled()
    expect(client.install).not.toHaveBeenCalled()
    expect(store.snapshot).toBeNull()
  })

  it('places the icon beside the title and does not start dragging or maximizing', async () => {
    const { wrapper } = render({}, { titlebar: true })
    await flushPromises()
    const button = wrapper.get('.q-titlebar-drag .q-host-update-button')
    expect(button.attributes('aria-label')).toContain('0.3.3-alpha')
    await button.trigger('mousedown', { button: 0 })
    await button.trigger('dblclick')
    expect(invoke.mock.calls.filter(args => (args as unknown as string[])[0] === 'control_main_window')).toHaveLength(0)
    expect(wrapper.find('.q-host-update-banner').exists()).toBe(false)
  })

  it('does not poll an idle available update', async () => {
    vi.useFakeTimers()
    const { client } = render()
    await flushPromises(); await vi.advanceTimersByTimeAsync(5000)
    expect(client.getSnapshot).toHaveBeenCalledTimes(1)
  })

  it('downloads, shows progress, verifies, then installs after one click, including while hidden', async () => {
    vi.useFakeTimers()
    vi.spyOn(document, 'visibilityState', 'get').mockImplementation(() => visibility)
    const getSnapshot = vi.fn().mockResolvedValueOnce(snapshot()).mockResolvedValueOnce(downloading())
      .mockResolvedValueOnce(snapshot({ downloadState: 'Verifying', canDownload: false }))
      .mockResolvedValue(ready())
    const { wrapper, client } = render({ getSnapshot })
    await flushPromises()
    await wrapper.get('button').trigger('click'); await flushPromises()
    expect(client.download).toHaveBeenCalledTimes(1)
    expect(wrapper.get('button').attributes('disabled')).toBeDefined()
    expect(client.install).not.toHaveBeenCalled()
    visibility = 'hidden'; document.dispatchEvent(new Event('visibilitychange'))
    await vi.advanceTimersByTimeAsync(1000)
    expect(wrapper.get('button').attributes('aria-label')).toContain('25%')
    await vi.advanceTimersByTimeAsync(1000)
    expect(wrapper.get('button').attributes('aria-label')).toBe('Verifying update…')
    await vi.advanceTimersByTimeAsync(1000)
    expect(client.install).toHaveBeenCalledTimes(1)
    await vi.advanceTimersByTimeAsync(3000)
    expect(client.install).toHaveBeenCalledTimes(1)
    expect(getSnapshot).toHaveBeenCalledTimes(4)
  })

  it('does not install after a failed download and allows retry', async () => {
    vi.useFakeTimers()
    const getSnapshot = vi.fn().mockResolvedValueOnce(snapshot())
      .mockResolvedValue(snapshot({ downloadState: 'Failed' }))
    const { wrapper, client, toast } = render({ getSnapshot })
    await flushPromises()
    await wrapper.get('button').trigger('click'); await flushPromises()
    await vi.advanceTimersByTimeAsync(1000)
    expect(client.install).not.toHaveBeenCalled()
    expect(toast.kind).toBe('error')
    expect(wrapper.get('button').attributes('disabled')).toBeUndefined()
    expect(wrapper.get('button').attributes('aria-label')).toContain('retry')
  })

  it('does not install an unsupported or a different release', async () => {
    vi.useFakeTimers()
    const getSnapshot = vi.fn().mockResolvedValueOnce(snapshot())
      .mockResolvedValue(snapshot({ downloadState: 'ReadyToInstall', canInstall: false, canDownload: false }))
    const { wrapper, client, store, toast } = render({ getSnapshot })
    await flushPromises()
    await wrapper.get('button').trigger('click'); await flushPromises()
    await vi.advanceTimersByTimeAsync(1000)
    expect(client.install).not.toHaveBeenCalled()
    expect(toast.message).toContain('does not support')
    store.complete(snapshot()); await flushPromises()
    await wrapper.get('button').trigger('click'); await flushPromises()
    store.complete({ ...ready(), latestVersion: '0.3.4-alpha' }); await flushPromises()
    expect(client.install).not.toHaveBeenCalled()
  })

  it('installs an already verified update without downloading again', async () => {
    const { wrapper, client } = render({ getSnapshot: vi.fn().mockResolvedValue(ready()) })
    await flushPromises()
    await wrapper.get('button').trigger('click'); await flushPromises()
    expect(client.download).not.toHaveBeenCalled()
    expect(client.install).toHaveBeenCalledTimes(1)
  })

  it('does not let an earlier progress read overwrite a completed download command', async () => {
    vi.useFakeTimers()
    let finishRead!: (snapshot: HostUpdateSnapshot) => void
    let finishDownload!: (snapshot: HostUpdateSnapshot) => void
    const getSnapshot = vi.fn().mockResolvedValueOnce(snapshot())
      .mockImplementation(() => new Promise<HostUpdateSnapshot>(resolve => { finishRead = resolve }))
    const download = vi.fn(() => new Promise<HostUpdateSnapshot>(resolve => { finishDownload = resolve }))
    const { wrapper, client, store } = render({ getSnapshot, download })
    await flushPromises()
    await wrapper.get('button').trigger('click')
    await vi.advanceTimersByTimeAsync(1000)
    finishDownload(ready()); await flushPromises()
    expect(client.install).toHaveBeenCalledTimes(1)
    finishRead(downloading()); await flushPromises()
    expect(store.snapshot?.downloadState).toBe('Installing')
  })

  it('stops polling on unmount and does not initiate installation afterwards', async () => {
    vi.useFakeTimers()
    const { wrapper, client } = render({
      getSnapshot: vi.fn().mockResolvedValueOnce(snapshot()).mockResolvedValue(ready()),
    })
    await flushPromises()
    await wrapper.get('button').trigger('click'); await flushPromises()
    wrapper.unmount()
    await vi.advanceTimersByTimeAsync(5000)
    expect(client.install).not.toHaveBeenCalled()
    expect(client.getSnapshot).toHaveBeenCalledTimes(1)
  })
})
