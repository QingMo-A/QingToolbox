import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import QSidebarLayout from './QSidebarLayout.vue'
import { useSettingsStore } from '../../app/settingsStore'

const snapshot=(showLogsInSidebar:boolean)=>({generatedAt:new Date().toISOString(),language:{code:'en-US',displayName:'English'},showLogsInSidebar,mainWindowCloseBehavior:'Ask' as const,closeBehaviorMessage:'',launchAtLogin:false,canConfigureLaunchAtLogin:true,canRepairStartup:false,startupPresentationMode:'FloatingBadge' as const,startupBackend:'None',startupStatus:'Unavailable',startupMessage:''})

describe('QSidebarLayout icons', () => {
  it('uses the native Fluent navigation glyphs and the product brand mark', () => {
    const pinia=createPinia();setActivePinia(pinia)
    const wrapper = mount(QSidebarLayout, { global: { plugins:[pinia],stubs: { RouterLink: { template: '<a><slot/></a>' } } } })
    expect(wrapper.findAll('nav .q-fluent-icon').length).toBeGreaterThanOrEqual(4)
    expect(wrapper.get('.q-brand img').attributes('src')).toContain('data:image/svg+xml')
    expect(wrapper.text()).toContain('Running')
    expect(wrapper.text()).toContain('Settings')
    expect(wrapper.text()).not.toMatch(/[⌂▦⌁⌖]/)
    expect(wrapper.get('button').attributes('aria-label')).toBe('Pin sidebar')
    expect(wrapper.get('button').attributes('title')).toBe('Pin sidebar')
  })
  it('uses the host preference only after a ready snapshot',async()=>{const pinia=createPinia();setActivePinia(pinia);const store=useSettingsStore();const wrapper=mount(QSidebarLayout,{global:{plugins:[pinia],stubs:{RouterLink:{template:'<a><slot/></a>'}}}});expect(wrapper.text()).toContain('Logs');store.complete(snapshot(false));await wrapper.vm.$nextTick();expect(wrapper.text()).not.toContain('Logs');store.complete(snapshot(true));await wrapper.vm.$nextTick();expect(wrapper.text()).toContain('Logs')})
  it('keeps Logs visible when the settings request fails',async()=>{const pinia=createPinia();setActivePinia(pinia);const store=useSettingsStore();store.fail(new Error('offline'));const wrapper=mount(QSidebarLayout,{global:{plugins:[pinia],stubs:{RouterLink:{template:'<a><slot/></a>'}}}});expect(wrapper.text()).toContain('Logs')})
})
