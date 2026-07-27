import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import HomePage from './HomePage.vue'
import { useAppStore } from '../app/store'
import { useModuleStore } from '../app/moduleStore'
import type { ModuleSnapshotItem } from '../contracts/modules'

const mounted: VueWrapper[] = []
afterEach(() => mounted.splice(0).forEach(wrapper => wrapper.unmount()))

function moduleItem(index: number, overrides: Partial<ModuleSnapshotItem> = {}): ModuleSnapshotItem {
  return { id: `module.${index}`, displayName: `Module ${index}`, displayDescription: `Description ${index}`, version: '1.0.0', author: 'Qing', runtimeType: 'InProcess', loadMode: 'Manual', runtimeState: 'NotLoaded', isValid: true, errorCount: 0, errors: [], permissions: [], minimumHostVersion: '0.2', isUserInstalled: true, canLoad: true, canActivate: false, canOpen: false, canDeactivate: false, canUnload: false, isBusy: false, isExecutionBlocked: false, isStartupEnabled: false, startupAuthorizationState: 'NotEnabled', canChangeStartupAuthorization: true, isStartupAuthorizationBusy: false, ...overrides }
}

async function page(options: { bridge?: string; status?: 'idle'|'loading'|'ready'|'error'; items?: ModuleSnapshotItem[]; getSnapshot?: ReturnType<typeof vi.fn> } = {}) {
  const pinia = createPinia(); setActivePinia(pinia)
  const app = useAppStore(); app.bridge = options.bridge ?? 'Connected'; app.mode = 'Development'
  app.snapshot = { environmentKind: 'Development', environmentDisplayName: 'Development', hostVersion: '0.2.0-alpha', protocolVersion: 4, totalModuleCount: 0, validModuleCount: 0, runningModuleCount: 0, generatedAt: new Date().toISOString() }
  const modules = useModuleStore(); modules.status = options.status ?? 'ready'; modules.modules = options.items ?? []
  const getSnapshot = options.getSnapshot ?? vi.fn(async () => ({ generatedAt: new Date().toISOString(), modules: [] }))
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/', component: HomePage }, { path: '/modules', component: { template: '<div />' } }, { path: '/running', component: { template: '<div />' } }, { path: '/settings', component: { template: '<div />' } }] })
  await router.push('/'); await router.isReady()
  const wrapper = mount(HomePage, { global: { plugins: [pinia, router], provide: { moduleClient: { getSnapshot } } } })
  mounted.push(wrapper)
  return { wrapper, app, modules, router, getSnapshot }
}

describe('HomePage everyday dashboard', () => {
  it('shows environment, host version, and bridge state', async () => { const { wrapper } = await page(); expect(wrapper.text()).toContain('Development workspace'); expect(wrapper.text()).toContain('0.2.0-alpha'); expect(wrapper.text()).toContain('Connected') })
  it('does not claim a healthy bridge while disconnected', async () => { const { wrapper } = await page({ bridge: 'Connecting' }); expect(wrapper.text()).toContain('Connecting'); expect(wrapper.text()).not.toContain('Running normally') })
  it('requests one snapshot for connected idle state', async () => { const getSnapshot = vi.fn(async () => ({ generatedAt: new Date().toISOString(), modules: [] })); await page({ status: 'idle', getSnapshot }); await flushPromises(); expect(getSnapshot).toHaveBeenCalledTimes(1) })
  it.each(['ready', 'loading', 'error'] as const)('does not automatically refresh from %s state', async status => { const getSnapshot = vi.fn(); await page({ status, getSnapshot }); await flushPromises(); expect(getSnapshot).not.toHaveBeenCalled() })
  it('computes all four overview values from the existing module store', async () => {
    const items = [moduleItem(1, { runtimeState: 'Running', isStartupEnabled: true }), moduleItem(2, { errorCount: 1 }), moduleItem(3, { isExecutionBlocked: true, isStartupEnabled: true }), moduleItem(4, { startupAuthorizationState: 'ChangedNeedsConfirmation' })]
    const { wrapper } = await page({ items }); const cards = wrapper.findAll('.dashboard-overview-grid button'); expect(cards.map(card => card.text())).toEqual(expect.arrayContaining(['Total modules4Browse installed modules', 'Running1Review active modules', 'Needs attention3Review module health', 'Starts on launch2Review startup access']))
  })
  it('deduplicates modules appearing in multiple attention categories', async () => { const item = moduleItem(1, { errorCount: 1, isExecutionBlocked: true, startupAuthorizationState: 'ChangedNeedsConfirmation' }); const { wrapper } = await page({ items: [item] }); expect(wrapper.findAll('.dashboard-overview-grid button')[2].text()).toContain('1') })
  it('opens the issues filter and selects the first issue', async () => { const { wrapper, modules, router } = await page({ items: [moduleItem(1, { errorCount: 1 })] }); await wrapper.find('.dashboard-attention-list button').trigger('click'); await flushPromises(); expect(modules.stateFilter).toBe('issues'); expect(modules.selectedModuleId).toBe('module.1'); expect(router.currentRoute.value.path).toBe('/modules') })
  it('opens the first recovery-blocked module without changing global filter semantics', async () => { const { wrapper, modules } = await page({ items: [moduleItem(1, { isExecutionBlocked: true })] }); await wrapper.findAll('.dashboard-attention-list button')[0].trigger('click'); await flushPromises(); expect(modules.stateFilter).toBe('all'); expect(modules.selectedModuleId).toBe('module.1') })
  it('opens the first module requiring startup approval', async () => { const { wrapper, modules } = await page({ items: [moduleItem(1, { startupAuthorizationState: 'ChangedNeedsConfirmation' })] }); await wrapper.findAll('.dashboard-attention-list button')[0].trigger('click'); await flushPromises(); expect(modules.selectedModuleId).toBe('module.1') })
  it('shows a truthful ready message when no attention category is present', async () => { const { wrapper } = await page({ items: [moduleItem(1)] }); expect(wrapper.text()).toContain('Everything looks ready.'); expect(wrapper.text()).toContain('No module issues currently need your attention.') })
  it('shows at most three running modules', async () => { const items = Array.from({ length: 4 }, (_, index) => moduleItem(index + 1, { runtimeState: 'Running' })); const { wrapper } = await page({ items }); expect(wrapper.findAll('.dashboard-running-list article')).toHaveLength(3) })
  it('selects a running module before navigating to details', async () => { const { wrapper, modules, router } = await page({ items: [moduleItem(1, { runtimeState: 'Running' })] }); await wrapper.get('.dashboard-running-list article>button').trigger('click'); await flushPromises(); expect(modules.selectedModuleId).toBe('module.1'); expect(router.currentRoute.value.path).toBe('/modules') })
  it('links the full running view correctly', async () => { const { wrapper } = await page({ items: [moduleItem(1, { runtimeState: 'Running' })] }); expect(wrapper.get('.dashboard-running header a').attributes('href')).toContain('/running') })
  it('shows an actionable empty running state', async () => { const { wrapper } = await page(); expect(wrapper.text()).toContain('No modules are running.'); expect(wrapper.text()).toContain('Browse modules') })
  it('provides only the three existing quick destinations', async () => { const { wrapper } = await page(); const links = wrapper.findAll('.dashboard-destinations a'); expect(links).toHaveLength(3); expect(links.map(link => link.attributes('href'))).toEqual(expect.arrayContaining(['/modules', '/running', '/settings'])) })
  it('does not expose lifecycle or startup controls on Home', async () => { const { wrapper } = await page({ items: [moduleItem(1, { runtimeState: 'Running' })] }); const text = wrapper.text(); expect(text).not.toMatch(/\b(Load|Activate|Deactivate|Unload|Open)\b/); expect(wrapper.find('[role="switch"]').exists()).toBe(false) })
  it('keeps an old snapshot visible and marks it stale after refresh failure', async () => { const { wrapper } = await page({ status: 'error', items: [moduleItem(1)] }); expect(wrapper.text()).toContain('last available snapshot'); expect(wrapper.text()).toContain('Total modules'); expect(wrapper.text()).not.toContain('Workspace overview is unavailable') })
  it('shows a full retry state when refresh fails without data', async () => { const getSnapshot = vi.fn(async () => ({ generatedAt: new Date().toISOString(), modules: [] })); const { wrapper } = await page({ status: 'error', getSnapshot }); expect(wrapper.text()).toContain('Workspace overview is unavailable'); await wrapper.get('.dashboard-full-error button').trigger('click'); await flushPromises(); expect(getSnapshot).toHaveBeenCalledTimes(1) })
  it('uses semantic buttons and links for every dashboard interaction', async () => { const { wrapper } = await page({ items: [moduleItem(1, { errorCount: 1, runtimeState: 'Running' })] }); expect(wrapper.findAll('.dashboard-overview-grid button').length).toBe(4); expect(wrapper.findAll('.dashboard-destinations a').length).toBe(3); expect(wrapper.findAll('[tabindex]:not(button):not(a)').length).toBe(0) })
})
