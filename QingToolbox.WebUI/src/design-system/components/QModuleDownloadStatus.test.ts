import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import type { ModuleRepositoryDownload } from '../../contracts/moduleRepository'
import QModuleDownloadStatus from './QModuleDownloadStatus.vue'

const mocks = vi.hoisted(() => ({ snapshot: vi.fn(), callbacks: new Map<string, () => void>(), cleanup: vi.fn() }))
vi.mock('../../bridge/clients/ModuleRepositoryClient', () => ({ moduleRepositoryClient: { snapshot: mocks.snapshot } }))
vi.mock('@tauri-apps/api/event', () => ({ listen: vi.fn(async (name: string, callback: () => void) => { mocks.callbacks.set(name, callback); return mocks.cleanup }) }))
const empty = (): ModuleRepositoryDownload => ({ jobId: 0, moduleId: '', name: '', status: '', bytesReceived: 0, expectedBytes: 0, savedPath: '', error: '' })
const active = (): ModuleRepositoryDownload => ({ ...empty(), jobId: 1, moduleId: 'qing.test', name: 'Test', status: 'Downloading', bytesReceived: 25, expectedBytes: 100 })
const wrappers: ReturnType<typeof mount>[] = []
afterEach(() => { wrappers.splice(0).forEach(w => w.unmount()); vi.useRealTimers(); vi.unstubAllGlobals(); mocks.snapshot.mockReset(); mocks.callbacks.clear(); mocks.cleanup.mockClear() })

describe('global native module download progress', () => {
  it('starts polling for a detail-page download, even if its event races the initial idle snapshot', async () => {
    vi.useFakeTimers(); vi.stubGlobal('__TAURI_INTERNALS__', {})
    let resolve!: (value: ModuleRepositoryDownload) => void
    mocks.snapshot.mockImplementationOnce(() => new Promise(r => { resolve = r })).mockResolvedValue(active())
    const pinia = createPinia(); setActivePinia(pinia)
    const wrapper = mount(QModuleDownloadStatus, { global: { plugins: [pinia] } }); wrappers.push(wrapper)
    await flushPromises()
    mocks.callbacks.get('qmod:module-repository-changed')!()
    resolve(empty()); await flushPromises(); await vi.advanceTimersByTimeAsync(0); await flushPromises()
    expect(wrapper.text()).toContain('25%')
    mocks.snapshot.mockResolvedValue({ ...active(), status: 'Installing', bytesReceived: 100 })
    await vi.advanceTimersByTimeAsync(500)
    expect(wrapper.text()).toContain('Installing')
    mocks.snapshot.mockResolvedValue({ ...active(), status: 'Completed', bytesReceived: 100 })
    await vi.advanceTimersByTimeAsync(500)
    expect(wrapper.find('[role="status"]').exists()).toBe(false)
    wrapper.unmount()
    expect(mocks.cleanup).toHaveBeenCalledTimes(1)
  })
})
