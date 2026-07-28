import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import QSidebarLayout from './QSidebarLayout.vue'
import { useSettingsStore } from '../../app/settingsStore'

const snapshot=(showLogsInSidebar:boolean)=>({generatedAt:new Date().toISOString(),language:{code:'en-US' as const,effectiveCode:'en-US' as const,displayName:'English',options:[{code:'system' as const,displayName:'System Default',nativeName:'跟随系统'},{code:'zh-CN' as const,displayName:'Simplified Chinese',nativeName:'简体中文'},{code:'en-US' as const,displayName:'English',nativeName:'English'}]},showLogsInSidebar,mainWindowCloseBehavior:'Ask' as const,closeBehaviorMessage:'',launchAtLogin:false,canConfigureLaunchAtLogin:true,canRepairStartup:false,startupPresentationMode:'FloatingBadge' as const,startupBackend:'None',startupStatus:'Unavailable',startupMessage:''})

describe('QSidebarLayout icons', () => {
  it('uses the native Fluent navigation glyphs and the product brand mark', () => {
    const pinia=createPinia();setActivePinia(pinia)
    const wrapper = mount(QSidebarLayout, { global: { plugins:[pinia],stubs: { RouterLink: { template: '<a><slot/></a>' } } } })
    expect(wrapper.findAll('nav .q-fluent-icon').length).toBeGreaterThanOrEqual(4)
    expect(wrapper.get('.q-brand img').attributes('src')).toContain('data:image/svg+xml')
    expect(wrapper.text()).toContain('Running')
    expect(wrapper.text()).toContain('Settings')
    expect(wrapper.text()).not.toMatch(/[⌂▦⌁⌖]/)
    expect(wrapper.get('[aria-label="Pin sidebar"]').attributes('title')).toBe('Pin sidebar')
  })
  it('shows Quick open with the Search icon and emits its explicit event',async()=>{const pinia=createPinia();setActivePinia(pinia);const wrapper=mount(QSidebarLayout,{global:{plugins:[pinia],stubs:{RouterLink:{template:'<a><slot/></a>'}}}});const quick=wrapper.get('[aria-label="Quick open"]');expect(quick.attributes('title')).toContain('Ctrl+K');expect(quick.find('.q-icon').exists()).toBe(true);await quick.trigger('click');expect(wrapper.emitted('openCommandPalette')).toHaveLength(1)})
  it('uses the host preference only after a ready snapshot',async()=>{const pinia=createPinia();setActivePinia(pinia);const store=useSettingsStore();const wrapper=mount(QSidebarLayout,{global:{plugins:[pinia],stubs:{RouterLink:{template:'<a><slot/></a>'}}}});expect(wrapper.text()).toContain('Logs');store.complete(snapshot(false));await wrapper.vm.$nextTick();expect(wrapper.text()).not.toContain('Logs');store.complete(snapshot(true));await wrapper.vm.$nextTick();expect(wrapper.text()).toContain('Logs')})
  it('keeps Logs visible when the settings request fails',async()=>{const pinia=createPinia();setActivePinia(pinia);const store=useSettingsStore();store.fail(new Error('offline'));const wrapper=mount(QSidebarLayout,{global:{plugins:[pinia],stubs:{RouterLink:{template:'<a><slot/></a>'}}}});expect(wrapper.text()).toContain('Logs')})
})
