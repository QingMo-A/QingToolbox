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

function page(module = item(), clientOverrides: Record<string, unknown> = {}) {
  const pinia = createPinia(); setActivePinia(pinia); useAppStore().bridge = 'Connected'
  useModuleStore().complete({ generatedAt: new Date().toISOString(), modules: [module] })
  const snapshot = () => ({ generatedAt: new Date().toISOString(), modules: [module] })
  const client = { getSnapshot: vi.fn(async () => snapshot()), load: vi.fn(async () => snapshot()), activate: vi.fn(async () => snapshot()), open: vi.fn(async () => snapshot()), deactivate: vi.fn(async () => snapshot()), unload: vi.fn(async () => snapshot()), setStartupAuthorization: vi.fn(async () => snapshot()), ...clientOverrides }
  const wrapper = mount(ModulesPage, { attachTo: document.body, global: { plugins: [pinia], provide: { moduleClient: client } } }); mounted.push(wrapper)
  return { wrapper, client }
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
    expect(useToastStore().message).toContain('ModuleBusy')
  })
})
