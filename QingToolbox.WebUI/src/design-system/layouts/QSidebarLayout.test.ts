import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import QSidebarLayout from './QSidebarLayout.vue'
import { useSettingsStore } from '../../app/settingsStore'
import { useAppStore } from '../../app/store'

const snapshot = (showLogsInSidebar: boolean, effectiveCode: 'en-US'|'zh-CN' = 'en-US', code: 'system'|'en-US'|'zh-CN' = effectiveCode) => ({
  generatedAt: new Date().toISOString(), language: { code, effectiveCode, displayName: code, options: [
    { code: 'system' as const, displayName: 'System Default', nativeName: '跟随系统' },
    { code: 'zh-CN' as const, displayName: 'Simplified Chinese', nativeName: '简体中文' },
    { code: 'en-US' as const, displayName: 'English', nativeName: 'English' },
  ] }, showLogsInSidebar, mainWindowCloseBehavior: 'Ask' as const, closeBehaviorMessage: '', launchAtLogin: false,
  canConfigureLaunchAtLogin: true, canRepairStartup: false, startupPresentationMode: 'FloatingBadge' as const,
  startupBackend: 'None', startupStatus: 'Unavailable', startupMessage: '',
})
const mountSidebar = () => mount(QSidebarLayout, { global: { plugins: [createPinia()], stubs: {
  RouterLink: { props: ['to'], template: '<a :data-to="to"><slot/></a>' },
} } })

describe('QSidebarLayout localization and icons', () => {
  it('uses native Fluent navigation glyphs, the product brand, and English fallback', () => {
    setActivePinia(createPinia()); const wrapper = mountSidebar()
    expect(wrapper.findAll('nav .q-fluent-icon').length).toBeGreaterThanOrEqual(4)
    expect(wrapper.get('.q-brand img').attributes('src')).toContain('data:image/svg+xml')
    expect(wrapper.text()).toContain('Running modules'); expect(wrapper.text()).toContain('Settings')
    expect(wrapper.get('[aria-label="Pin sidebar"]').attributes('title')).toBe('Pin sidebar')
  })

  it('shows localized Quick Open and preserves its event', async () => {
    const pinia = createPinia(); setActivePinia(pinia); const store = useSettingsStore(); const wrapper = mount(QSidebarLayout, { global: { plugins: [pinia], stubs: { RouterLink: { template: '<a><slot/></a>' } } } })
    store.complete(snapshot(true, 'zh-CN', 'system')); await wrapper.vm.$nextTick()
    const quick = wrapper.get('[aria-label="快速打开"]')
    expect(quick.attributes('title')).toBe('快速打开（Ctrl+K）'); expect(quick.find('.q-icon').exists()).toBe(true)
    await quick.trigger('click'); expect(wrapper.emitted('openCommandPalette')).toHaveLength(1)
  })

  it('uses the host Logs preference only after a ready snapshot', async () => {
    const pinia=createPinia(); setActivePinia(pinia); const store=useSettingsStore(); const wrapper=mount(QSidebarLayout,{global:{plugins:[pinia],stubs:{RouterLink:{template:'<a><slot/></a>'}}}})
    expect(wrapper.text()).toContain('Session logs'); store.complete(snapshot(false)); await wrapper.vm.$nextTick(); expect(wrapper.text()).not.toContain('Session logs')
    store.complete(snapshot(true)); await wrapper.vm.$nextTick(); expect(wrapper.text()).toContain('Session logs')
  })

  it('keeps Logs visible when the settings request fails', () => {
    const pinia=createPinia(); setActivePinia(pinia); useSettingsStore().fail(new Error('offline'))
    expect(mount(QSidebarLayout,{global:{plugins:[pinia],stubs:{RouterLink:{template:'<a><slot/></a>'}}}}).text()).toContain('Session logs')
  })

  it('reactively localizes navigation, link titles, workspace, and pin state', async () => {
    const pinia=createPinia(); setActivePinia(pinia); const store=useSettingsStore(); const wrapper=mount(QSidebarLayout,{global:{plugins:[pinia],stubs:{RouterLink:{props:['to'],template:'<a :data-to="to"><slot/></a>'}}}})
    store.complete(snapshot(true,'zh-CN','system')); await wrapper.vm.$nextTick()
    expect(wrapper.text()).toContain('首页'); expect(wrapper.text()).toContain('运行中模块'); expect(wrapper.get('nav').attributes('aria-label')).toBe('工作区')
    expect(wrapper.get('a[data-to="/modules"]').attributes('title')).toBe('模块')
    await wrapper.get('[aria-label="固定侧边栏"]').trigger('click'); expect(wrapper.get('[aria-label="取消固定侧边栏"]').attributes('title')).toBe('取消固定侧边栏')
    store.complete(snapshot(true,'en-US')); await wrapper.vm.$nextTick(); expect(wrapper.text()).toContain('Home'); expect(wrapper.get('nav').attributes('aria-label')).toBe('Workspace')
  })

  it('shows diagnostics only for a Development host', async () => {
    const pinia=createPinia(); setActivePinia(pinia); const app=useAppStore(); const wrapper=mount(QSidebarLayout,{global:{plugins:[pinia],stubs:{RouterLink:{props:['to'],template:'<a :data-to="to"><slot/></a>'}}}})
    expect(wrapper.findAll('a').map(link=>link.attributes('data-to'))).toEqual(['/','/modules','/running','/logs','/settings'])
    app.rebuild({environmentKind:'Development',environmentDisplayName:'Development',hostVersion:'1',protocolVersion:4,totalModuleCount:0,validModuleCount:0,runningModuleCount:0,generatedAt:new Date().toISOString()}); await wrapper.vm.$nextTick()
    expect(wrapper.findAll('a').map(link=>link.attributes('data-to'))).toEqual(['/','/modules','/running','/logs','/diagnostics','/settings'])
  })
})
