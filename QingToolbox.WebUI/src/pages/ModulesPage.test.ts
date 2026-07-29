import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import ModulesPage from './ModulesPage.vue'
import { useAppStore } from '../app/store'
import { useModuleStore } from '../app/moduleStore'
import { useToastStore } from '../app/toastStore'
import type { ModuleSnapshotItem } from '../contracts/modules'
import { useSettingsStore } from '../app/settingsStore'
import type { EffectiveLanguageCode, LanguageCode, SettingsSnapshot } from '../contracts/settings'

const item = (overrides: Partial<ModuleSnapshotItem> = {}): ModuleSnapshotItem => ({
  id: 'qing.text', displayName: 'Text Tools', displayDescription: 'Formatting tools', version: '1.0.0', author: 'Qing', runtimeType: 'OutOfProcess', loadMode: 'Manual', runtimeState: 'NotLoaded', isValid: true, errorCount: 0, errors: [], permissions: [], minimumHostVersion: '0.2', isUserInstalled: true, canRemove: true, canLoad: true, canActivate: false, canOpen: false, canDeactivate: false, canUnload: false, isBusy: false, isExecutionBlocked: false, isStartupEnabled: false, startupAuthorizationState: 'NotEnabled', canChangeStartupAuthorization: true, isStartupAuthorizationBusy: false, updateStatus: 'NotChecked', targetVersion: null, releaseNotes: null, isFromStaleCache: false, canCheckForUpdate: true, isUpdateCheckBusy: false, canDownloadUpdate: false, downloadStatus: 'NotDownloaded', isDownloadActive: false, downloadBytesReceived: 0, downloadExpectedBytes: 0, canInstallVerifiedUpdate: false, ...overrides
})
const mounted: ReturnType<typeof mount>[] = []
afterEach(() => { mounted.splice(0).forEach(x => x.unmount()); document.body.innerHTML = ''; vi.restoreAllMocks() })

const settingsSnapshot = (code: LanguageCode, effectiveCode: EffectiveLanguageCode): SettingsSnapshot => ({
  generatedAt: new Date().toISOString(), language: { code, effectiveCode, displayName: code, options: [
    { code: 'system', displayName: 'System Default', nativeName: '跟随系统' }, { code: 'zh-CN', displayName: 'Simplified Chinese', nativeName: '简体中文' }, { code: 'en-US', displayName: 'English', nativeName: 'English' },
  ] }, showLogsInSidebar: true, mainWindowCloseBehavior: 'Ask', closeBehaviorMessage: '', launchAtLogin: false,
  canConfigureLaunchAtLogin: true, canRepairStartup: false, startupPresentationMode: 'FloatingBadge', startupBackend: 'None', startupStatus: 'Unavailable', startupMessage: '',
})

function page(module = item(), clientOverrides: Record<string, unknown> = {}, state: { confirmed?: boolean; status?: 'idle'|'loading'|'ready'|'error'; bridge?: string; language?: LanguageCode; effectiveLanguage?: EffectiveLanguageCode } = {}) {
  const pinia = createPinia(); setActivePinia(pinia)
  const app = useAppStore(); app.bridge = state.bridge ?? 'Connected'
  if (state.language || state.effectiveLanguage) useSettingsStore().complete(settingsSnapshot(state.language ?? state.effectiveLanguage ?? 'en-US', state.effectiveLanguage ?? 'en-US'))
  const store = useModuleStore()
  if (state.confirmed !== false) store.complete({ generatedAt: new Date().toISOString(), modules: [module] })
  if (state.status === 'loading') store.begin()
  else if (state.status === 'error') store.fail(new Error('Bridge.Internal: secret failure'))
  else if (state.status === 'idle') store.status = 'idle'
  const snapshot = () => ({ generatedAt: new Date().toISOString(), modules: [module] })
  const client = { getSnapshot: vi.fn(async () => snapshot()), importModule: vi.fn(async () => ({ disposition: 'Cancelled', importedModuleId: null, snapshot: snapshot() })), load: vi.fn(async () => snapshot()), activate: vi.fn(async () => snapshot()), open: vi.fn(async () => snapshot()), deactivate: vi.fn(async () => snapshot()), unload: vi.fn(async () => snapshot()), setStartupAuthorization: vi.fn(async () => snapshot()), openDirectory: vi.fn(async () => ({ disposition: 'Succeeded', snapshot: snapshot() })), remove: vi.fn(async () => ({ disposition: 'Succeeded', snapshot: { generatedAt: new Date().toISOString(), modules: [] } })), checkUpdate: vi.fn(async () => snapshot()), downloadUpdate: vi.fn(async () => snapshot()), installVerifiedUpdate: vi.fn(async () => ({ disposition: 'Installed', sourceVersion: module.version, targetVersion: module.targetVersion ?? module.version, snapshot: snapshot() })), ...clientOverrides }
  const wrapper = mount(ModulesPage, { attachTo: document.body, global: { plugins: [pinia], provide: { moduleClient: client } } }); mounted.push(wrapper)
  return { wrapper, client, app, store }
}

const cardLabels = (wrapper: ReturnType<typeof mount>) => wrapper.findAll('.wpf-module-card .module-actions .q-button').map(button => button.text())
const summaryValues = (wrapper: ReturnType<typeof mount>) => Object.fromEntries(
  wrapper.findAll('.wpf-module-summary article').map(article => [
    article.get('label').text(),
    Number(article.get('strong').text()),
  ]),
)

describe('ModulesPage lifecycle controls', () => {
  it('imports once through the native picker and selects the confirmed module', async () => {
    let resolve!: (value: unknown) => void
    const imported = item({ id: 'qing.imported', displayName: 'Imported Tool' })
    const importModule = vi.fn(() => new Promise(value => { resolve = value }))
    const load = vi.fn()
    const { wrapper, store } = page(item(), { importModule, load })
    store.searchQuery = 'hidden'; store.stateFilter = 'running'
    const button = wrapper.findAll('.wpf-page-header .q-button').find(item => item.text().includes('Import module'))!
    const refresh = wrapper.get('.wpf-page-header .module-refresh-button')
    expect(button.classes()).toContain('is-primary')
    expect(refresh.classes()).toContain('is-secondary')
    expect(button.attributes('aria-busy')).toBe('false')
    await button.trigger('click'); await button.trigger('click')
    expect(importModule).toHaveBeenCalledTimes(1)
    expect(button.text()).toContain('Importing…')
    expect(button.find('.module-operation-spinner').exists()).toBe(true)
    expect(button.attributes('aria-busy')).toBe('true')
    expect(button.attributes('disabled')).toBeDefined()
    expect(refresh.attributes('disabled')).toBeDefined()
    resolve({ disposition: 'Imported', importedModuleId: imported.id, snapshot: { generatedAt: new Date().toISOString(), modules: [imported] } })
    await flushPromises()
    expect(store.selectedModuleId).toBe(imported.id)
    expect(store.searchQuery).toBe(''); expect(store.stateFilter).toBe('all')
    expect(useToastStore().message).toBe('Imported Tool was imported.')
    expect(load).not.toHaveBeenCalled()
  })

  it('keeps page state unchanged when native import is cancelled', async () => {
    const { wrapper, store } = page(item())
    store.searchQuery = 'Text'; store.stateFilter = 'notLoaded'; store.selectedModuleId = 'qing.text'
    await wrapper.findAll('.wpf-page-header .q-button').find(item => item.text().includes('Import module'))!.trigger('click')
    await flushPromises()
    expect(store.searchQuery).toBe('Text'); expect(store.stateFilter).toBe('notLoaded'); expect(store.selectedModuleId).toBe('qing.text')
    expect(useToastStore().message).toBe('')
    const button = wrapper.get('.wpf-page-header .module-import-button')
    expect(button.attributes('aria-busy')).toBe('false')
    expect(button.attributes('disabled')).toBeUndefined()
  })

  it('offers localized import from the empty module state', async () => {
    const { wrapper, store, client } = page(item(), {}, { language: 'zh-CN', effectiveLanguage: 'zh-CN' })
    store.complete({ generatedAt: new Date().toISOString(), modules: [] }); await wrapper.vm.$nextTick()
    expect(wrapper.findAll('.wpf-page-header .q-button').some(button => button.text().includes('导入模块'))).toBe(true)
    const emptyImport = wrapper.get('.q-empty .module-import-button')
    expect(emptyImport.text()).toBe('导入模块')
    expect(emptyImport.classes()).toContain('is-primary')
    expect(emptyImport.find('.q-icon').exists()).toBe(true)
    await emptyImport.trigger('click'); await flushPromises()
    expect(client.importModule).toHaveBeenCalledTimes(1)
    expect(emptyImport.attributes('aria-busy')).toBe('false')
  })
  it('shows the unified six-part summary for a mixed snapshot', async () => {
    const { wrapper, store } = page()
    store.complete({
      generatedAt: new Date().toISOString(),
      modules: [
        item({ id: 'not-loaded', runtimeState: 'NotLoaded' }),
        item({ id: 'unloaded', runtimeState: 'Unloaded' }),
        item({ id: 'loaded', runtimeState: 'Loaded' }),
        item({ id: 'deactivated', runtimeState: 'Deactivated' }),
        item({ id: 'running', runtimeState: 'Running' }),
        item({ id: 'failed', runtimeState: 'Failed' }),
        item({ id: 'invalid', runtimeState: 'Loaded', isValid: false }),
        item({ id: 'errors', runtimeState: 'Loaded', errorCount: 1 }),
      ],
    })
    await wrapper.vm.$nextTick()

    expect(summaryValues(wrapper)).toEqual({
      Total: 8,
      Valid: 7,
      Issues: 3,
      'Not loaded': 2,
      Loaded: 4,
      Running: 1,
    })
    expect(wrapper.findAll('.wpf-module-summary label').map(label => label.text())).not.toContain('Failed')
  })

  it.each([
    [item(), ['Load']],
    [item({ runtimeState: 'Unloaded' }), ['Load']],
    [item({ runtimeState: 'Loaded', canLoad: false, canActivate: true, canOpen: true, canUnload: true }), ['Open', 'Activate', 'Unload']],
    [item({ runtimeState: 'Running', canLoad: false, canOpen: true, canDeactivate: true, canUnload: true }), ['Open', 'Deactivate', 'Unload']],
    [item({ runtimeState: 'Deactivated', canLoad: false, canActivate: true, canOpen: true, canUnload: true }), ['Open', 'Activate', 'Unload']],
    [item({ runtimeState: 'Failed', isValid: false, canLoad: false, errorCount: 1, errors: ['failed'] }), []],
    [item({ runtimeState: 'FutureFailure', isValid: false, canLoad: false, canUnload: true, errorCount: 1 }), ['Unload']],
  ])('shows every host-confirmed card action in stable order', (module, expected) => {
    const { wrapper } = page(module as ModuleSnapshotItem)
    expect(cardLabels(wrapper)).toEqual(expected)
  })

  it('keeps only lifecycle actions in the card action region', () => {
    const running = item({ runtimeState: 'Running', canLoad: false, canOpen: true, canDeactivate: true, canUnload: true })
    const { wrapper } = page(running)
    const actionBar = wrapper.get('.module-card-actions')
    expect(actionBar.findAll(':scope > div')).toHaveLength(1)
    expect(actionBar.get('.module-card-lifecycle-actions').findAll('.q-button').map(button => button.text()))
      .toEqual(['Open', 'Deactivate', 'Unload'])
    expect(actionBar.find('.module-details-button').exists()).toBe(false)
    expect(cardLabels(wrapper)).toEqual(['Open', 'Deactivate', 'Unload'])
  })

  it('opens a read-only lifecycle detail panel from the card surface', async () => {
    const { wrapper } = page(item({ runtimeState: 'Running', canLoad: false, canOpen: true, canDeactivate: true, canUnload: true }))
    expect(cardLabels(wrapper)).toEqual(['Open', 'Deactivate', 'Unload'])
    await wrapper.get('.wpf-module-card').trigger('click')
    const details = wrapper.get('.wpf-module-details')
    expect(details.text()).not.toContain('Module actions')
    expect(details.find('.module-detail-actions').exists()).toBe(false)
    expect(details.text()).toContain('Module information')
  })

  it('hides execution-sensitive actions when the host reports execution blocked', async () => {
    const blocked = item({ runtimeState: 'Running', canLoad: false, canActivate: true, canOpen: true, canDeactivate: true, canUnload: true, isExecutionBlocked: true })
    const { wrapper } = page(blocked)
    expect(cardLabels(wrapper)).toEqual(['Activate'])
    await wrapper.get('.wpf-module-card').trigger('click')
    expect(wrapper.find('.module-detail-actions').exists()).toBe(false)
  })

  it('keeps card details available and locks every lifecycle action while one operation is pending', async () => {
    let resolve!: (value: unknown) => void
    const pending = new Promise(value => { resolve = value })
    const running = item({ runtimeState: 'Running', canLoad: false, canOpen: true, canDeactivate: true, canUnload: true })
    const deactivate = vi.fn(() => pending)
    const unload = vi.fn()
    const { wrapper } = page(running, { deactivate, unload })
    await wrapper.findAll('.module-card-actions .q-button').find(button => button.text() === 'Deactivate')!.trigger('click')
    const actions = wrapper.findAll('.module-card-actions .q-button')
    expect(actions.map(button => button.text())).toEqual(['Open', 'Deactivating…', 'Unload'])
    expect(actions.every(button => button.attributes('disabled') !== undefined)).toBe(true)
    await wrapper.get('.wpf-module-card').trigger('click')
    expect(wrapper.find('.wpf-module-details').exists()).toBe(true)
    await actions.find(button => button.text() === 'Unload')!.trigger('click')
    expect(deactivate).toHaveBeenCalledTimes(1)
    expect(unload).not.toHaveBeenCalled()
    expect(useModuleStore().modules[0].runtimeState).toBe('Running')
    resolve({ generatedAt: new Date().toISOString(), modules: [item({ runtimeState: 'Deactivated', canLoad: false, canActivate: true, canOpen: true, canUnload: true })] })
    await flushPromises()
    expect(cardLabels(wrapper)).toEqual(['Open', 'Activate', 'Unload'])
  })

  it('does not optimistically change runtime state while card Unload is pending', async () => {
    vi.useFakeTimers()
    try {
      let resolve!: (value: unknown) => void
      const pending = new Promise(value => { resolve = value })
      const loaded = item({ runtimeState: 'Loaded', canLoad: false, canActivate: true, canOpen: true, canUnload: true })
      const unload = vi.fn(() => pending)
      const { wrapper } = page(loaded, { unload })
      await wrapper.findAll('.module-card-actions .q-button').find(button => button.text() === 'Unload')!.trigger('click')
      expect(unload).toHaveBeenCalledWith('qing.text')
      expect(useModuleStore().modules[0].runtimeState).toBe('Loaded')
      expect(cardLabels(wrapper)).toEqual(['Open', 'Activate', 'Unloading…'])
      resolve({ generatedAt: new Date().toISOString(), modules: [item({ runtimeState: 'Unloaded', canLoad: true })] })
      await flushPromises()
      expect(useModuleStore().modules[0].runtimeState).toBe('Unloaded')
      expect(wrapper.get('.module-lifecycle-success').classes()).toContain('is-unload')
      expect(cardLabels(wrapper)).toEqual([])
      vi.advanceTimersByTime(1400)
      await wrapper.vm.$nextTick()
      expect(cardLabels(wrapper)).toEqual(['Load'])
      expect(wrapper.get('.module-card-lifecycle-actions').classes()).toContain('is-revealing')
    } finally {
      vi.useRealTimers()
    }
  })

  it('groups runtime and startup badges without duplicating startup controls on cards', async () => {
    const { wrapper } = page(item({ runtimeState: 'Running', canLoad: false, canOpen: true, startupAuthorizationState: 'Enabled', isStartupEnabled: true }))
    expect(wrapper.get('.module-card-badges').text()).toContain('Running')
    expect(wrapper.get('.module-card-badges').text()).toContain('Starts on launch')
    expect(wrapper.find('.q-switch').exists()).toBe(false)
    await wrapper.get('.wpf-module-card').trigger('click')
    expect(wrapper.get('.q-switch').attributes('role')).toBe('switch')
  })

  it('uses primary styling for forward actions and secondary styling for deactivate and unload', () => {
    const { wrapper } = page(item({ runtimeState: 'Running', canLoad: false, canOpen: true, canDeactivate: true, canUnload: true }))
    const actions = wrapper.findAll('.module-card-actions .q-button')
    expect(actions.find(button => button.text() === 'Open')!.classes()).toContain('is-primary')
    expect(actions.find(button => button.text() === 'Deactivate')!.classes()).toContain('is-secondary')
    expect(actions.find(button => button.text() === 'Unload')!.classes()).toContain('is-secondary')
  })

  it('shows card deactivate and unload labels in Chinese', () => {
    const running = item({ runtimeState: 'Running', canLoad: false, canOpen: true, canDeactivate: true, canUnload: true })
    const { wrapper } = page(running, {}, { language: 'zh-CN', effectiveLanguage: 'zh-CN' })
    expect(cardLabels(wrapper)).toEqual(['打开', '停用', '卸载'])
  })

  it('loads from the card without optimistic runtime changes', async () => {
    let resolve!: (value: unknown) => void
    const pending = new Promise(value => { resolve = value })
    const { wrapper, client } = page(item(), { load: vi.fn(() => pending) })
    await wrapper.findAll('.module-card-actions .q-button').find(button => button.text() === 'Load')!.trigger('click')
    expect(client.load).toHaveBeenCalledTimes(1)
    expect(useModuleStore().modules[0].runtimeState).toBe('NotLoaded')
    expect(summaryValues(wrapper)).toMatchObject({ 'Not loaded': 1, Loaded: 0, Running: 0 })
    expect(wrapper.text()).toContain('Loading…')
    resolve({ generatedAt: new Date().toISOString(), modules: [item({ runtimeState: 'Loaded', canLoad: false, canActivate: true })] })
    await flushPromises()
    expect(useModuleStore().modules[0].runtimeState).toBe('Loaded')
    expect(summaryValues(wrapper)).toMatchObject({ 'Not loaded': 0, Loaded: 1, Running: 0 })
  })

  it('shows a fixed load-success animation only after the confirmed snapshot, then reveals the next actions', async () => {
    vi.useFakeTimers()
    try {
      let resolve!: (value: unknown) => void
      const pending = new Promise(value => { resolve = value })
      const loaded = item({ runtimeState: 'Loaded', canLoad: false, canActivate: true, canOpen: true, canUnload: true })
      const { wrapper, store } = page(item(), { load: vi.fn(() => pending) })

      await wrapper.get('.module-card-actions .q-button').trigger('click')
      expect(wrapper.get('.module-operation-spinner').classes()).toContain('module-operation-spinner')
      expect(wrapper.get('.module-card-actions').text()).toContain('Loading…')
      expect(wrapper.find('.module-lifecycle-success').exists()).toBe(false)

      resolve({ generatedAt: new Date().toISOString(), modules: [loaded] })
      await flushPromises()

      expect(store.modules[0].runtimeState).toBe('Loaded')
      const success = wrapper.get('.module-lifecycle-success')
      expect(success.classes()).not.toContain('is-unload')
      expect(success.find('circle').attributes('pathLength')).toBe('100')
      expect(success.find('path').attributes('pathLength')).toBe('100')
      expect(wrapper.findAll('.module-card-actions .q-button')).toHaveLength(0)

      vi.advanceTimersByTime(1400)
      await wrapper.vm.$nextTick()
      expect(wrapper.find('.module-lifecycle-success').exists()).toBe(false)
      expect(cardLabels(wrapper)).toEqual(['Open', 'Activate', 'Unload'])
      expect(wrapper.get('.module-card-lifecycle-actions').classes()).toContain('is-revealing')
    } finally {
      vi.useRealTimers()
    }
  })

  it('shows the localized loading spinner in Chinese', async () => {
    const pending = new Promise(() => {})
    const { wrapper } = page(item(), { load: vi.fn(() => pending) }, { language: 'zh-CN', effectiveLanguage: 'zh-CN' })
    await wrapper.get('.module-card-actions .q-button').trigger('click')
    expect(wrapper.get('.module-operation-spinner').classes()).toContain('module-operation-spinner')
    expect(wrapper.get('.module-card-actions').text()).toContain('正在加载…')
  })

  it.each([
    ['activate', item({ runtimeState: 'Loaded', canLoad: false, canActivate: true }), item({ runtimeState: 'Running', canLoad: false, canActivate: false, canOpen: true }), 'Activate', { 'Not loaded': 0, Loaded: 0, Running: 1 }],
    ['deactivate', item({ runtimeState: 'Running', canLoad: false, canDeactivate: true }), item({ runtimeState: 'Deactivated', canLoad: false, canActivate: true, canUnload: true }), 'Deactivate', { 'Not loaded': 0, Loaded: 1, Running: 0 }],
    ['unload', item({ runtimeState: 'Deactivated', canLoad: false, canUnload: true }), item({ runtimeState: 'Unloaded', canLoad: true }), 'Unload', { 'Not loaded': 1, Loaded: 0, Running: 0 }],
  ] as const)('uses the complete host snapshot after %s', async (operation, before, after, label, expected) => {
    const result = { generatedAt: new Date().toISOString(), modules: [after] }
    const { wrapper } = page(before, { [operation]: vi.fn().mockResolvedValue(result) })
    await wrapper.findAll('.module-card-actions .q-button').find(button => button.text() === label)!.trigger('click')
    await flushPromises()
    expect(summaryValues(wrapper)).toMatchObject(expected)
  })

  it('opens details by mouse and keyboard and closes them with Escape', async () => {
    const { wrapper } = page()
    const card = wrapper.get('.wpf-module-card')
    expect(card.attributes('tabindex')).toBe('0')
    expect(card.attributes('aria-label')).toContain('Text Tools')
    await card.trigger('click')
    expect(useModuleStore().selectedModuleId).toBe('qing.text')
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }))
    await wrapper.vm.$nextTick()
    expect(useModuleStore().selectedModuleId).toBeNull()
    await card.trigger('keydown', { key: 'Enter' })
    expect(useModuleStore().selectedModuleId).toBe('qing.text')
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }))
    await wrapper.vm.$nextTick()
    expect(useModuleStore().selectedModuleId).toBeNull()
    await card.trigger('keydown', { key: ' ' })
    expect(useModuleStore().selectedModuleId).toBe('qing.text')
  })

  it('does not open details when a lifecycle action is clicked', async () => {
    const load = vi.fn().mockResolvedValue({ generatedAt: new Date().toISOString(), modules: [item()] })
    const { wrapper } = page(item(), { load })
    await wrapper.findAll('.module-card-actions .q-button').find(button => button.text() === 'Load')!.trigger('click')
    await flushPromises()
    expect(load).toHaveBeenCalledWith('qing.text')
    expect(useModuleStore().selectedModuleId).toBeNull()
  })

  it('keeps startup authorization in details and waits for the host snapshot', async () => {
    let resolve!: (value: unknown) => void
    const pending = new Promise(value => { resolve = value })
    const setStartupAuthorization = vi.fn(() => pending)
    const { wrapper } = page(item(), { setStartupAuthorization })
    await wrapper.get('.wpf-module-card').trigger('click')
    await wrapper.get('.q-switch').trigger('click')
    expect(setStartupAuthorization).toHaveBeenCalledWith('qing.text', true)
    expect(useModuleStore().modules[0].isStartupEnabled).toBe(false)
    expect(wrapper.get('.q-switch').attributes('aria-busy')).toBe('true')
    expect(wrapper.get('.q-switch em').text()).toBe('Off')
    resolve({ generatedAt: new Date().toISOString(), modules: [item({ isStartupEnabled: true, startupAuthorizationState: 'Enabled' })] })
    await flushPromises()
    expect(useToastStore().message).toBe('Text Tools will start with QingToolbox.')
  })

  it('preserves the host snapshot and resyncs once after an operation failure', async () => {
    const before = item()
    const getSnapshot = vi.fn().mockRejectedValue(new Error('resync failed'))
    const { wrapper, store } = page(before, { load: vi.fn().mockRejectedValue(new Error('ModuleBusy: busy')), getSnapshot })
    await wrapper.findAll('.module-card-actions .q-button').find(button => button.text() === 'Load')!.trigger('click')
    await flushPromises()
    expect(useModuleStore().modules[0]).toEqual(before)
    expect(getSnapshot).toHaveBeenCalledTimes(1)
    expect(store.status).toBe('error')
    expect(store.lastUpdatedAt).not.toBeNull()
    expect(store.error).toBe('The host could not confirm the current module state.')
    expect(wrapper.get('[role="status"]').text()).toBe('The host could not refresh modules. Showing the last confirmed snapshot.')
    expect(wrapper.findAll('.module-card-actions .q-button').find(button => button.text() === 'Load')!.attributes('disabled')).toBeDefined()
    expect(wrapper.find('.module-lifecycle-success').exists()).toBe(false)
    await wrapper.get('.wpf-module-card').trigger('click')
    expect(wrapper.find('.wpf-module-details').exists()).toBe(true)
    expect(useToastStore().message).toBe('Text Tools could not be loaded.')
    expect(useToastStore().message).not.toContain('ModuleBusy')
  })

  it('shows skeletons before the first snapshot and a safe first-load error', async () => {
    const loading = page(item(), {}, { confirmed: false, status: 'loading' })
    expect(loading.wrapper.findAll('.q-skeleton')).toHaveLength(3)
    loading.wrapper.unmount()

    const failed = page(item(), {}, { confirmed: false, status: 'error' })
    expect(failed.wrapper.text()).toContain('Modules are unavailable')
    expect(failed.wrapper.text()).toContain('The host could not provide the current module snapshot.')
    expect(failed.wrapper.text()).not.toContain('Bridge.Internal')
    expect(failed.wrapper.get('.q-empty .q-button').text()).toBe('Try again')
  })

  it.each([
    ['loading', 'Refreshing modules. Showing the last confirmed snapshot until the host responds.'],
    ['error', 'The host could not refresh modules. Showing the last confirmed snapshot.'],
  ] as const)('keeps a confirmed snapshot visible during %s', async (status, message) => {
    const { wrapper, store } = page(item(), {}, { status })
    expect(wrapper.text()).toContain('Text Tools')
    expect(wrapper.get('[role="status"]').text()).toBe(message)
    expect(wrapper.text()).not.toContain('Bridge.Internal')
    expect(store.lastUpdatedAt).not.toBeNull()
    const load = wrapper.findAll('.module-card-actions .q-button').find(button => button.text() === 'Load')!
    expect(load.attributes('disabled')).toBeDefined()
    await wrapper.get('.wpf-module-card').trigger('click')
    expect(wrapper.text()).toContain('Module information')
    expect(wrapper.get('.q-switch').attributes('disabled')).toBeDefined()
  })

  it('disables all host actions but keeps details available on a stale running module', async () => {
    const running = item({ runtimeState: 'Running', canLoad: false, canOpen: true, canDeactivate: true, canUnload: true })
    const { wrapper } = page(running, {}, { status: 'error' })
    expect(wrapper.findAll('.module-card-actions .q-button').find(button => button.text() === 'Open')!.attributes('disabled')).toBeDefined()
    await wrapper.get('.wpf-module-card').trigger('click')
    expect(wrapper.find('.module-detail-actions').exists()).toBe(false)
    expect(wrapper.find('.wpf-module-details').exists()).toBe(true)
  })

  it('keeps the snapshot visible while disconnected and prioritizes the disconnect notice', async () => {
    const { wrapper, app, store } = page()
    store.fail(new Error('hidden host error'))
    app.bridge = 'Disconnected'
    await wrapper.vm.$nextTick()
    expect(wrapper.text()).toContain('Text Tools')
    expect(wrapper.get('[role="status"]').text()).toBe('The host is disconnected. Showing the last confirmed module snapshot.')
    expect(wrapper.text()).not.toContain('hidden host error')
    expect(wrapper.get('.wpf-page-header .q-button').attributes('disabled')).toBeDefined()
  })

  it('keeps ready connected lifecycle operations available', async () => {
    const { wrapper } = page()
    expect(wrapper.findAll('.module-card-actions .q-button').find(button => button.text() === 'Load')!.attributes('disabled')).toBeUndefined()
    expect(wrapper.get('.wpf-module-card').attributes('tabindex')).toBe('0')
  })

  it('keeps search, filtering, and details browsing available with a stale snapshot', async () => {
    const { wrapper, store } = page(item(), {}, { status: 'error' })
    await wrapper.get('input[type="search"]').setValue('missing')
    expect(wrapper.text()).toContain('No modules found')
    await wrapper.get('input[type="search"]').setValue('Text')
    await wrapper.findAll('.filters button').find(button => button.text() === 'Not loaded')!.trigger('click')
    expect(store.searchQuery).toBe('Text')
    expect(store.stateFilter).toBe('notLoaded')
    expect(wrapper.text()).toContain('Text Tools')
    await wrapper.get('.wpf-module-card').trigger('click')
    expect(wrapper.text()).toContain('Module information')
  })

  it.each([
    ['load', item(), 'Load', 'Text Tools could not be loaded.'],
    ['activate', item({ runtimeState: 'Loaded', canLoad: false, canActivate: true }), 'Activate', 'Text Tools could not be activated.'],
    ['open', item({ runtimeState: 'Loaded', canLoad: false, canOpen: true }), 'Open', 'The Text Tools window could not be opened.'],
    ['deactivate', item({ runtimeState: 'Running', canLoad: false, canDeactivate: true }), 'Deactivate', 'Text Tools could not be deactivated.'],
    ['unload', item({ runtimeState: 'Running', canLoad: false, canUnload: true }), 'Unload', 'Text Tools could not be unloaded.'],
  ] as const)('uses a safe %s failure message and resyncs once', async (operation, module, label, expected) => {
    const getSnapshot = vi.fn().mockRejectedValue(new Error('resync secret'))
    const { wrapper, client, store } = page(module, { [operation]: vi.fn().mockRejectedValue(new Error('Bridge.SecretCode: internal path')), getSnapshot })
    await wrapper.findAll('.module-card-actions .q-button').find(button => button.text() === label)!.trigger('click')
    await flushPromises()
    expect(useToastStore().message).toBe(expected)
    expect(useToastStore().message).not.toContain('SecretCode')
    expect(getSnapshot).toHaveBeenCalledTimes(1)
    expect(store.modules[0]).toEqual(module)
    expect(store.status).toBe('error')
    expect(store.error).toBe('The host could not confirm the current module state.')
    expect(wrapper.text()).not.toContain('resync secret')
    expect(client[operation as keyof typeof client]).toHaveBeenCalledTimes(1)
  })

  it('uses a safe startup authorization failure and preserves the snapshot', async () => {
    const module = item()
    const getSnapshot = vi.fn().mockRejectedValue(new Error('resync secret'))
    const { wrapper, store } = page(module, { setStartupAuthorization: vi.fn().mockRejectedValue(new Error('FingerprintMismatch: path')), getSnapshot })
    await wrapper.get('.wpf-module-card').trigger('click')
    await wrapper.get('.q-switch').trigger('click')
    await flushPromises()
    expect(useToastStore().message).toBe('Startup authorization for Text Tools could not be updated.')
    expect(useToastStore().message).not.toContain('FingerprintMismatch')
    expect(getSnapshot).toHaveBeenCalledTimes(1)
    expect(store.modules[0]).toEqual(module)
    expect(store.status).toBe('error')
    expect(store.modules[0].isStartupEnabled).toBe(false)
    expect(wrapper.get('.q-switch').attributes('disabled')).toBeDefined()
  })

  it('uses a successful resync snapshot without replacing the operation error toast', async () => {
    const confirmed = item({ runtimeState: 'Loaded', canLoad: false, canActivate: true })
    const generatedAt = '2026-07-27T12:00:00.000Z'
    const getSnapshot = vi.fn().mockResolvedValue({ generatedAt, modules: [confirmed] })
    const { wrapper, store } = page(item(), { load: vi.fn().mockRejectedValue(new Error('operation failed')), getSnapshot })
    await wrapper.findAll('.module-card-actions .q-button').find(button => button.text() === 'Load')!.trigger('click')
    await flushPromises()
    expect(store.modules).toEqual([confirmed])
    expect(store.status).toBe('ready')
    expect(store.lastUpdatedAt).toBe(generatedAt)
    expect(wrapper.get('[role="status"]').text()).toBe('Found 1 module.')
    expect(useToastStore().message).toBe('Text Tools could not be loaded.')
    expect(getSnapshot).toHaveBeenCalledTimes(1)
  })

  it('prioritizes a bridge disconnect after failed operation resynchronization', async () => {
    const getSnapshot = vi.fn().mockRejectedValue(new Error('private resync failure'))
    const { wrapper, app } = page(item(), { load: vi.fn().mockRejectedValue(new Error('private operation failure')), getSnapshot })
    await wrapper.findAll('.module-card-actions .q-button').find(button => button.text() === 'Load')!.trigger('click')
    await flushPromises()
    app.bridge = 'Disconnected'
    await wrapper.vm.$nextTick()
    expect(wrapper.get('[role="status"]').text()).toBe('The host is disconnected. Showing the last confirmed module snapshot.')
    expect(wrapper.text()).not.toContain('private resync failure')
  })

  it('allows one explicit refresh to recover after failed operation resynchronization', async () => {
    const recovered = item({ runtimeState: 'Loaded', canLoad: false, canActivate: true })
    const getSnapshot = vi.fn()
      .mockRejectedValueOnce(new Error('confirmation failed'))
      .mockResolvedValueOnce({ generatedAt: '2026-07-27T12:30:00.000Z', modules: [recovered] })
    const { wrapper, store } = page(item(), { load: vi.fn().mockRejectedValue(new Error('operation failed')), getSnapshot })
    await wrapper.findAll('.module-card-actions .q-button').find(button => button.text() === 'Load')!.trigger('click')
    await flushPromises()
    expect(store.status).toBe('error')
    await wrapper.findAll('.wpf-page-header .q-button').find(button => button.text().includes('Refresh'))!.trigger('click')
    await flushPromises()
    expect(getSnapshot).toHaveBeenCalledTimes(2)
    expect(store.status).toBe('ready')
    expect(store.modules).toEqual([recovered])
  })

  it('keeps the old snapshot and safe page state after a refresh failure', async () => {
    const getSnapshot = vi.fn().mockRejectedValue(new Error('Bridge.Timeout: endpoint'))
    const { wrapper, store } = page(item(), { getSnapshot })
    await wrapper.findAll('.wpf-page-header .q-button').find(button => button.text().includes('Refresh'))!.trigger('click')
    await flushPromises()
    expect(store.status).toBe('error')
    expect(wrapper.text()).toContain('Text Tools')
    expect(wrapper.text()).not.toContain('Bridge.Timeout')
    expect(useToastStore().message).toBe('The host could not refresh modules.')
  })

  it.each([
    ['system', 'zh-CN'],
    ['zh-CN', 'zh-CN'],
  ] as const)('localizes the complete module workspace for %s with effective %s', async (language, effectiveLanguage) => {
    const module = item({ errorCount: 1, permissions: ['network'], errors: ['Host supplied issue'], startupAuthorizationState: 'Enabled', isStartupEnabled: true })
    const { wrapper } = page(module, {}, { language, effectiveLanguage })
    expect(wrapper.text()).toContain('模块'); expect(wrapper.text()).toContain('发现、浏览和查看工具箱模块。')
    expect(wrapper.text()).toContain('总计'); expect(wrapper.text()).toContain('有效'); expect(wrapper.text()).toContain('未加载')
    expect(wrapper.get('input').attributes('placeholder')).toBe('搜索模块…')
    expect(wrapper.findAll('.filters button').map(button => button.text())).toEqual(['全部','运行中','未加载','有问题','无效'])
    expect(wrapper.text()).toContain('随工具箱启动'); expect(wrapper.text()).toContain('1 个问题')
    await wrapper.get('.wpf-module-card').trigger('click')
    expect(wrapper.text()).not.toContain('模块操作'); expect(wrapper.text()).toContain('启动'); expect(wrapper.text()).toContain('模块信息')
    expect(wrapper.text()).not.toContain('未声明')
    for (const hostValue of ['Text Tools','Formatting tools','Qing','qing.text','network','Host supplied issue','OutOfProcess','Manual']) expect(wrapper.text()).toContain(hostValue)
  })

  it('updates the mounted page reactively without clearing module workspace state', async () => {
    const { wrapper, store } = page(item())
    store.searchQuery = 'Text'; store.stateFilter = 'notLoaded'; store.selectedModuleId = 'qing.text'
    useSettingsStore().complete(settingsSnapshot('system','zh-CN')); await wrapper.vm.$nextTick()
    expect(wrapper.text()).not.toContain('正在刷新模块状态')
    expect(wrapper.text()).toContain('模块信息'); expect(store.searchQuery).toBe('Text'); expect(store.stateFilter).toBe('notLoaded'); expect(store.selectedModuleId).toBe('qing.text')
    expect(store.modules).toHaveLength(1)
  })

  it('translates every startup authorization explanation', async () => {
    const { wrapper, store } = page(item(), {}, { language: 'zh-CN', effectiveLanguage: 'zh-CN' })
    store.selectedModuleId = 'qing.text'
    const cases = [
      ['NotEnabled','未获自动启动授权'], ['Enabled','已获随 QingToolbox 启动的授权'],
      ['ChangedNeedsConfirmation','模块内容已变化'], ['Unavailable','无法验证模块内容'], ['Missing','已无法匹配可用模块'],
    ] as const
    for (const [state, expected] of cases) {
      store.modules[0] = { ...store.modules[0], startupAuthorizationState: state }; await wrapper.vm.$nextTick()
      expect(wrapper.get('.module-startup-status').text()).toContain(expected)
    }
  })

  it('uses localized safe operation and startup Toasts with the public module name', async () => {
    const failure = vi.fn().mockRejectedValue(new Error('SecretCode: private path'))
    const { wrapper } = page(item(), { load: failure, getSnapshot: vi.fn().mockRejectedValue(new Error('private')) }, { language: 'zh-CN', effectiveLanguage: 'zh-CN' })
    await wrapper.findAll('.module-card-actions .q-button').find(button => button.text() === '加载')!.trigger('click'); await flushPromises()
    expect(useToastStore().message).toBe('无法加载 Text Tools。'); expect(useToastStore().message).not.toContain('SecretCode')
    const startupFailure = vi.fn().mockRejectedValue(new Error('private'))
    const second = page(item(), { setStartupAuthorization: startupFailure, getSnapshot: vi.fn().mockRejectedValue(new Error('private')) }, { language: 'zh-CN', effectiveLanguage: 'zh-CN' })
    await second.wrapper.get('.wpf-module-card').trigger('click'); await second.wrapper.get('.q-switch').trigger('click'); await flushPromises()
    expect(useToastStore().message).toBe('无法更新 Text Tools 的启动授权。')
  })

  it('keeps unknown runtime states host-authored while translating known and invalid states', async () => {
    const { wrapper, store } = page(item({ runtimeState: 'FutureState', canLoad: false }), {}, { language: 'zh-CN', effectiveLanguage: 'zh-CN' })
    expect(wrapper.text()).toContain('FutureState')
    store.complete({ generatedAt: new Date().toISOString(), modules: [item({ runtimeState: 'Failed', isValid: false, canLoad: false })] }); await wrapper.vm.$nextTick()
    expect(wrapper.get('.module-card-badges').text()).toContain('无效')
  })

  it('shows management in details, opens folders once, and hides removal for built-in modules', async () => {
    let resolve!: (value: unknown) => void
    const openDirectory = vi.fn(() => new Promise(resolveResult => { resolve = resolveResult }))
    const { wrapper } = page(item({ isUserInstalled: false, canRemove: false }), { openDirectory })
    await wrapper.get('.wpf-module-card').trigger('click')
    expect(wrapper.get('.module-management').text()).toContain('Module management')
    expect(wrapper.find('.module-remove-entry').exists()).toBe(false)
    const open = wrapper.get('.module-management-actions .q-button')
    await open.trigger('click'); await open.trigger('click')
    expect(openDirectory).toHaveBeenCalledTimes(1)
    expect(openDirectory).toHaveBeenCalledWith('qing.text')
    expect(open.attributes('aria-busy')).toBe('true')
    expect(open.text()).toContain('Opening…')
    expect(open.get('.module-operation-spinner').classes()).toContain('module-operation-spinner')
    resolve({ disposition: 'Succeeded', snapshot: { generatedAt: new Date().toISOString(), modules: [item({ isUserInstalled: false, canRemove: false })] } })
    await flushPromises()
    expect(useToastStore().message).toBe('Module folder opened.')
  })

  it('requires inline confirmation and applies the authoritative removal snapshot', async () => {
    let resolve!: (value: unknown) => void
    const remove = vi.fn(() => new Promise(resolveResult => { resolve = resolveResult }))
    const { wrapper, store } = page(item(), { remove })
    await wrapper.get('.wpf-module-card').trigger('click')
    const removeEntry = wrapper.get('.module-remove-entry')
    expect(removeEntry.classes()).toContain('is-ghost')
    expect(removeEntry.attributes('aria-expanded')).toBe('false')
    expect(removeEntry.attributes('aria-controls')).toBe('module-remove-confirmation')
    await removeEntry.trigger('click')
    expect(remove).not.toHaveBeenCalled()
    expect(removeEntry.attributes('aria-expanded')).toBe('true')
    const confirmation = wrapper.get('.module-remove-confirmation')
    expect(confirmation.attributes('id')).toBe('module-remove-confirmation')
    expect(confirmation.attributes('role')).toBe('group')
    expect(confirmation.attributes('aria-labelledby')).toBe('module-remove-confirmation-title')
    expect(confirmation.text()).toContain('Remove Text Tools?')
    const confirm = wrapper.get('.module-remove-confirm')
    expect(confirm.classes()).toContain('module-remove-confirm')
    await confirm.trigger('click')
    expect(remove).toHaveBeenCalledTimes(1)
    expect(remove).toHaveBeenCalledWith('qing.text')
    expect(wrapper.get('.module-remove-confirm').text()).toContain('Removing…')
    expect(wrapper.get('.module-remove-confirm').attributes('aria-busy')).toBe('true')
    expect(wrapper.get('.module-remove-confirm .module-operation-spinner').classes()).toContain('module-operation-spinner')
    expect(wrapper.get('.q-switch').attributes('disabled')).toBeDefined()
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }))
    await wrapper.vm.$nextTick()
    expect(wrapper.find('.module-remove-confirmation').exists()).toBe(true)
    expect(store.selectedModuleId).toBe('qing.text')
    resolve({ disposition: 'Succeeded', snapshot: { generatedAt: new Date().toISOString(), modules: [] } })
    await flushPromises()
    expect(store.modules).toEqual([])
    expect(store.selectedModuleId).toBeNull()
    expect(useToastStore().message).toBe('Module removed.')
  })

  it('cancels confirmation, closes it on selection changes, and respects host canRemove', async () => {
    const { wrapper, store, client } = page(item({ canRemove: false }))
    await wrapper.get('.wpf-module-card').trigger('click')
    const removeEntry = wrapper.get('.module-remove-entry')
    expect(removeEntry.attributes('disabled')).toBeDefined()
    store.modules[0] = { ...store.modules[0], canRemove: true }; await wrapper.vm.$nextTick()
    await wrapper.get('.module-remove-entry').trigger('click')
    await wrapper.findAll('.module-remove-confirmation .q-button')[0].trigger('click')
    expect(wrapper.find('.module-remove-confirmation').exists()).toBe(false)
    expect(client.remove).not.toHaveBeenCalled()
    await wrapper.get('.module-remove-entry').trigger('click')
    store.selectedModuleId = null; await wrapper.vm.$nextTick()
    expect(wrapper.find('.module-remove-confirmation').exists()).toBe(false)
  })

  it('shows the localized warning result without exposing host details', async () => {
    const remove = vi.fn(async () => ({ disposition: 'SucceededWithWarning', snapshot: { generatedAt: new Date().toISOString(), modules: [] } }))
    const { wrapper } = page(item(), { remove }, { language: 'zh-CN', effectiveLanguage: 'zh-CN' })
    await wrapper.get('.wpf-module-card').trigger('click')
    expect(wrapper.get('.module-management').text()).toContain('模块管理')
    await wrapper.get('.module-remove-entry').trigger('click')
    expect(wrapper.get('.module-remove-confirmation').text()).toContain('保留模块数据')
    await wrapper.get('.module-remove-confirm').trigger('click'); await flushPromises()
    expect(useToastStore().kind).toBe('warning')
    expect(useToastStore().message).toBe('模块程序已移除，但启动授权清理失败。')
  })

  it('keeps selection and resynchronizes after a failed removal', async () => {
    const getSnapshot = vi.fn(async () => ({ generatedAt: new Date().toISOString(), modules: [item()] }))
    const { wrapper, store } = page(item(), { remove: vi.fn().mockRejectedValue(new Error('C:/private/module')), getSnapshot })
    await wrapper.get('.wpf-module-card').trigger('click')
    await wrapper.get('.module-remove-entry').trigger('click')
    await wrapper.get('.module-remove-confirm').trigger('click'); await flushPromises()
    expect(getSnapshot).toHaveBeenCalledTimes(1)
    expect(store.selectedModuleId).toBe('qing.text')
    expect(useToastStore().message).toBe('Could not remove the module.')
    expect(useToastStore().message).not.toContain('C:/private')
  })
})

describe('ModulesPage host-authoritative update presentation', () => {
  it('checks once, shows pending feedback, and applies the complete host snapshot', async () => {
    let resolve!: (value: unknown) => void
    const available = item({ updateStatus: 'UpdateAvailable', targetVersion: '1.1.0', releaseNotes: 'Safer formatting.', canDownloadUpdate: true })
    const checkUpdate = vi.fn(() => new Promise(value => { resolve = value }))
    const { wrapper, store } = page(item(), { checkUpdate })
    await wrapper.get('.wpf-module-card').trigger('click')
    expect(wrapper.get('.module-update').text()).toContain('Current version')
    expect(wrapper.get('.module-update').text()).toContain('1.0.0')
    const check = wrapper.get('.module-check-update')
    await check.trigger('click'); await check.trigger('click')
    expect(checkUpdate).toHaveBeenCalledTimes(1)
    expect(checkUpdate).toHaveBeenCalledWith('qing.text')
    expect(check.attributes('aria-busy')).toBe('true')
    expect(check.text()).toContain('Checking…')
    expect(check.find('.module-operation-spinner').exists()).toBe(true)
    resolve({ generatedAt: new Date().toISOString(), modules: [available] })
    await flushPromises()
    expect(store.selectedModuleId).toBe('qing.text')
    expect(wrapper.get('.module-update').text()).toContain('1.1.0')
    expect(wrapper.get('.module-update').text()).toContain('Safer formatting.')
    expect(wrapper.get('.module-update-notes').attributes('tabindex')).toBe('0')
    expect(wrapper.findAll('.module-update-overall .q-badge')).toHaveLength(1)
    expect(wrapper.get('.module-download-update').classes()).toContain('is-primary')
    expect(wrapper.get('.module-download-update .q-icon path').attributes('d')).toBe('M10 2.8v9')
    expect(wrapper.get('.module-card-badges').text()).toContain('Update available')
  })

  it('keeps the current version unchanged after verified staging and offers no install action', async () => {
    const verified = item({ updateStatus: 'UpdateAvailable', targetVersion: '1.1.0', canDownloadUpdate: false, downloadStatus: 'Verified', downloadBytesReceived: 512, downloadExpectedBytes: 512 })
    const downloadUpdate = vi.fn(async () => ({ generatedAt: new Date().toISOString(), modules: [verified] }))
    const { wrapper } = page(item({ updateStatus: 'UpdateAvailable', targetVersion: '1.1.0', canDownloadUpdate: true }), { downloadUpdate })
    await wrapper.get('.wpf-module-card').trigger('click')
    await wrapper.get('.module-download-update').trigger('click')
    await flushPromises()
    const update = wrapper.get('.module-update')
    expect(downloadUpdate).toHaveBeenCalledWith('qing.text')
    expect(update.text()).toContain('The update package has been downloaded and verified.')
    expect(update.text()).toContain('It has not been installed, and the current module version is unchanged.')
    expect(update.text()).toContain('1.0.0')
    expect(wrapper.get('.module-card-badges').text()).toContain('Update verified')
    expect(wrapper.findAll('.module-update-overall .q-badge')).toHaveLength(1)
    expect(wrapper.findAll('.module-update-detail')).toHaveLength(1)
    expect(wrapper.find('.module-update-verified').exists()).toBe(true)
    expect(wrapper.find('.module-download-status').exists()).toBe(false)
    expect(wrapper.find('.module-install-unavailable').exists()).toBe(true)
    expect(wrapper.find('.module-download-update').exists()).toBe(false)
    expect(update.text().toLowerCase()).not.toContain('install update')
  })

  it('formats host-reported download progress without inventing an unknown percentage', async () => {
    const { wrapper, store } = page(item({
      updateStatus: 'UpdateAvailable', targetVersion: '1.1.0', canDownloadUpdate: true,
      downloadStatus: 'Downloading', isDownloadActive: true,
      downloadBytesReceived: 2_516_582, downloadExpectedBytes: 8_388_608,
    }))
    await wrapper.get('.wpf-module-card').trigger('click')
    expect(wrapper.get('.module-download-status').text()).toContain('2.4 MB / 8.0 MB · 30%')

    store.complete({ generatedAt: new Date().toISOString(), modules: [item({
      updateStatus: 'UpdateAvailable', targetVersion: '1.1.0', canDownloadUpdate: true,
      downloadStatus: 'Downloading', isDownloadActive: true,
      downloadBytesReceived: 1536, downloadExpectedBytes: 0,
    })] })
    await flushPromises()
    expect(wrapper.get('.module-download-status').text()).toContain('1.5 KB')
    expect(wrapper.get('.module-download-status').text()).not.toContain('%')
  })

  it('uses a status-specific safe toast for a failed package validation', async () => {
    const failed = item({ updateStatus: 'UpdateAvailable', targetVersion: '1.1.0', downloadStatus: 'HashMismatch' })
    const downloadUpdate = vi.fn(async () => ({ generatedAt: new Date().toISOString(), modules: [failed] }))
    const { wrapper } = page(item({ updateStatus: 'UpdateAvailable', targetVersion: '1.1.0', canDownloadUpdate: true }), { downloadUpdate })
    await wrapper.get('.wpf-module-card').trigger('click')
    await wrapper.get('.module-download-update').trigger('click'); await flushPromises()
    expect(useToastStore().kind).toBe('error')
    expect(useToastStore().message).toBe('The update package failed size or hash verification.')
  })

  it('uses host failure classifications and safe localized resynchronization', async () => {
    const failed = item({ updateStatus: 'HostVersionIncompatible', targetVersion: '2.0.0', downloadStatus: 'HashMismatch' })
    const getSnapshot = vi.fn(async () => ({ generatedAt: new Date().toISOString(), modules: [failed] }))
    const { wrapper } = page(item(), { checkUpdate: vi.fn().mockRejectedValue(new Error('C:/private/update.qmod')), getSnapshot })
    await wrapper.get('.wpf-module-card').trigger('click')
    await wrapper.get('.module-check-update').trigger('click'); await flushPromises()
    expect(getSnapshot).toHaveBeenCalledTimes(1)
    expect(wrapper.get('.module-update').text()).toContain('Package hash mismatch')
    expect(wrapper.get('.module-download-status').classes()).toContain('is-danger')
    expect(useToastStore().message).toBe('The update check failed.')
    expect(useToastStore().message).not.toContain('C:/private')
  })

  it('renders Simplified Chinese update labels from the shared localization system', async () => {
    const { wrapper } = page(item({ updateStatus: 'UpdateAvailable', targetVersion: '1.1.0', canDownloadUpdate: true }), {}, { language: 'zh-CN', effectiveLanguage: 'zh-CN' })
    await wrapper.get('.wpf-module-card').trigger('click')
    expect(wrapper.get('.module-update').text()).toContain('模块更新')
    expect(wrapper.get('.module-check-update').text()).toContain('检查更新')
    expect(wrapper.get('.module-download-update').text()).toContain('下载并验证')
    expect(wrapper.get('.module-card-badges').text()).toContain('有可用更新')
  })

  it('shows installation only for a host-authorized verified package and confirms inline', async () => {
    const installVerifiedUpdate=vi.fn()
    const verified=item({updateStatus:'UpdateAvailable',targetVersion:'1.1.0',downloadStatus:'Verified',canInstallVerifiedUpdate:true})
    const {wrapper,store}=page(verified,{installVerifiedUpdate})
    await wrapper.get('.wpf-module-card').trigger('click')
    expect(wrapper.findAll('.module-update-detail')).toHaveLength(1)
    expect(wrapper.find('.module-update-verified').exists()).toBe(true)
    expect(wrapper.find('.module-download-status').exists()).toBe(false)
    expect(wrapper.find('.module-install-unavailable').exists()).toBe(false)
    expect(wrapper.find('.module-download-update').exists()).toBe(false)
    expect(wrapper.get('.module-install-update').classes()).toContain('is-primary')
    await wrapper.get('.module-install-update').trigger('click')
    expect(installVerifiedUpdate).not.toHaveBeenCalled()
    const confirmation=wrapper.get('.module-install-confirmation')
    expect(confirmation.text()).toContain('1.0.0')
    expect(confirmation.text()).toContain('1.1.0')
    expect(confirmation.classes()).not.toContain('is-danger')
    await confirmation.get('.q-button.is-secondary').trigger('click')
    expect(wrapper.find('.module-install-confirmation').exists()).toBe(false)
  })

  it('keeps install and removal confirmations exclusive and closes each with Escape', async () => {
    const verified=item({updateStatus:'UpdateAvailable',targetVersion:'1.1.0',downloadStatus:'Verified',canInstallVerifiedUpdate:true})
    const {wrapper,store}=page(verified)
    await wrapper.get('.wpf-module-card').trigger('click')
    await wrapper.get('.module-install-update').trigger('click')
    expect(wrapper.find('.module-install-confirmation').exists()).toBe(true)
    expect(wrapper.find('.module-remove-confirmation').exists()).toBe(false)

    await wrapper.get('.module-remove-entry').trigger('click')
    expect(wrapper.find('.module-install-confirmation').exists()).toBe(false)
    expect(wrapper.find('.module-remove-confirmation').exists()).toBe(true)

    await wrapper.get('.module-install-update').trigger('click')
    expect(wrapper.find('.module-install-confirmation').exists()).toBe(true)
    expect(wrapper.find('.module-remove-confirmation').exists()).toBe(false)
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }))
    await wrapper.vm.$nextTick()
    expect(wrapper.find('.module-install-confirmation').exists()).toBe(false)
    expect(store.selectedModuleId).toBe('qing.text')

    await wrapper.get('.module-remove-entry').trigger('click')
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }))
    await wrapper.vm.$nextTick()
    expect(wrapper.find('.module-remove-confirmation').exists()).toBe(false)
    expect(store.selectedModuleId).toBe('qing.text')
  })

  it('waits for the authoritative installed snapshot and keeps the module selected', async () => {
    let resolve!:(value:unknown)=>void
    const pending=new Promise(value=>{resolve=value})
    const verified=item({updateStatus:'UpdateAvailable',targetVersion:'1.1.0',downloadStatus:'Verified',canInstallVerifiedUpdate:true,canOpen:true,canUnload:true})
    const installed=item({version:'1.1.0',updateStatus:'NotChecked',targetVersion:null,downloadStatus:'NotDownloaded',canInstallVerifiedUpdate:false,canOpen:true,canUnload:true})
    const installVerifiedUpdate=vi.fn(()=>pending)
    const {wrapper,store,client}=page(verified,{installVerifiedUpdate})
    await wrapper.get('.wpf-module-card').trigger('click')
    await wrapper.get('.module-install-update').trigger('click')
    await wrapper.get('.module-install-confirm').trigger('click')
    expect(installVerifiedUpdate).toHaveBeenCalledTimes(1)
    expect(installVerifiedUpdate).toHaveBeenCalledWith('qing.text')
    expect(wrapper.get('.module-install-confirm').text()).toContain('Installing update…')
    expect(wrapper.get('.wpf-module-details').text()).toContain('v1.0.0')
    expect(wrapper.get('.module-open-directory').attributes('disabled')).toBeDefined()
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }))
    await wrapper.vm.$nextTick()
    expect(wrapper.find('.module-install-confirmation').exists()).toBe(true)
    expect(store.selectedModuleId).toBe('qing.text')
    resolve({disposition:'Installed',sourceVersion:'1.0.0',targetVersion:'1.1.0',snapshot:{generatedAt:new Date().toISOString(),modules:[installed]}})
    await flushPromises()
    expect(store.selectedModuleId).toBe('qing.text')
    expect(store.selectedModule?.version).toBe('1.1.0')
    expect(useToastStore().message).toBe('Text Tools was updated to v1.1.0.')
    expect(client.load).not.toHaveBeenCalled();expect(client.activate).not.toHaveBeenCalled();expect(client.open).not.toHaveBeenCalled()
  })

  it('reports rollback and recovery from complete host snapshots without optimistic success', async () => {
    const verified=item({updateStatus:'UpdateAvailable',targetVersion:'1.1.0',downloadStatus:'Verified',canInstallVerifiedUpdate:true})
    const rolled=item({...verified,canInstallVerifiedUpdate:false})
    const installVerifiedUpdate=vi.fn(async()=>({disposition:'RolledBack',sourceVersion:'1.0.0',targetVersion:'1.1.0',snapshot:{generatedAt:new Date().toISOString(),modules:[rolled]}}))
    const {wrapper,store}=page(verified,{installVerifiedUpdate})
    await wrapper.get('.wpf-module-card').trigger('click');await wrapper.get('.module-install-update').trigger('click');await wrapper.get('.module-install-confirm').trigger('click');await flushPromises()
    expect(useToastStore().kind).toBe('warning');expect(useToastStore().message).toContain('restored to v1.0.0')
    installVerifiedUpdate.mockResolvedValue({disposition:'RecoveryRequired',sourceVersion:'1.0.0',targetVersion:'1.1.0',snapshot:{generatedAt:new Date().toISOString(),modules:[item({...verified,isExecutionBlocked:true,canInstallVerifiedUpdate:false})]}})
    store.complete({generatedAt:new Date().toISOString(),modules:[verified]})
    await flushPromises()
    await wrapper.get('.module-install-update').trigger('click');await wrapper.get('.module-install-confirm').trigger('click');await flushPromises()
    expect(useToastStore().kind).toBe('error');expect(wrapper.text()).toContain('Module operations are blocked while recovery is pending.')
  })
})
