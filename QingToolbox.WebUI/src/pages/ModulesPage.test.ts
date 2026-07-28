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
  id: 'qing.text', displayName: 'Text Tools', displayDescription: 'Formatting tools', version: '1.0.0', author: 'Qing', runtimeType: 'OutOfProcess', loadMode: 'Manual', runtimeState: 'NotLoaded', isValid: true, errorCount: 0, errors: [], permissions: [], minimumHostVersion: '0.2', isUserInstalled: true, canLoad: true, canActivate: false, canOpen: false, canDeactivate: false, canUnload: false, isBusy: false, isExecutionBlocked: false, isStartupEnabled: false, startupAuthorizationState: 'NotEnabled', canChangeStartupAuthorization: true, isStartupAuthorizationBusy: false, ...overrides
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
  const client = { getSnapshot: vi.fn(async () => snapshot()), load: vi.fn(async () => snapshot()), activate: vi.fn(async () => snapshot()), open: vi.fn(async () => snapshot()), deactivate: vi.fn(async () => snapshot()), unload: vi.fn(async () => snapshot()), setStartupAuthorization: vi.fn(async () => snapshot()), ...clientOverrides }
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
    [item(), ['Load', 'Details']],
    [item({ runtimeState: 'Unloaded' }), ['Load', 'Details']],
    [item({ runtimeState: 'Loaded', canLoad: false, canActivate: true, canOpen: true, canUnload: true }), ['Activate', 'Open', 'Unload', 'Details']],
    [item({ runtimeState: 'Running', canLoad: false, canOpen: true, canDeactivate: true, canUnload: true }), ['Open', 'Deactivate', 'Unload', 'Details']],
    [item({ runtimeState: 'Deactivated', canLoad: false, canActivate: true, canOpen: true, canUnload: true }), ['Activate', 'Open', 'Unload', 'Details']],
    [item({ runtimeState: 'Failed', isValid: false, canLoad: false, errorCount: 1, errors: ['failed'] }), ['Details']],
    [item({ runtimeState: 'FutureFailure', isValid: false, canLoad: false, canUnload: true, errorCount: 1 }), ['Unload', 'Details']],
  ])('shows every host-confirmed card action in stable order', (module, expected) => {
    const { wrapper } = page(module as ModuleSnapshotItem)
    expect(cardLabels(wrapper)).toEqual(expected)
  })

  it('uses the same lifecycle operation order on the card and in details', async () => {
    const { wrapper } = page(item({ runtimeState: 'Running', canLoad: false, canOpen: true, canDeactivate: true, canUnload: true }))
    expect(cardLabels(wrapper)).toEqual(['Open', 'Deactivate', 'Unload', 'Details'])
    await wrapper.get('.module-details-button').trigger('click')
    const details = wrapper.get('.wpf-module-details')
    expect(details.text()).toContain('Module actions')
    expect(details.findAll('.module-detail-actions .q-button').map(button => button.text())).toEqual(cardLabels(wrapper).slice(0, -1))
    expect(details.text()).toContain('Module information')
  })

  it('hides execution-sensitive actions when the host reports execution blocked', async () => {
    const blocked = item({ runtimeState: 'Running', canLoad: false, canActivate: true, canOpen: true, canDeactivate: true, canUnload: true, isExecutionBlocked: true })
    const { wrapper } = page(blocked)
    expect(cardLabels(wrapper)).toEqual(['Activate', 'Details'])
    await wrapper.get('.module-details-button').trigger('click')
    expect(wrapper.findAll('.module-detail-actions .q-button').map(button => button.text())).toEqual(['Activate'])
  })

  it('keeps Details available and locks every lifecycle action while one card operation is pending', async () => {
    let resolve!: (value: unknown) => void
    const pending = new Promise(value => { resolve = value })
    const running = item({ runtimeState: 'Running', canLoad: false, canOpen: true, canDeactivate: true, canUnload: true })
    const deactivate = vi.fn(() => pending)
    const unload = vi.fn()
    const { wrapper } = page(running, { deactivate, unload })
    await wrapper.findAll('.module-card-actions .q-button').find(button => button.text() === 'Deactivate')!.trigger('click')
    const actions = wrapper.findAll('.module-card-actions .q-button')
    expect(actions.map(button => button.text())).toEqual(['Open', 'Deactivating…', 'Unload', 'Details'])
    expect(actions.slice(0, -1).every(button => button.attributes('disabled') !== undefined)).toBe(true)
    expect(actions.at(-1)!.attributes('disabled')).toBeUndefined()
    await actions.find(button => button.text() === 'Unload')!.trigger('click')
    expect(deactivate).toHaveBeenCalledTimes(1)
    expect(unload).not.toHaveBeenCalled()
    expect(useModuleStore().modules[0].runtimeState).toBe('Running')
    resolve({ generatedAt: new Date().toISOString(), modules: [item({ runtimeState: 'Deactivated', canLoad: false, canActivate: true, canOpen: true, canUnload: true })] })
    await flushPromises()
    expect(cardLabels(wrapper)).toEqual(['Activate', 'Open', 'Unload', 'Details'])
  })

  it('does not optimistically change runtime state while card Unload is pending', async () => {
    let resolve!: (value: unknown) => void
    const pending = new Promise(value => { resolve = value })
    const loaded = item({ runtimeState: 'Loaded', canLoad: false, canActivate: true, canOpen: true, canUnload: true })
    const unload = vi.fn(() => pending)
    const { wrapper } = page(loaded, { unload })
    await wrapper.findAll('.module-card-actions .q-button').find(button => button.text() === 'Unload')!.trigger('click')
    expect(unload).toHaveBeenCalledWith('qing.text')
    expect(useModuleStore().modules[0].runtimeState).toBe('Loaded')
    expect(cardLabels(wrapper)).toEqual(['Activate', 'Open', 'Unloading…', 'Details'])
    resolve({ generatedAt: new Date().toISOString(), modules: [item({ runtimeState: 'Unloaded', canLoad: true })] })
    await flushPromises()
    expect(useModuleStore().modules[0].runtimeState).toBe('Unloaded')
    expect(cardLabels(wrapper)).toEqual(['Load', 'Details'])
  })

  it('groups runtime and startup badges without duplicating startup controls on cards', async () => {
    const { wrapper } = page(item({ runtimeState: 'Running', canLoad: false, canOpen: true, startupAuthorizationState: 'Enabled', isStartupEnabled: true }))
    expect(wrapper.get('.module-card-badges').text()).toContain('Running')
    expect(wrapper.get('.module-card-badges').text()).toContain('Starts on launch')
    expect(wrapper.find('.q-switch').exists()).toBe(false)
    await wrapper.get('.module-details-button').trigger('click')
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
    expect(cardLabels(wrapper)).toEqual(['打开', '停用', '卸载', '详情'])
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

  it.each([
    ['activate', item({ runtimeState: 'Loaded', canLoad: false, canActivate: true }), item({ runtimeState: 'Running', canLoad: false, canActivate: false, canOpen: true }), 'Activate', false, { 'Not loaded': 0, Loaded: 0, Running: 1 }],
    ['deactivate', item({ runtimeState: 'Running', canLoad: false, canDeactivate: true }), item({ runtimeState: 'Deactivated', canLoad: false, canActivate: true, canUnload: true }), 'Deactivate', false, { 'Not loaded': 0, Loaded: 1, Running: 0 }],
    ['unload', item({ runtimeState: 'Deactivated', canLoad: false, canUnload: true }), item({ runtimeState: 'Unloaded', canLoad: true }), 'Unload', false, { 'Not loaded': 1, Loaded: 0, Running: 0 }],
  ] as const)('uses the complete host snapshot after %s', async (operation, before, after, label, details, expected) => {
    const result = { generatedAt: new Date().toISOString(), modules: [after] }
    const { wrapper } = page(before, { [operation]: vi.fn().mockResolvedValue(result) })
    if (details) await wrapper.get('.module-details-button').trigger('click')
    const scope = details ? wrapper.get('.module-detail-actions') : wrapper.get('.module-card-actions')
    await scope.findAll('.q-button').find(button => button.text() === label)!.trigger('click')
    await flushPromises()
    expect(summaryValues(wrapper)).toMatchObject(expected)
  })

  it('opens details by mouse and keyboard and closes them with Escape', async () => {
    const { wrapper } = page()
    const details = wrapper.get('.module-details-button')
    expect(details.element.tagName).toBe('BUTTON')
    await details.trigger('keydown', { key: 'Enter' })
    expect(useModuleStore().selectedModuleId).toBe('qing.text')
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }))
    await wrapper.vm.$nextTick()
    expect(useModuleStore().selectedModuleId).toBeNull()
  })

  it('keeps startup authorization in details and waits for the host snapshot', async () => {
    let resolve!: (value: unknown) => void
    const pending = new Promise(value => { resolve = value })
    const setStartupAuthorization = vi.fn(() => pending)
    const { wrapper } = page(item(), { setStartupAuthorization })
    await wrapper.get('.module-details-button').trigger('click')
    await wrapper.get('.q-switch').trigger('click')
    expect(setStartupAuthorization).toHaveBeenCalledWith('qing.text', true)
    expect(useModuleStore().modules[0].isStartupEnabled).toBe(false)
    expect(wrapper.text()).toContain('Authorizing…')
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
    expect(wrapper.get('.module-details-button').attributes('disabled')).toBeUndefined()
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
    const details = wrapper.get('.module-details-button')
    expect(details.attributes('disabled')).toBeUndefined()
    await details.trigger('click')
    expect(wrapper.text()).toContain('Module information')
    expect(wrapper.get('.q-switch').attributes('disabled')).toBeDefined()
  })

  it('disables all host actions but keeps details available on a stale running module', async () => {
    const running = item({ runtimeState: 'Running', canLoad: false, canOpen: true, canDeactivate: true, canUnload: true })
    const { wrapper } = page(running, {}, { status: 'error' })
    expect(wrapper.findAll('.module-card-actions .q-button').find(button => button.text() === 'Open')!.attributes('disabled')).toBeDefined()
    await wrapper.get('.module-details-button').trigger('click')
    for (const button of wrapper.findAll('.module-detail-actions .q-button')) expect(button.attributes('disabled')).toBeDefined()
    expect(wrapper.get('.module-details-button').attributes('disabled')).toBeUndefined()
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
    expect(wrapper.get('.module-details-button').attributes('disabled')).toBeUndefined()
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
    await wrapper.get('.module-details-button').trigger('click')
    expect(wrapper.text()).toContain('Module information')
  })

  it.each([
    ['load', item(), 'Load', false, 'Text Tools could not be loaded.'],
    ['activate', item({ runtimeState: 'Loaded', canLoad: false, canActivate: true }), 'Activate', false, 'Text Tools could not be activated.'],
    ['open', item({ runtimeState: 'Loaded', canLoad: false, canOpen: true }), 'Open', false, 'The Text Tools window could not be opened.'],
    ['deactivate', item({ runtimeState: 'Running', canLoad: false, canDeactivate: true }), 'Deactivate', false, 'Text Tools could not be deactivated.'],
    ['unload', item({ runtimeState: 'Running', canLoad: false, canUnload: true }), 'Unload', false, 'Text Tools could not be unloaded.'],
  ] as const)('uses a safe %s failure message and resyncs once', async (operation, module, label, details, expected) => {
    const getSnapshot = vi.fn().mockRejectedValue(new Error('resync secret'))
    const { wrapper, client, store } = page(module, { [operation]: vi.fn().mockRejectedValue(new Error('Bridge.SecretCode: internal path')), getSnapshot })
    if (details) await wrapper.get('.module-details-button').trigger('click')
    const scope = details ? wrapper.get('.module-detail-actions') : wrapper.get('.module-card-actions')
    await scope.findAll('.q-button').find(button => button.text() === label)!.trigger('click')
    await flushPromises()
    expect(useToastStore().message).toBe(expected)
    expect(useToastStore().message).not.toContain('SecretCode')
    expect(getSnapshot).toHaveBeenCalledTimes(1)
    expect(store.modules[0]).toEqual(module)
    expect(store.status).toBe('error')
    expect(store.error).toBe('The host could not confirm the current module state.')
    expect(wrapper.text()).not.toContain('resync secret')
    expect(client[operation as keyof typeof client]).toHaveBeenCalledTimes(1)
    if (details) {
      expect(wrapper.get('.wpf-module-details').text()).toContain('Module information')
      for (const button of wrapper.findAll('.module-detail-actions .q-button')) expect(button.attributes('disabled')).toBeDefined()
      await wrapper.get('.wpf-back').trigger('click')
      expect(wrapper.find('.wpf-module-details').exists()).toBe(false)
    }
  })

  it('uses a safe startup authorization failure and preserves the snapshot', async () => {
    const module = item()
    const getSnapshot = vi.fn().mockRejectedValue(new Error('resync secret'))
    const { wrapper, store } = page(module, { setStartupAuthorization: vi.fn().mockRejectedValue(new Error('FingerprintMismatch: path')), getSnapshot })
    await wrapper.get('.module-details-button').trigger('click')
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
    await wrapper.get('.wpf-page-header .q-button').trigger('click')
    await flushPromises()
    expect(getSnapshot).toHaveBeenCalledTimes(2)
    expect(store.status).toBe('ready')
    expect(store.modules).toEqual([recovered])
  })

  it('keeps the old snapshot and safe page state after a refresh failure', async () => {
    const getSnapshot = vi.fn().mockRejectedValue(new Error('Bridge.Timeout: endpoint'))
    const { wrapper, store } = page(item(), { getSnapshot })
    await wrapper.get('.wpf-page-header .q-button').trigger('click')
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
    await wrapper.get('.module-details-button').trigger('click')
    expect(wrapper.text()).toContain('模块操作'); expect(wrapper.text()).toContain('启动'); expect(wrapper.text()).toContain('模块信息')
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
    await second.wrapper.get('.module-details-button').trigger('click'); await second.wrapper.get('.q-switch').trigger('click'); await flushPromises()
    expect(useToastStore().message).toBe('无法更新 Text Tools 的启动授权。')
  })

  it('keeps unknown runtime states host-authored while translating known and invalid states', async () => {
    const { wrapper, store } = page(item({ runtimeState: 'FutureState', canLoad: false }), {}, { language: 'zh-CN', effectiveLanguage: 'zh-CN' })
    expect(wrapper.text()).toContain('FutureState')
    store.complete({ generatedAt: new Date().toISOString(), modules: [item({ runtimeState: 'Failed', isValid: false, canLoad: false })] }); await wrapper.vm.$nextTick()
    expect(wrapper.get('.module-card-badges').text()).toContain('无效')
  })
})
