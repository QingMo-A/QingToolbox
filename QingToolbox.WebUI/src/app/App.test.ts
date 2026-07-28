import { afterEach, describe, expect, it, vi } from 'vitest'
import { mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import App from './App.vue'
import { useSettingsStore } from './settingsStore'
import type { EffectiveLanguageCode, LanguageCode, SettingsSnapshot } from '../contracts/settings'

const wrappers: VueWrapper[] = []
const originalTitle = document.title
const originalLanguage = document.documentElement.lang
afterEach(() => {
  wrappers.splice(0).forEach(wrapper => wrapper.unmount())
  document.title = originalTitle
  document.documentElement.lang = originalLanguage
})

const snapshot = (code: LanguageCode, effectiveCode: EffectiveLanguageCode): SettingsSnapshot => ({
  generatedAt: new Date().toISOString(), language: { code, effectiveCode, displayName: code, options: [
    { code: 'system', displayName: 'System Default', nativeName: '跟随系统' }, { code: 'zh-CN', displayName: 'Simplified Chinese', nativeName: '简体中文' }, { code: 'en-US', displayName: 'English', nativeName: 'English' },
  ] }, showLogsInSidebar: true, mainWindowCloseBehavior: 'Ask', closeBehaviorMessage: '', launchAtLogin: false,
  canConfigureLaunchAtLogin: true, canRepairStartup: false, startupPresentationMode: 'FloatingBadge', startupBackend: 'None', startupStatus: 'Unavailable', startupMessage: '',
})

function app() {
  const pinia = createPinia(); setActivePinia(pinia)
  const router = createRouter({ history: createMemoryHistory(), routes: [
    { path: '/', component: { template: '<div />' } }, { path: '/modules', component: { template: '<div />' } },
  ] })
  const preventDefault = vi.fn()
  const getSnapshot = vi.fn()
  const wrapper = mount(App, {
    global: {
      plugins: [pinia, router],
      provide: { settingsClient: { getSnapshot } },
      stubs: {
        RouterView: { template: '<div />' },
        QToast: { template: '<div />' },
        QSidebarLayout: { emits: ['openCommandPalette'], template: '<button class="open" @click="$emit(\'openCommandPalette\')">Open</button><slot />' },
        QCommandPalette: { props: ['open'], emits: ['close'], template: '<div class="palette" :data-open="open"><button @click="$emit(\'close\')">Close</button></div>' },
      },
    },
  })
  wrappers.push(wrapper)
  return { wrapper, preventDefault, router, getSnapshot, settings: useSettingsStore() }
}

describe('App Quick Open integration', () => {
  it.each([
    { ctrlKey: true, metaKey: false },
    { ctrlKey: false, metaKey: true },
  ])('opens with the platform shortcut', async modifiers => {
    const { wrapper, preventDefault } = app()
    window.dispatchEvent(Object.assign(new Event('keydown'), { ...modifiers, altKey: false, key: 'K', preventDefault }))
    await wrapper.vm.$nextTick()
    expect(preventDefault).toHaveBeenCalled()
    expect(wrapper.get('.palette').attributes('data-open')).toBe('true')
  })

  it('ignores plain K and Alt+Ctrl+K', async () => {
    const { wrapper } = app()
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'k' }))
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'k', ctrlKey: true, altKey: true }))
    await wrapper.vm.$nextTick()
    expect(wrapper.get('.palette').attributes('data-open')).toBe('false')
  })

  it('opens from the sidebar and closes from the palette', async () => {
    const { wrapper } = app()
    await wrapper.get('.open').trigger('click')
    expect(wrapper.get('.palette').attributes('data-open')).toBe('true')
    await wrapper.get('.palette button').trigger('click')
    expect(wrapper.get('.palette').attributes('data-open')).toBe('false')
  })

  it('removes the global listener when unmounted', () => {
    const remove = vi.spyOn(window, 'removeEventListener')
    const { wrapper } = app()
    wrapper.unmount()
    expect(remove).toHaveBeenCalledWith('keydown', expect.any(Function))
    remove.mockRestore()
  })

  it('updates html language and the current route title reactively without requesting settings', async () => {
    const { wrapper, router, getSnapshot, settings } = app()
    expect(document.documentElement.lang).toBe('en-US')
    expect(document.title).toBe('Home · QingToolbox')
    settings.complete(snapshot('system', 'zh-CN')); await wrapper.vm.$nextTick()
    expect(document.documentElement.lang).toBe('zh-CN')
    expect(document.title).toBe('首页 · QingToolbox')
    await router.push('/modules'); await wrapper.vm.$nextTick()
    expect(document.title).toBe('模块 · QingToolbox')
    settings.complete(snapshot('en-US', 'en-US')); await wrapper.vm.$nextTick()
    expect(document.title).toBe('Modules · QingToolbox')
    expect(getSnapshot).not.toHaveBeenCalled()
  })
})
