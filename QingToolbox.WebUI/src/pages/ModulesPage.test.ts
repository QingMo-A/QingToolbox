import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import ModulesPage from './ModulesPage.vue'
import { useAppStore } from '../app/store'
import { useModuleStore } from '../app/moduleStore'
import { useToastStore } from '../app/toastStore'
import type { ModuleSnapshotItem } from '../contracts/modules'

const item = (overrides: Partial<ModuleSnapshotItem> = {}): ModuleSnapshotItem => ({
  id: 'qing.text', displayName: 'Text Tools', displayDescription: 'Formatting tools', version: '1.0.0', author: 'Qing', runtimeType: 'OutOfProcess', loadMode: 'Manual', runtimeState: 'NotLoaded', isValid: true, errorCount: 0, errors: [], permissions: [], minimumHostVersion: '0.2', isUserInstalled: true, canLoad: true, canActivate: false, canOpen: false, canDeactivate: false, canUnload: false, isBusy: false, isExecutionBlocked: false, isStartupEnabled: false, startupAuthorizationState: 'NotEnabled', canChangeStartupAuthorization: true, isStartupAuthorizationBusy: false, ...overrides
})
const mounted: ReturnType<typeof mount>[] = []
afterEach(() => { mounted.splice(0).forEach(x => x.unmount()); document.body.innerHTML = ''; vi.restoreAllMocks() })

function page(module = item(), clientOverrides: Record<string, unknown> = {}, state: { confirmed?: boolean; status?: 'idle'|'loading'|'ready'|'error'; bridge?: string } = {}) {
  const pinia = createPinia(); setActivePinia(pinia)
  const app = useAppStore(); app.bridge = state.bridge ?? 'Connected'
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

describe('ModulesPage lifecycle controls', () => {
  it.each([
    [item(), ['Load', 'Details']],
    [item({ runtimeState: 'Unloaded' }), ['Load', 'Details']],
    [item({ runtimeState: 'Loaded', canLoad: false, canActivate: true, canOpen: true }), ['Activate', 'Open', 'Details']],
    [item({ runtimeState: 'Running', canLoad: false, canOpen: true, canDeactivate: true, canUnload: true }), ['Open', 'Details']],
    [item({ runtimeState: 'Deactivated', canLoad: false, canActivate: true, canOpen: true, canUnload: true }), ['Activate', 'Open', 'Details']],
    [item({ runtimeState: 'Failed', isValid: false, canLoad: false, errorCount: 1, errors: ['failed'] }), ['Details']]
  ])('keeps card actions compact for each runtime state', (module, expected) => {
    const { wrapper } = page(module as ModuleSnapshotItem)
    expect(cardLabels(wrapper)).toEqual(expected)
    expect(cardLabels(wrapper).length).toBeLessThanOrEqual(3)
    expect(cardLabels(wrapper)).not.toContain('Deactivate')
    expect(cardLabels(wrapper)).not.toContain('Unload')
  })

  it('keeps full shutdown operations in the details action section', async () => {
    const { wrapper } = page(item({ runtimeState: 'Running', canLoad: false, canOpen: true, canDeactivate: true, canUnload: true }))
    await wrapper.get('.module-details-button').trigger('click')
    const details = wrapper.get('.wpf-module-details')
    expect(details.text()).toContain('Module actions')
    expect(details.findAll('.module-detail-actions .q-button').map(button => button.text())).toEqual(['Open', 'Deactivate', 'Unload'])
    expect(details.text()).toContain('Module information')
  })

  it('groups runtime and startup badges without duplicating startup controls on cards', async () => {
    const { wrapper } = page(item({ runtimeState: 'Running', canLoad: false, canOpen: true, startupAuthorizationState: 'Enabled', isStartupEnabled: true }))
    expect(wrapper.get('.module-card-badges').text()).toContain('Running')
    expect(wrapper.get('.module-card-badges').text()).toContain('Starts on launch')
    expect(wrapper.find('.q-switch').exists()).toBe(false)
    await wrapper.get('.module-details-button').trigger('click')
    expect(wrapper.get('.q-switch').attributes('role')).toBe('switch')
  })

  it('loads from the card without optimistic runtime changes', async () => {
    let resolve!: (value: unknown) => void
    const pending = new Promise(value => { resolve = value })
    const { wrapper, client } = page(item(), { load: vi.fn(() => pending) })
    await wrapper.findAll('.module-card-actions .q-button').find(button => button.text() === 'Load')!.trigger('click')
    expect(client.load).toHaveBeenCalledTimes(1)
    expect(useModuleStore().modules[0].runtimeState).toBe('NotLoaded')
    expect(wrapper.text()).toContain('Loading…')
    resolve({ generatedAt: new Date().toISOString(), modules: [item({ runtimeState: 'Loaded', canLoad: false, canActivate: true })] })
    await flushPromises()
    expect(useModuleStore().modules[0].runtimeState).toBe('Loaded')
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
    const { wrapper } = page(before, { load: vi.fn().mockRejectedValue(new Error('ModuleBusy: busy')), getSnapshot })
    await wrapper.findAll('.module-card-actions .q-button').find(button => button.text() === 'Load')!.trigger('click')
    await flushPromises()
    expect(useModuleStore().modules[0]).toEqual(before)
    expect(getSnapshot).toHaveBeenCalledTimes(1)
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
    ['deactivate', item({ runtimeState: 'Running', canLoad: false, canDeactivate: true }), 'Deactivate', true, 'Text Tools could not be deactivated.'],
    ['unload', item({ runtimeState: 'Running', canLoad: false, canUnload: true }), 'Unload', true, 'Text Tools could not be unloaded.'],
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
    expect(client[operation as keyof typeof client]).toHaveBeenCalledTimes(1)
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
})
