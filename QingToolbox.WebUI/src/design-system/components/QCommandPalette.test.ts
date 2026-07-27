import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import QCommandPalette from './QCommandPalette.vue'
import { useModuleStore } from '../../app/moduleStore'
import type { ModuleSnapshotItem } from '../../contracts/modules'

const wrappers: VueWrapper[] = []
const moduleItem = (id: string, state = 'NotLoaded'): ModuleSnapshotItem => ({
  id, displayName: id === 'qing.alpha' ? 'Alpha Tools' : 'Beta Tools',
  displayDescription: id === 'qing.alpha' ? 'Format useful text' : 'Monitor a server',
  version: '1.0.0', author: id === 'qing.alpha' ? 'QingMo-A' : 'Example Author',
  runtimeType: 'OutOfProcess', loadMode: 'Manual', runtimeState: state,
  isValid: true, errorCount: 0, errors: [], permissions: [], minimumHostVersion: '0.2.0-alpha',
  isUserInstalled: true, canLoad: true, canActivate: false, canOpen: false, canDeactivate: false,
  canUnload: false, isBusy: false, isExecutionBlocked: false, isStartupEnabled: false,
  startupAuthorizationState: 'NotEnabled', canChangeStartupAuthorization: true, isStartupAuthorizationBusy: false,
})

beforeEach(() => {
  Object.defineProperty(HTMLDialogElement.prototype, 'showModal', {
    configurable: true,
    value: vi.fn(function(this: HTMLDialogElement) { this.setAttribute('open', '') }),
  })
  Object.defineProperty(HTMLDialogElement.prototype, 'close', {
    configurable: true,
    value: vi.fn(function(this: HTMLDialogElement) {
      this.removeAttribute('open')
      this.dispatchEvent(new Event('close'))
    }),
  })
})
afterEach(() => wrappers.splice(0).forEach(wrapper => wrapper.unmount()))

async function palette(open = true) {
  const pinia = createPinia(); setActivePinia(pinia)
  const store = useModuleStore()
  store.modules = [moduleItem('qing.alpha', 'Running'), moduleItem('qing.beta')]
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', component: { template: '<div />' } },
      { path: '/modules', component: { template: '<div />' } },
      { path: '/running', component: { template: '<div />' } },
      { path: '/logs', component: { template: '<div />' } },
      { path: '/settings', component: { template: '<div />' } },
      { path: '/diagnostics', component: { template: '<div />' } },
    ],
  })
  await router.push('/'); await router.isReady()
  const wrapper = mount(QCommandPalette, { attachTo: document.body, props: { open }, global: { plugins: [pinia, router] } })
  wrappers.push(wrapper)
  await flushPromises()
  return { wrapper, store, router }
}

describe('QCommandPalette', () => {
  it('opens one native dialog, focuses search, and exposes six pages plus running modules', async () => {
    const { wrapper } = await palette()
    expect(HTMLDialogElement.prototype.showModal).toHaveBeenCalledTimes(1)
    expect(wrapper.get('dialog').attributes('aria-labelledby')).toBe('quick-open-title')
    expect(document.activeElement).toBe(wrapper.get('input').element)
    expect(wrapper.findAll('[role="option"]')).toHaveLength(7)
    expect(wrapper.text()).toContain('Running now')
  })

  it.each([
    ['Modules', 'Browse and manage installed modules'],
    ['alpha tools', 'Alpha Tools'],
    ['FORMAT USEFUL', 'Alpha Tools'],
    ['qing.alpha', 'Alpha Tools'],
    ['qingmo-a', 'Alpha Tools'],
  ])('searches %s without case sensitivity', async (query, expected) => {
    const { wrapper } = await palette()
    await wrapper.get('input').setValue(query)
    expect(wrapper.text()).toContain(expected)
  })

  it('limits module results to eight and preserves store order', async () => {
    const { wrapper, store } = await palette()
    store.modules = Array.from({ length: 10 }, (_, index) => ({ ...moduleItem(`qing.${index}`), displayName: `Tool ${index}`, displayDescription: 'Common match' }))
    await wrapper.get('input').setValue('common')
    const moduleButtons = wrapper.findAll('section')[0].findAll('[role="option"]')
    expect(moduleButtons).toHaveLength(8)
    expect(moduleButtons.map(button => button.text())).toEqual(expect.arrayContaining([expect.stringContaining('Tool 0'), expect.stringContaining('Tool 7')]))
    expect(wrapper.text()).not.toContain('Tool 8')
  })

  it('shows a useful empty state', async () => {
    const { wrapper } = await palette()
    await wrapper.get('input').setValue('nothing-matches-this')
    expect(wrapper.text()).toContain('No results')
    expect(wrapper.text()).toContain('module ID')
  })

  it('navigates pages and closes without requesting data', async () => {
    const { wrapper, router } = await palette()
    await wrapper.get('input').setValue('settings')
    await wrapper.get('[role="option"]').trigger('click')
    await flushPromises()
    expect(router.currentRoute.value.path).toBe('/settings')
    expect(wrapper.emitted('close')).toBeTruthy()
  })

  it('opens module details using only existing store state', async () => {
    const { wrapper, store, router } = await palette()
    store.searchQuery = 'old'; store.stateFilter = 'running'
    await wrapper.get('input').setValue('Alpha Tools')
    await wrapper.get('[role="option"]').trigger('click')
    await flushPromises()
    expect(store.selectedModuleId).toBe('qing.alpha')
    expect(store.searchQuery).toBe('')
    expect(store.stateFilter).toBe('all')
    expect(router.currentRoute.value.path).toBe('/modules')
  })

  it('supports wrapping arrow navigation and Enter selection', async () => {
    const { wrapper, router } = await palette()
    const input = wrapper.get('input')
    await input.setValue('settings')
    await input.trigger('keydown', { key: 'ArrowUp' })
    expect(wrapper.get('[role="option"]').attributes('aria-selected')).toBe('true')
    await input.trigger('keydown', { key: 'ArrowDown' })
    await input.trigger('keydown', { key: 'Enter' })
    await flushPromises()
    expect(router.currentRoute.value.path).toBe('/settings')
  })

  it('closes through Escape, the explicit button, and prop synchronization', async () => {
    const { wrapper } = await palette()
    await wrapper.get('dialog').trigger('cancel')
    expect(wrapper.emitted('close')).toBeTruthy()
    await wrapper.setProps({ open: true }); await flushPromises()
    await wrapper.get('[aria-label="Close Quick Open"]').trigger('click')
    await wrapper.setProps({ open: false }); await flushPromises()
    expect(HTMLDialogElement.prototype.close).toHaveBeenCalled()
  })

  it('resets the active option when the query changes and uses only button results', async () => {
    const { wrapper } = await palette()
    await wrapper.get('input').trigger('keydown', { key: 'ArrowDown' })
    await wrapper.get('input').setValue('modules')
    expect(wrapper.findAll('[role="option"]').every(result => result.element.tagName === 'BUTTON')).toBe(true)
    expect(wrapper.get('[role="option"]').attributes('aria-selected')).toBe('true')
  })
})
