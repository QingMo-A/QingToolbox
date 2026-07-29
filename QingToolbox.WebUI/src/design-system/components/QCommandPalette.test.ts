import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import QCommandPalette from './QCommandPalette.vue'
import QIcon from './QIcon.vue'
import { useModuleStore } from '../../app/moduleStore'
import type { ModuleSnapshotItem } from '../../contracts/modules'
import { useSettingsStore } from '../../app/settingsStore'
import type { EffectiveLanguageCode, LanguageCode, SettingsSnapshot } from '../../contracts/settings'

const wrappers: VueWrapper[] = []
const scrollIntoView = vi.fn()
const moduleItem = (id: string, state = 'NotLoaded'): ModuleSnapshotItem => ({
  id, displayName: id === 'qing.alpha' ? 'Alpha Tools' : 'Beta Tools',
  displayDescription: id === 'qing.alpha' ? 'Format useful text' : 'Monitor a server',
  version: '1.0.0', author: id === 'qing.alpha' ? 'QingMo-A' : 'Example Author',
  runtimeType: 'OutOfProcess', loadMode: 'Manual', runtimeState: state,
  isValid: true, errorCount: 0, errors: [], permissions: [], minimumHostVersion: '0.2.0-alpha',
  isUserInstalled: true, canRemove: true, canLoad: true, canActivate: false, canOpen: false, canDeactivate: false,
  canUnload: false, isBusy: false, isExecutionBlocked: false, isStartupEnabled: false,
  startupAuthorizationState: 'NotEnabled', canChangeStartupAuthorization: true, isStartupAuthorizationBusy: false, updateStatus: 'NotChecked', targetVersion: null, releaseNotes: null, isFromStaleCache: false, canCheckForUpdate: true, isUpdateCheckBusy: false, canDownloadUpdate: false, downloadStatus: 'NotDownloaded', isDownloadActive: false, downloadBytesReceived: 0, downloadExpectedBytes: 0, canInstallVerifiedUpdate: false,
})
const settingsSnapshot = (code: LanguageCode, effectiveCode: EffectiveLanguageCode): SettingsSnapshot => ({
  generatedAt: new Date().toISOString(), language: { code, effectiveCode, displayName: code, options: [
    { code: 'system', displayName: 'System Default', nativeName: '跟随系统' }, { code: 'zh-CN', displayName: 'Simplified Chinese', nativeName: '简体中文' }, { code: 'en-US', displayName: 'English', nativeName: 'English' },
  ] }, showLogsInSidebar: true, mainWindowCloseBehavior: 'Ask', closeBehaviorMessage: '', launchAtLogin: false,
  canConfigureLaunchAtLogin: true, canRepairStartup: false, startupPresentationMode: 'FloatingBadge', startupBackend: 'None', startupStatus: 'Unavailable', startupMessage: '',
})

beforeEach(() => {
  scrollIntoView.mockReset()
  Object.defineProperty(Element.prototype, 'scrollIntoView', {
    configurable: true,
    value: scrollIntoView,
  })
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
  return { wrapper, store, router, settings: useSettingsStore() }
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

  it('exposes one combobox controlling a listbox and a real active option', async () => {
    const { wrapper } = await palette()
    const input = wrapper.get('input')
    expect(input.attributes('role')).toBe('combobox')
    expect(input.attributes('aria-autocomplete')).toBe('list')
    expect(input.attributes('aria-controls')).toBe('quick-open-results')
    expect(wrapper.get('#quick-open-results').attributes('role')).toBe('listbox')
    const activeId = input.attributes('aria-activedescendant')
    expect(activeId).toBe('quick-open-result-0')
    expect(wrapper.findAll(`#${activeId}`)).toHaveLength(1)
    expect(wrapper.get(`#${activeId}`).attributes('role')).toBe('option')
  })

  it('uses the corresponding navigation icon for each page result', async () => {
    const { wrapper } = await palette()
    const iconNames = wrapper.findAll('section')[0]
      .findAllComponents(QIcon)
      .map((icon: VueWrapper) => (icon.props() as { name: string }).name)
    expect(iconNames).toEqual(['home', 'modules', 'running', 'logs', 'settings', 'diagnostics'])
    expect(new Set(iconNames).size).toBe(6)
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
    expect(wrapper.get('input').attributes('aria-activedescendant')).toBeUndefined()
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

  it('reveals keyboard selections without moving focus away from search', async () => {
    const { wrapper } = await palette()
    const input = wrapper.get('input')
    scrollIntoView.mockClear()

    await input.trigger('keydown', { key: 'ArrowDown' })
    await flushPromises()
    expect(scrollIntoView).toHaveBeenLastCalledWith({ block: 'nearest' })
    expect(input.attributes('aria-activedescendant')).toBe('quick-open-result-1')
    expect(document.activeElement).toBe(input.element)

    await input.trigger('keydown', { key: 'ArrowUp' })
    await flushPromises()
    expect(input.attributes('aria-activedescendant')).toBe('quick-open-result-0')
    expect(scrollIntoView).toHaveBeenLastCalledWith({ block: 'nearest' })
    expect(document.activeElement).toBe(input.element)
  })

  it('reveals wrapped first and last results and query-reset selections', async () => {
    const { wrapper } = await palette()
    const input = wrapper.get('input')
    scrollIntoView.mockClear()

    await input.trigger('keydown', { key: 'ArrowUp' })
    await flushPromises()
    expect(input.attributes('aria-activedescendant')).toBe('quick-open-result-6')

    await input.trigger('keydown', { key: 'ArrowDown' })
    await flushPromises()
    expect(input.attributes('aria-activedescendant')).toBe('quick-open-result-0')

    await input.setValue('settings')
    await flushPromises()
    expect(input.attributes('aria-activedescendant')).toBe('quick-open-result-0')
    expect(scrollIntoView).toHaveBeenLastCalledWith({ block: 'nearest' })
  })

  it('does not reveal an option when the query has no results', async () => {
    const { wrapper } = await palette()
    scrollIntoView.mockClear()
    await wrapper.get('input').setValue('nothing-matches-this')
    await flushPromises()
    expect(scrollIntoView).not.toHaveBeenCalled()
  })

  it('closes through Escape, the explicit button, and prop synchronization', async () => {
    const { wrapper } = await palette()
    await wrapper.get('dialog').trigger('cancel')
    expect(wrapper.emitted('close')).toBeTruthy()
    await wrapper.setProps({ open: false }); await flushPromises()
    await wrapper.setProps({ open: true }); await flushPromises()
    expect(wrapper.get('input').attributes('aria-activedescendant')).toBe('quick-open-result-0')
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

  it('shows all six localized page titles and descriptions and updates while open', async () => {
    const { wrapper, settings } = await palette()
    expect(wrapper.text()).toContain('Home'); expect(wrapper.text()).toContain('Development diagnostics')
    settings.complete(settingsSnapshot('system', 'zh-CN')); await flushPromises()
    for (const value of ['首页','模块','运行中模块','会话日志','设置','开发诊断','浏览和管理已安装模块']) expect(wrapper.text()).toContain(value)
    expect(wrapper.get('input').attributes('aria-label')).toBe('搜索页面和模块')
    expect(wrapper.get('input').attributes('placeholder')).toBe('搜索页面和模块')
    expect(wrapper.get('#quick-open-results').attributes('aria-label')).toBe('快速打开结果')
    expect(wrapper.get('[aria-label="关闭快速打开"]').element.tagName).toBe('BUTTON')
    expect(wrapper.text()).toContain('↑↓ 导航'); expect(wrapper.text()).toContain('Enter 打开'); expect(wrapper.text()).toContain('Esc 关闭')
  })

  it.each([
    ['模块', '模块'],
    ['管理', '模块'],
    ['Modules', '模块'],
    ['installed', '模块'],
    ['/modules', '模块'],
  ])('finds the localized Modules page with %s', async (query, expected) => {
    const { wrapper, settings } = await palette(); settings.complete(settingsSnapshot('zh-CN', 'zh-CN')); await flushPromises()
    await wrapper.get('input').setValue(query); expect(wrapper.text()).toContain(expected)
  })

  it('keeps module metadata and runtime state host-authored in Chinese mode', async () => {
    const { wrapper, settings } = await palette(); settings.complete(settingsSnapshot('zh-CN', 'zh-CN')); await flushPromises()
    expect(wrapper.text()).toContain('Alpha Tools'); expect(wrapper.text()).toContain('Format useful text'); expect(wrapper.text()).toContain('Running')
  })

  it('localizes group and empty-state labels without changing keyboard behavior', async () => {
    const { wrapper, settings } = await palette(); settings.complete(settingsSnapshot('zh-CN', 'zh-CN')); await flushPromises()
    expect(wrapper.text()).toContain('页面'); expect(wrapper.text()).toContain('当前运行')
    await wrapper.get('input').setValue('nothing-matches-this'); expect(wrapper.text()).toContain('没有结果'); expect(wrapper.text()).toContain('模块 ID')
  })
})
