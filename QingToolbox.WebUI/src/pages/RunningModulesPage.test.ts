import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import RunningModulesPage from './RunningModulesPage.vue'
import { router as appRouter } from '../app/router'
import { useAppStore } from '../app/store'
import { useModuleStore } from '../app/moduleStore'

const mounted: VueWrapper[] = []
afterEach(() => mounted.splice(0).forEach(wrapper => wrapper.unmount()))

const item = (id: string, runtimeState: string) => ({ id, displayName: id, displayDescription: `${id} description`, version: '1.0.0', author: 'Qing', runtimeType: 'OutOfProcess', loadMode: 'Manual', runtimeState, isValid: true, errorCount: 0, errors: [], permissions: [], minimumHostVersion: '0.2', isUserInstalled: true, canLoad: false, canActivate: false, canOpen: runtimeState === 'Running', isBusy: false, isExecutionBlocked: false })
const snapshot = { generatedAt: new Date().toISOString(), modules: [item('Running module', 'Running'), item('Loaded module', 'Loaded'), item('Waiting module', 'NotLoaded'), item('Failed module', 'Failed')] }

async function page(status: 'idle'|'loading'|'ready'|'error' = 'ready') {
  const pinia = createPinia()
  setActivePinia(pinia)
  useAppStore().bridge = 'Connected'
  const modules = useModuleStore()
  modules.status = status
  if (status === 'ready') modules.complete(snapshot)
  if (status === 'error') modules.fail(new Error('offline'))
  const getSnapshot = vi.fn(async () => snapshot)
  const open = vi.fn(async () => snapshot)
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/running', component: RunningModulesPage }, { path: '/modules', component: { template: '<div>Modules</div>' } }] })
  await router.push('/running')
  await router.isReady()
  const wrapper = mount(RunningModulesPage, { global: { plugins: [pinia, router], provide: { moduleClient: { getSnapshot, open } } } })
  mounted.push(wrapper)
  return { wrapper, modules, router, getSnapshot, open }
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
    expect(wrapper.text()).not.toMatch(/Deactivate|Unload|Remove/)
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

  it('uses a native focusable button for keyboard detail navigation', async () => {
    const { wrapper } = await page()
    const button = wrapper.findAll('.running-module-action button').find(item => item.text() === 'View details')!
    expect(button.element.tagName).toBe('BUTTON')
    expect(button.attributes('disabled')).toBeUndefined()
    expect(button.attributes('tabindex')).not.toBe('-1')
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
    await failed.wrapper.get('button').trigger('click')
    await flushPromises()
    expect(failed.getSnapshot).toHaveBeenCalledTimes(1)
  })
})
