import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import RunningModulesPage from './RunningModulesPage.vue'
import { router as appRouter } from '../app/router'
import { useAppStore } from '../app/store'
import { useModuleStore } from '../app/moduleStore'
import { useToastStore } from '../app/toastStore'
import { useSettingsStore } from '../app/settingsStore'
import type { EffectiveLanguageCode, LanguageCode, SettingsSnapshot } from '../contracts/settings'

const mounted: VueWrapper[] = []
afterEach(() => mounted.splice(0).forEach(wrapper => wrapper.unmount()))

const item = (id: string, runtimeState: string) => ({ id, displayName: id, displayDescription: `${id} description`, version: '1.0.0', author: 'Qing', runtimeType: 'OutOfProcess', loadMode: 'Manual', runtimeState, isValid: true, errorCount: 0, errors: [], permissions: [], minimumHostVersion: '0.2', isUserInstalled: true, canLoad: false, canActivate: false, canOpen: runtimeState === 'Running', canDeactivate: runtimeState === 'Running', canUnload: runtimeState === 'Running', isBusy: false, isExecutionBlocked: false, isStartupEnabled: false, startupAuthorizationState: 'NotEnabled' as const, canChangeStartupAuthorization: true, isStartupAuthorizationBusy: false })
const snapshot = { generatedAt: new Date().toISOString(), modules: [item('Running module', 'Running'), item('Loaded module', 'Loaded'), item('Waiting module', 'NotLoaded'), item('Failed module', 'Failed')] }
const settingsSnapshot = (code: LanguageCode, effectiveCode: EffectiveLanguageCode): SettingsSnapshot => ({
  generatedAt: new Date().toISOString(), language: { code, effectiveCode, displayName: code, options: [
    { code: 'system', displayName: 'System Default', nativeName: '跟随系统' }, { code: 'zh-CN', displayName: 'Simplified Chinese', nativeName: '简体中文' }, { code: 'en-US', displayName: 'English', nativeName: 'English' },
  ] }, showLogsInSidebar: true, mainWindowCloseBehavior: 'Ask', closeBehaviorMessage: '', launchAtLogin: false,
  canConfigureLaunchAtLogin: true, canRepairStartup: false, startupPresentationMode: 'FloatingBadge', startupBackend: 'None', startupStatus: 'Unavailable', startupMessage: '',
})

async function page(status: 'idle'|'loading'|'ready'|'error' = 'ready', clientOverrides: Record<string, unknown> = {}, options: { confirmed?: boolean; bridge?: string; language?: LanguageCode; effectiveLanguage?: EffectiveLanguageCode } = {}) {
  const pinia = createPinia()
  setActivePinia(pinia)
  if (options.language || options.effectiveLanguage) useSettingsStore().complete(settingsSnapshot(options.language ?? options.effectiveLanguage ?? 'en-US', options.effectiveLanguage ?? 'en-US'))
  useAppStore().bridge = options.bridge ?? 'Connected'
  const modules = useModuleStore()
  const confirmed = options.confirmed ?? status === 'ready'
  if (confirmed) modules.complete(snapshot)
  if (status === 'loading') modules.begin()
  else if (status === 'error') modules.fail(new Error('Bridge.Secret: offline'))
  else modules.status = status
  const getSnapshot = vi.fn(async () => snapshot)
  const open = vi.fn(async () => snapshot)
  const deactivate = vi.fn(async () => ({ generatedAt: new Date().toISOString(), modules: snapshot.modules.map(module => module.id === 'Running module' ? { ...module, runtimeState: 'Deactivated', canActivate: true, canOpen: true, canDeactivate: false, canUnload: true } : module) }))
  const unload = vi.fn(async () => ({ generatedAt: new Date().toISOString(), modules: snapshot.modules.map(module => module.id === 'Running module' ? { ...module, runtimeState: 'Unloaded', canLoad: true, canOpen: false, canDeactivate: false, canUnload: false } : module) }))
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/running', component: RunningModulesPage }, { path: '/modules', component: { template: '<div>Modules</div>' } }] })
  await router.push('/running')
  await router.isReady()
  const wrapper = mount(RunningModulesPage, { global: { plugins: [pinia, router], provide: { moduleClient: { getSnapshot, open, deactivate, unload, ...clientOverrides } } } })
  mounted.push(wrapper)
  return { wrapper, modules, router, getSnapshot, open, deactivate, unload }
}

describe('RunningModulesPage', () => {
  it('is registered at /running', () => {
    expect(appRouter.resolve('/running').matched).toHaveLength(1)
  })

  it('projects only modules confirmed Running by the host', async () => {
    const { wrapper } = await page()
    expect(wrapper.text()).toContain('Running module')
    expect(wrapper.text()).not.toContain('Loaded module')
    expect(wrapper.text()).not.toContain('Waiting module')
    expect(wrapper.text()).not.toContain('Failed module')
    expect(wrapper.text()).toContain('Open')
    expect(wrapper.text()).toContain('Deactivate')
    expect(wrapper.text()).toContain('Unload')
    expect(wrapper.get('.running-module-heading .q-badge').text()).toBe('Running')
    expect(wrapper.get('.running-module-action .q-button.is-primary').text()).toBe('Open')
    expect(wrapper.get('.running-details-link').classes()).not.toContain('q-button')
    expect(wrapper.text()).not.toContain('Remove')
    expect(wrapper.text()).not.toContain('Start with QingToolbox')
  })

  it('shows the empty state and links safely to modules', async () => {
    const { wrapper, modules } = await page()
    modules.complete({ generatedAt: new Date().toISOString(), modules: [] })
    await wrapper.vm.$nextTick()
    expect(wrapper.text()).toContain('No modules are currently running')
    expect(wrapper.get('a').attributes('href')).toBe('/modules')
  })

  it('opens details through the existing module workspace', async () => {
    const { wrapper, modules, router } = await page()
    await wrapper.findAll('.running-module-action button').find(button => button.text() === 'View details')!.trigger('click')
    await flushPromises()
    expect(modules.selectedModuleId).toBe('Running module')
    expect(router.currentRoute.value.path).toBe('/modules')
  })

  it('opens through the shared store and preserves details navigation', async () => {
    const { wrapper, modules, open } = await page()
    const openButton = wrapper.findAll('.running-module-action button').find(button => button.text() === 'Open')!
    await openButton.trigger('click')
    await flushPromises()
    expect(open).toHaveBeenCalledTimes(1)
    expect(modules.selectedModuleId).toBeNull()
    expect(modules.modules[0].runtimeState).toBe('Running')
  })

  it.each(['Deactivate', 'Unload'])('waits for the host before removing a running card for %s', async label => {
    let resolve!: (value: typeof snapshot) => void
    const pending = new Promise<typeof snapshot>(value => { resolve = value })
    const operation = vi.fn(() => pending)
    const { wrapper } = await page('ready', { [label.toLowerCase()]: operation })
    await wrapper.findAll('.running-module-action button').find(button => button.text() === label)!.trigger('click')
    expect(wrapper.text()).toContain('Running module')
    expect(wrapper.text()).toContain(label === 'Unload' ? 'Unloading…' : 'Deactivating…')
    resolve({ generatedAt: new Date().toISOString(), modules: snapshot.modules.map(module => module.id === 'Running module' ? { ...module, runtimeState: label === 'Unload' ? 'Unloaded' : 'Deactivated', canDeactivate: false, canUnload: label !== 'Unload' } : module) })
    await flushPromises()
    expect(operation).toHaveBeenCalledTimes(1)
    expect(wrapper.text()).not.toContain('Running module description')
  })

  it('preserves the host snapshot and resyncs only once after a failed shutdown operation', async () => {
    const getSnapshot = vi.fn().mockRejectedValue(new Error('resync failed'))
    const deactivate = vi.fn().mockRejectedValue(new Error('ModuleBusy: The requested module is busy.'))
    const { wrapper, modules } = await page('ready', { getSnapshot, deactivate })
    await wrapper.findAll('.running-module-action button').find(button => button.text() === 'Deactivate')!.trigger('click')
    await flushPromises()
    expect(modules.modules[0].runtimeState).toBe('Running')
    expect(deactivate).toHaveBeenCalledTimes(1)
    expect(getSnapshot).toHaveBeenCalledTimes(1)
    expect(modules.status).toBe('error')
    expect(modules.lastUpdatedAt).not.toBeNull()
    expect(modules.error).toBe('The host could not confirm the current module state.')
    expect(wrapper.get('[role="status"]').text()).toBe('The host could not refresh running modules. Showing the last confirmed snapshot.')
  })

  it('uses a native focusable button for keyboard detail navigation', async () => {
    const { wrapper } = await page()
    const button = wrapper.findAll('.running-module-action button').find(item => item.text() === 'View details')!
    expect(button.element.tagName).toBe('BUTTON')
    expect(button.attributes('disabled')).toBeUndefined()
    expect(button.attributes('tabindex')).not.toBe('-1')
  })

  it('keeps controls in a wrapping action group for narrow layouts', async () => {
    const { wrapper } = await page()
    expect(wrapper.get('.running-module-action').element.children).toHaveLength(4)
    expect(wrapper.get('.running-details-link').element.tagName).toBe('BUTTON')
  })

  it('requests one snapshot from idle and reuses a ready snapshot', async () => {
    const idle = await page('idle')
    await flushPromises()
    expect(idle.getSnapshot).toHaveBeenCalledTimes(1)
    idle.wrapper.unmount()
    const ready = await page('ready')
    await flushPromises()
    expect(ready.getSnapshot).not.toHaveBeenCalled()
  })

  it('renders loading and retries an error through the existing client', async () => {
    const loading = await page('loading')
    expect(loading.wrapper.findAll('.q-skeleton')).toHaveLength(3)
    loading.wrapper.unmount()
    const failed = await page('error')
    expect(failed.wrapper.text()).toContain('Running modules are unavailable')
    expect(failed.wrapper.text()).toContain('The host could not provide the current module snapshot.')
    expect(failed.wrapper.text()).not.toContain('Bridge.Secret')
    await failed.wrapper.get('button').trigger('click')
    await flushPromises()
    expect(failed.getSnapshot).toHaveBeenCalledTimes(1)
  })

  it.each([
    ['loading', 'Refreshing module state. Showing the last confirmed snapshot.'],
    ['error', 'The host could not refresh running modules. Showing the last confirmed snapshot.'],
  ] as const)('keeps running cards visible during stale %s state', async (status, message) => {
    const { wrapper, modules } = await page(status, {}, { confirmed: true })
    expect(wrapper.text()).toContain('Running module description')
    expect(wrapper.get('[role="status"]').text()).toBe(message)
    expect(wrapper.text()).not.toContain('Bridge.Secret')
    expect(modules.lastUpdatedAt).not.toBeNull()
    for (const button of wrapper.findAll('.running-module-action .q-button')) expect(button.attributes('disabled')).toBeDefined()
    expect(wrapper.get('.running-details-link').attributes('disabled')).toBeUndefined()
  })

  it('keeps the last running snapshot visible while disconnected', async () => {
    const { wrapper } = await page('error', {}, { confirmed: true, bridge: 'Disconnected' })
    expect(wrapper.text()).toContain('Running module description')
    expect(wrapper.get('[role="status"]').text()).toBe('The host is disconnected. Showing the last confirmed running-module snapshot.')
    expect(wrapper.text()).not.toContain('Bridge.Secret')
  })

  it('describes an empty stale snapshot without claiming live state', async () => {
    const { wrapper, modules } = await page()
    modules.complete({ generatedAt: new Date().toISOString(), modules: [] })
    modules.fail(new Error('hidden'))
    await wrapper.vm.$nextTick()
    expect(wrapper.text()).toContain('No running modules were present in the last confirmed snapshot.')
    expect(wrapper.text()).not.toContain('No modules are currently running')
  })

  it.each([
    ['open', 'Open', 'The Running module window could not be opened.'],
    ['deactivate', 'Deactivate', 'Running module could not be deactivated.'],
    ['unload', 'Unload', 'Running module could not be unloaded.'],
  ] as const)('uses a safe %s failure, resyncs once, and preserves the card', async (operation, label, expected) => {
    const getSnapshot = vi.fn().mockRejectedValue(new Error('resync path'))
    const failure = vi.fn().mockRejectedValue(new Error('Bridge.SecretCode: internal details'))
    const { wrapper, modules } = await page('ready', { [operation]: failure, getSnapshot })
    await wrapper.findAll('.running-module-action button').find(button => button.text() === label)!.trigger('click')
    await flushPromises()
    expect(useToastStore().message).toBe(expected)
    expect(useToastStore().message).not.toContain('SecretCode')
    expect(failure).toHaveBeenCalledTimes(1)
    expect(getSnapshot).toHaveBeenCalledTimes(1)
    expect(modules.modules[0].runtimeState).toBe('Running')
    expect(wrapper.text()).toContain('Running module description')
    expect(modules.status).toBe('error')
    expect(modules.error).toBe('The host could not confirm the current module state.')
    expect(wrapper.text()).not.toContain('resync path')
    for (const button of wrapper.findAll('.running-module-action .q-button')) expect(button.attributes('disabled')).toBeDefined()
    expect(wrapper.get('.running-details-link').attributes('disabled')).toBeUndefined()
  })

  it('uses a successful resync snapshot and removes cards no longer running', async () => {
    const resynced = { generatedAt: '2026-07-27T12:00:00.000Z', modules: snapshot.modules.map(module => module.id === 'Running module' ? { ...module, runtimeState: 'Unloaded', canOpen: false, canDeactivate: false, canUnload: false } : module) }
    const getSnapshot = vi.fn().mockResolvedValue(resynced)
    const { wrapper, modules } = await page('ready', { unload: vi.fn().mockRejectedValue(new Error('operation failed')), getSnapshot })
    await wrapper.findAll('.running-module-action button').find(button => button.text() === 'Unload')!.trigger('click')
    await flushPromises()
    expect(modules.status).toBe('ready')
    expect(modules.lastUpdatedAt).toBe(resynced.generatedAt)
    expect(wrapper.text()).not.toContain('Running module description')
    expect(wrapper.find('[role="status"]').exists()).toBe(false)
    expect(useToastStore().message).toBe('Running module could not be unloaded.')
    expect(getSnapshot).toHaveBeenCalledTimes(1)
  })

  it.each([['system','zh-CN'],['zh-CN','zh-CN']] as const)('localizes the running workspace for %s with effective %s', async (language, effectiveLanguage) => {
    const { wrapper } = await page('ready', {}, { language, effectiveLanguage })
    expect(wrapper.text()).toContain('运行中模块'); expect(wrapper.text()).toContain('宿主运行时中当前处于活动状态的模块。')
    expect(wrapper.text()).toContain('运行方式'); expect(wrapper.text()).toContain('作者'); expect(wrapper.text()).toContain('运行中')
    expect(wrapper.findAll('.running-module-action button').map(button => button.text())).toEqual(['打开','停用','卸载','查看详情'])
    for (const hostValue of ['Running module','Running module description','OutOfProcess','Qing']) expect(wrapper.text()).toContain(hostValue)
  })

  it('reactively changes labels without clearing running cards', async () => {
    const { wrapper, modules } = await page()
    useSettingsStore().complete(settingsSnapshot('system','zh-CN')); await wrapper.vm.$nextTick()
    expect(wrapper.text()).toContain('运行中模块'); expect(wrapper.text()).toContain('Running module description'); expect(modules.runningModules).toHaveLength(1)
  })

  it('localizes live, stale, unavailable, and disconnected states', async () => {
    const live = await page('ready', {}, { language: 'zh-CN', effectiveLanguage: 'zh-CN' }); live.modules.complete({ generatedAt: new Date().toISOString(), modules: [] }); await live.wrapper.vm.$nextTick()
    expect(live.wrapper.text()).toContain('当前没有正在运行的模块'); expect(live.wrapper.text()).toContain('前往模块')
    live.modules.fail(new Error('hidden')); await live.wrapper.vm.$nextTick(); expect(live.wrapper.text()).toContain('上次确认的快照中没有运行中模块')
    live.wrapper.unmount()
    const unavailable = await page('error', {}, { language: 'zh-CN', effectiveLanguage: 'zh-CN' }); expect(unavailable.wrapper.text()).toContain('运行中模块不可用'); expect(unavailable.wrapper.text()).toContain('重试')
    unavailable.wrapper.unmount()
    const disconnected = await page('error', {}, { confirmed: true, bridge: 'Disconnected', language: 'zh-CN', effectiveLanguage: 'zh-CN' }); expect(disconnected.wrapper.get('[role="status"]').text()).toContain('宿主已断开连接')
  })

  it('uses the shared localized operation Toast and preserves detail navigation', async () => {
    const failure = vi.fn().mockRejectedValue(new Error('SecretCode: hidden'))
    const resync = vi.fn().mockRejectedValue(new Error('hidden'))
    const { wrapper } = await page('ready', { open: failure, getSnapshot: resync }, { language: 'zh-CN', effectiveLanguage: 'zh-CN' })
    await wrapper.findAll('.running-module-action button').find(button => button.text() === '打开')!.trigger('click'); await flushPromises()
    expect(useToastStore().message).toBe('无法打开 Running module 窗口。'); expect(useToastStore().message).not.toContain('SecretCode'); expect(resync).toHaveBeenCalledTimes(1)
  })
})
