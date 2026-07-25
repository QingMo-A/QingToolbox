import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import SettingsPage from './SettingsPage.vue'
import { router } from '../app/router'
import { useAppStore } from '../app/store'
import { useSettingsStore } from '../app/settingsStore'
import { useThemeStore } from '../app/themeStore'
import { useToastStore } from '../app/toastStore'
import type { SettingsSnapshot } from '../contracts/settings'

const wrappers: VueWrapper[] = []
afterEach(() => wrappers.splice(0).forEach(wrapper => wrapper.unmount()))

const snapshot: SettingsSnapshot = {
  generatedAt: '2026-07-25T12:00:00Z',
  language: { code: 'en-US', displayName: 'English' },
  showLogsInSidebar: true,
  mainWindowCloseBehavior: 'Ask',
  closeBehaviorMessage: 'Ask before closing.',
  launchAtLogin: false,
  canConfigureLaunchAtLogin: true,
  startupPresentationMode: 'FloatingBadge' as const,
  startupBackend: 'Registry Run',
  startupStatus: 'Healthy',
  startupMessage: 'Registration is healthy.'
}

function page(
  bridge: 'Connecting' | 'Connected' = 'Connected',
  status: 'idle' | 'loading' | 'ready' | 'error' = 'idle',
  setImpl?: (value: boolean) => Promise<typeof snapshot>,
  closeImpl?: (value: 'Ask'|'MinimizeToNotificationArea'|'ExitApplication') => Promise<typeof snapshot>
) {
  const pinia = createPinia()
  setActivePinia(pinia)
  useAppStore().bridge = bridge
  const settings = useSettingsStore()
  settings.status = status
  if (status === 'ready') settings.complete(snapshot)
  if (status === 'error') settings.fail(new Error('offline'))
  const getSnapshot = vi.fn(async () => snapshot)
  const setShowLogsInSidebar = vi.fn(setImpl ?? (async value => ({ ...snapshot, showLogsInSidebar: value })))
  const setMainWindowCloseBehavior = vi.fn(closeImpl ?? (async value => ({ ...snapshot, mainWindowCloseBehavior: value })))
  const wrapper = mount(SettingsPage, {
    global: { plugins: [pinia], provide: { settingsClient: { getSnapshot, setShowLogsInSidebar, setMainWindowCloseBehavior } } }
  })
  wrappers.push(wrapper)
  return { wrapper, settings, getSnapshot, setShowLogsInSidebar, setMainWindowCloseBehavior, theme: useThemeStore(), toast: useToastStore() }
}

describe('SettingsPage', () => {
  it('is registered at /settings and keeps /logs directly accessible', () => {
    expect(router.resolve('/settings').matched).toHaveLength(1)
    expect(router.resolve('/logs').matched).toHaveLength(1)
  })
  it('shows connecting state', () => expect(page('Connecting').wrapper.text()).toContain('Connecting to the host'))
  it('shows compact loading skeletons', () => expect(page('Connected', 'loading').wrapper.findAll('.q-skeleton')).toHaveLength(4))
  it('loads once from idle and reuses ready state', async () => {
    const idle = page()
    await flushPromises()
    expect(idle.getSnapshot).toHaveBeenCalledTimes(1)
    idle.wrapper.unmount()
    const ready = page('Connected', 'ready')
    await flushPromises()
    expect(ready.getSnapshot).not.toHaveBeenCalled()
  })
  it('renders host language close and startup values', () => {
    const text = page('Connected', 'ready').wrapper.text()
    expect(text).toContain('English')
    expect(text).toContain('Ask')
    expect(text).toContain('Registry Run')
    expect(text).toContain('Healthy')
  })
  it('keeps language and startup read-only while exposing only the two supported controls', () => {
    const wrapper = page('Connected', 'ready').wrapper
    expect(wrapper.text()).toContain('Some host settings can be changed')
    expect(wrapper.findAll('button').map(button => button.text())).not.toEqual(expect.arrayContaining(['Save', 'Apply', 'Reset']))
    expect(wrapper.find('select').exists()).toBe(false)
    expect(wrapper.find('input').exists()).toBe(false)
    expect(wrapper.findAll('[role="switch"]')).toHaveLength(1)
    expect(wrapper.findAll('[role="radiogroup"]')).toHaveLength(1)
    expect(wrapper.findAll('[role="radio"]')).toHaveLength(3)
  })
  it('maps the current close behavior to one accessible radio', () => {
    const radios = page('Connected', 'ready').wrapper.findAll('[role="radio"]')
    expect(radios.map(radio => radio.attributes('aria-checked'))).toEqual(['true', 'false', 'false'])
    expect(radios.map(radio => radio.text())).toEqual(expect.arrayContaining([expect.stringContaining('Ask every time'), expect.stringContaining('Minimize to notification area'), expect.stringContaining('Exit application')]))
  })
  it('persists a close behavior without closing the page', async () => {
    const x = page('Connected', 'ready')
    await x.wrapper.findAll('[role="radio"]')[2].trigger('click')
    await flushPromises()
    expect(x.setMainWindowCloseBehavior).toHaveBeenCalledOnce()
    expect(x.setMainWindowCloseBehavior).toHaveBeenCalledWith('ExitApplication')
    expect(x.settings.snapshot?.mainWindowCloseBehavior).toBe('ExitApplication')
    expect(x.wrapper.exists()).toBe(true)
    expect(x.toast.kind).toBe('success')
  })
  it('preserves close behavior on failure', async () => {
    const x = page('Connected', 'ready', undefined, async () => { throw new Error('denied') })
    await x.wrapper.findAll('[role="radio"]')[1].trigger('click')
    await flushPromises()
    expect(x.settings.snapshot?.mainWindowCloseBehavior).toBe('Ask')
    expect(x.settings.closeBehaviorError).toBe('denied')
    expect(x.toast.kind).toBe('error')
  })
  it('disables only close choices and suppresses parallel close writes', async () => {
    let resolve!: (value: typeof snapshot) => void
    const pending = new Promise<typeof snapshot>(done => { resolve = done })
    const x = page('Connected', 'ready', undefined, () => pending)
    const radios = x.wrapper.findAll('[role="radio"]')
    await radios[1].trigger('click')
    await radios[2].trigger('click')
    expect(x.setMainWindowCloseBehavior).toHaveBeenCalledTimes(1)
    expect(radios.every(radio => radio.attributes('disabled') !== undefined)).toBe(true)
    expect(x.wrapper.get('[role="switch"]').attributes('disabled')).toBeUndefined()
    resolve({ ...snapshot, mainWindowCloseBehavior: 'MinimizeToNotificationArea' })
    await flushPromises()
    expect(x.wrapper.findAll('[role="radio"]').every(radio => radio.attributes('disabled') === undefined)).toBe(true)
  })
  it('persists the logs switch and exposes its accessible state', async () => {
    const x = page('Connected', 'ready')
    const control = x.wrapper.get('[role="switch"]')
    expect(control.attributes('aria-checked')).toBe('true')
    await control.trigger('click')
    await flushPromises()
    expect(x.setShowLogsInSidebar).toHaveBeenCalledOnce()
    expect(x.setShowLogsInSidebar).toHaveBeenCalledWith(false)
    expect(x.settings.snapshot?.showLogsInSidebar).toBe(false)
    expect(control.attributes('aria-checked')).toBe('false')
    expect(x.toast.kind).toBe('success')
  })
  it('preserves host state and reports a failed mutation', async () => {
    const x = page('Connected', 'ready', async () => { throw new Error('denied') })
    await x.wrapper.get('[role="switch"]').trigger('click')
    await flushPromises()
    expect(x.settings.snapshot?.showLogsInSidebar).toBe(true)
    expect(x.settings.logsVisibilityError).toBe('denied')
    expect(x.wrapper.text()).toContain('preference was not changed')
    expect(x.toast.kind).toBe('error')
  })
  it('disables the switch and suppresses parallel writes while saving', async () => {
    let resolve!: (value: typeof snapshot) => void
    const pending = new Promise<typeof snapshot>(done => { resolve = done })
    const x = page('Connected', 'ready', () => pending)
    const control = x.wrapper.get('[role="switch"]')
    await control.trigger('click')
    await control.trigger('click')
    expect(x.setShowLogsInSidebar).toHaveBeenCalledTimes(1)
    expect(control.attributes('disabled')).toBeDefined()
    resolve({ ...snapshot, showLogsInSidebar: false })
    await flushPromises()
    expect(control.attributes('disabled')).toBeUndefined()
  })
  it('retries an error', async () => {
    const x = page('Connected', 'error')
    await x.wrapper.get('.settings-state button').trigger('click')
    await flushPromises()
    expect(x.getSnapshot).toHaveBeenCalledTimes(1)
  })
  it('refreshes a ready snapshot on demand', async () => {
    const x = page('Connected', 'ready')
    await x.wrapper.get('.settings-header button').trigger('click')
    await flushPromises()
    expect(x.getSnapshot).toHaveBeenCalledTimes(1)
  })
  it('changes only the local theme preview', async () => {
    const x = page('Connected', 'ready')
    const buttons = x.wrapper.findAll('.settings-theme button')
    await buttons[2].trigger('click')
    expect(x.theme.mode).toBe('dark')
    expect(x.getSnapshot).not.toHaveBeenCalled()
    expect(x.setShowLogsInSidebar).not.toHaveBeenCalled()
    expect(x.setMainWindowCloseBehavior).not.toHaveBeenCalled()
  })
  it('renders long state through definition rows', () => expect(page('Connected', 'ready').wrapper.findAll('.settings-values > div').length).toBeGreaterThanOrEqual(8))
})
