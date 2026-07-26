import { afterEach, describe, expect, it } from 'vitest'
import { mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import HomePage from './HomePage.vue'
import { useAppStore } from '../app/store'
import { useModuleStore } from '../app/moduleStore'

const mounted: VueWrapper[] = []
afterEach(() => mounted.splice(0).forEach(wrapper => wrapper.unmount()))

function moduleItem(index: number, hasIssue = false) {
  return { id: `module.${index}`, displayName: `Module ${index}`, displayDescription: `Description ${index}`, version: '1.0.0', author: 'Qing', runtimeType: 'InProcess', loadMode: 'Manual', runtimeState: 'NotLoaded', isValid: !hasIssue, errorCount: hasIssue ? 1 : 0, errors: hasIssue ? ['Invalid'] : [], permissions: [], minimumHostVersion: '0.2', isUserInstalled: true, canLoad: !hasIssue, canActivate: false, isBusy: false, isExecutionBlocked: false }
}

function page(bridge = 'Connected') {
  const pinia = createPinia()
  setActivePinia(pinia)
  useAppStore().bridge = bridge
  useModuleStore().status = 'loading'
  const wrapper = mount(HomePage, { global: { plugins: [pinia], stubs: { RouterLink: { template: '<a><slot/></a>' } }, provide: { moduleClient: { getSnapshot: async () => ({ generatedAt: new Date().toISOString(), modules: [] }) } } } })
  mounted.push(wrapper)
  return { wrapper, modules: useModuleStore() }
}

describe('HomePage truthful host states', () => {
  it('does not report normal operation while loading', async () => {
    const { wrapper, modules } = page()
    modules.begin()
    await wrapper.vm.$nextTick()
    expect(wrapper.text()).toContain('Checking system status')
    expect(wrapper.text()).not.toContain('Running normally')
  })

  it('reports normal operation only for a healthy ready snapshot', async () => {
    const { wrapper, modules } = page()
    modules.complete({ generatedAt: new Date().toISOString(), modules: [moduleItem(1)] })
    await wrapper.vm.$nextTick()
    expect(wrapper.text()).toContain('Running normally')
  })

  it('reports modules that need attention', async () => {
    const { wrapper, modules } = page()
    modules.complete({ generatedAt: new Date().toISOString(), modules: [moduleItem(1, true)] })
    await wrapper.vm.$nextTick()
    expect(wrapper.text()).toContain('Modules need attention')
  })

  it('reports an unavailable host when the bridge is unavailable', async () => {
    const { wrapper, modules } = page('Connecting')
    modules.complete({ generatedAt: new Date().toISOString(), modules: [moduleItem(1)] })
    await wrapper.vm.$nextTick()
    expect(wrapper.text()).toContain('Host status unavailable')
  })

  it('shows at most five modules with an all-modules entry', async () => {
    const { wrapper, modules } = page()
    modules.complete({ generatedAt: new Date().toISOString(), modules: Array.from({ length: 6 }, (_, index) => moduleItem(index + 1)) })
    await wrapper.vm.$nextTick()
    expect(wrapper.findAll('.wpf-recent > button')).toHaveLength(5)
    expect(wrapper.text()).toContain('Modules overview')
    expect(wrapper.text()).not.toContain('Recent modules')
    expect(wrapper.text()).toContain('View all modules')
  })
})
