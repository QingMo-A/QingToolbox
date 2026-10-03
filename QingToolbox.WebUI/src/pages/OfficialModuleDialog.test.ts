import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import OfficialModuleDialog from './OfficialModuleDialog.vue'
import QModuleImportMenu from '../design-system/components/QModuleImportMenu.vue'
import QModuleDownloadStatus from '../design-system/components/QModuleDownloadStatus.vue'
import { useModuleRepositoryStore } from '../app/moduleRepositoryStore'
import { useToastStore } from '../app/toastStore'
import { isOfficialModules, isModuleRepositoryDownload, type ModuleRepositoryDownload } from '../contracts/moduleRepository'
import { ModuleRepositoryClient } from '../bridge/clients/ModuleRepositoryClient'

const invoke = vi.hoisted(() => vi.fn())
vi.mock('@tauri-apps/api/core', () => ({ invoke }))
const modules = [{ id: 'qing.launcher', name: { 'en-US': 'Qing Launcher', 'zh-CN': '启动台' }, description: { 'en-US': 'Application launcher', 'zh-CN': '启动程序' }, version: '0.3.0', apiVersion: 1, size: 1024, canDownload: true, unavailableReason: '' }]
const snapshot = (changes: Partial<ModuleRepositoryDownload> = {}): ModuleRepositoryDownload => ({ jobId: 1, moduleId: 'qing.launcher', name: 'Qing Launcher', status: 'Downloading', bytesReceived: 0, expectedBytes: 1024, savedPath: '', error: '', ...changes })
const wrappers: VueWrapper[] = []
function render(component: typeof OfficialModuleDialog | typeof QModuleImportMenu | typeof QModuleDownloadStatus, props: Record<string, unknown> = {}) {
  const pinia = createPinia(); setActivePinia(pinia)
  const wrapper = mount(component as any, { props, attachTo: document.body, global: { plugins: [pinia] } }); wrappers.push(wrapper)
  return { wrapper, store: useModuleRepositoryStore(pinia) }
}
beforeEach(() => { invoke.mockReset() })
afterEach(() => { wrappers.splice(0).forEach(w => w.unmount()); document.body.innerHTML = ''; vi.useRealTimers() })

describe('module import choices', () => {
  it('expands choices without importing, and selects local or repository explicitly', async () => {
    const { wrapper } = render(QModuleImportMenu)
    await wrapper.get('.module-import-button').trigger('click')
    expect(wrapper.get('[role="menu"]').text()).toContain('Import from local file')
    expect(wrapper.emitted('local')).toBeUndefined()
    await wrapper.get('.module-import-local').trigger('click')
    expect(wrapper.emitted('local')).toHaveLength(1)
    await wrapper.get('.module-import-button').trigger('click')
    await wrapper.get('.module-import-repository').trigger('click')
    expect(wrapper.emitted('repository')).toHaveLength(1)
    expect(invoke).not.toHaveBeenCalled()
  })
  it('supports keyboard selection, Escape, and outside dismissal', async () => {
    const { wrapper } = render(QModuleImportMenu)
    await wrapper.get('.module-import-button').trigger('keydown', { key: 'ArrowDown' })
    expect(document.activeElement).toBe(wrapper.get('.module-import-local').element)
    await wrapper.get('.module-import-local').trigger('keydown', { key: 'ArrowDown' })
    expect(document.activeElement).toBe(wrapper.get('.module-import-repository').element)
    await wrapper.get('.module-import-repository').trigger('keydown', { key: 'Escape' })
    expect(wrapper.find('[role="menu"]').exists()).toBe(false)
    await wrapper.get('.module-import-button').trigger('click')
    document.body.dispatchEvent(new Event('pointerdown', { bubbles: true })); await flushPromises()
    expect(wrapper.find('[role="menu"]').exists()).toBe(false)
  })
})

describe('official module dialog', () => {
  it('requires a selection and starts backend-owned download/install with only its id', async () => {
    invoke.mockImplementation(async command => command === 'get_official_modules' ? modules : snapshot())
    const { wrapper, store } = render(OfficialModuleDialog, { open: true })
    expect(document.querySelector<HTMLButtonElement>('.official-module-download')!.disabled).toBe(true)
    await flushPromises()
    const option = document.querySelector<HTMLButtonElement>('[role="option"]')!
    expect(option.textContent).toContain('Qing Launcher'); expect(option.textContent).toContain('0.3.0')
    option.click(); await flushPromises()
    const button = document.querySelector<HTMLButtonElement>('.official-module-download')!
    expect(button.textContent).toContain('Download and install')
    expect(button.disabled).toBe(false); button.click(); await flushPromises()
    expect(invoke).toHaveBeenLastCalledWith('download_official_module', { moduleId: 'qing.launcher' })
    expect(wrapper.emitted('close')).toHaveLength(1)
    expect(store.active).toBe(true)
    wrapper.unmount(); expect(store.snapshot?.status).toBe('Downloading')
    expect(invoke.mock.calls.some(([command]) => command === 'import_module')).toBe(false)
  })
  it('shows errors and retries without inventing modules', async () => {
    invoke.mockRejectedValueOnce({ code: 'NetworkUnavailable' }).mockResolvedValueOnce(modules)
    render(OfficialModuleDialog, { open: true }); await flushPromises()
    expect(document.querySelector('[role="alert"]')!.textContent).toContain('Cannot connect')
    document.querySelector<HTMLButtonElement>('[role="alert"] button')!.click(); await flushPromises()
    expect(document.querySelectorAll('[role="option"]')).toHaveLength(1)
    expect(document.querySelector<HTMLButtonElement>('.official-module-download')!.disabled).toBe(true)
  })
  it('rejects malformed metadata, prevents incompatible selection, and cancels cleanly', async () => {
    invoke.mockResolvedValue([{ ...modules[0], canDownload: false, unavailableReason: 'IncompatibleApi' }])
    const { wrapper } = render(OfficialModuleDialog, { open: true }); await flushPromises()
    document.querySelector<HTMLButtonElement>('[role="option"]')!.click(); await flushPromises()
    expect(document.querySelector<HTMLButtonElement>('.official-module-download')!.disabled).toBe(true)
    document.querySelectorAll<HTMLButtonElement>('.q-modal-actions button')[0].click(); await flushPromises()
    expect(wrapper.emitted('close')).toHaveLength(1)
    expect(isOfficialModules([{ ...modules[0], id: '../evil' }])).toBe(false)
    expect(isOfficialModules([modules[0], modules[0]])).toBe(false)
    invoke.mockResolvedValue({ modules }); await expect(new ModuleRepositoryClient().list()).rejects.toThrow('InvalidCatalog')
  })
  it('does not allow a closed stale fetch to overwrite a reopened list', async () => {
    let first!: (value: unknown) => void
    invoke.mockImplementationOnce(() => new Promise(resolve => { first = resolve })).mockResolvedValueOnce(modules)
    const { wrapper } = render(OfficialModuleDialog, { open: true })
    await wrapper.setProps({ open: false }); await wrapper.setProps({ open: true }); await flushPromises()
    first([]); await flushPromises()
    expect(document.querySelectorAll('[role="option"]')).toHaveLength(1)
  })
})

describe('global download progress', () => {
  it('shows download then installation, keeps polling off-page, and clears only when installed', async () => {
    vi.useFakeTimers()
    invoke.mockResolvedValue(snapshot({ bytesReceived: 512 }))
    const { wrapper, store } = render(QModuleDownloadStatus)
    await flushPromises()
    expect(wrapper.text()).toContain('Downloading “Qing Launcher” 50%')
    invoke.mockResolvedValue(snapshot({ status: 'Installing', bytesReceived: 1024, savedPath: 'C:/Downloads/test.qmod' }))
    await vi.advanceTimersByTimeAsync(500); await flushPromises()
    expect(wrapper.text()).toContain('Installing “Qing Launcher”…')
    expect(wrapper.text()).not.toContain('Downloading')
    expect(store.active).toBe(true)
    invoke.mockResolvedValue(snapshot({ status: 'Completed', bytesReceived: 1024, savedPath: 'C:/Downloads/test.qmod' }))
    await vi.advanceTimersByTimeAsync(500); await flushPromises()
    expect(store.active).toBe(false); expect(wrapper.find('.q-module-download-status').exists()).toBe(false)
    const calls = invoke.mock.calls.length
    await vi.advanceTimersByTimeAsync(1000); expect(invoke).toHaveBeenCalledTimes(calls)
  })
  it('reports installation failure separately and preserves the downloaded package path', async () => {
    vi.useFakeTimers()
    invoke.mockResolvedValue(snapshot({ status: 'Installing', bytesReceived: 1024, savedPath: 'C:/Downloads/test.qmod' }))
    const { wrapper, store } = render(QModuleDownloadStatus); await flushPromises()
    invoke.mockResolvedValue(snapshot({ status: 'InstallFailed', bytesReceived: 1024, savedPath: 'C:/Downloads/test.qmod', error: 'InstallFailed' }))
    await vi.advanceTimersByTimeAsync(500); await flushPromises()
    expect(store.active).toBe(false)
    expect(store.snapshot?.savedPath).toBe('C:/Downloads/test.qmod')
    expect(wrapper.find('.q-module-download-status').exists()).toBe(false)
    expect(useToastStore().message).toContain('could not be installed')
    expect(useToastStore().message).toContain('kept for local import')
  })
  it('ignores stale progress and caps unverified progress below 100%', () => {
    setActivePinia(createPinia()); const store = useModuleRepositoryStore()
    store.complete(snapshot({ jobId: 2, bytesReceived: 1024, status: 'Verifying' }))
    expect(store.percentage).toBe(99)
    store.complete(snapshot({ jobId: 1, bytesReceived: 1 })); expect(store.snapshot?.jobId).toBe(2)
    store.complete(snapshot({ jobId: 2, status: 'Installing', bytesReceived: 1024, savedPath: 'C:/Downloads/test.qmod' }))
    store.complete(snapshot({ jobId: 2, status: 'Verifying', bytesReceived: 1024 }))
    expect(store.snapshot?.status).toBe('Installing')
    store.complete(snapshot({ jobId: 2, status: 'Completed', bytesReceived: 1024 })); store.complete(snapshot({ jobId: 2 }))
    expect(store.snapshot?.status).toBe('Completed')
    expect(isModuleRepositoryDownload(snapshot({ bytesReceived: 1025 }))).toBe(false)
    expect(isModuleRepositoryDownload(snapshot({ status: 'Installing' }))).toBe(true)
    expect(isModuleRepositoryDownload(snapshot({ status: 'InstallFailed' }))).toBe(true)
  })
})
