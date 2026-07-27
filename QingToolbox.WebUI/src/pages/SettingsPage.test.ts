import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import SettingsPage from './SettingsPage.vue'
import { useAppStore } from '../app/store'
import { useSettingsStore } from '../app/settingsStore'
import { useThemeStore } from '../app/themeStore'
import { useToastStore } from '../app/toastStore'
import type { SettingsSnapshot } from '../contracts/settings'

const wrappers: VueWrapper[] = []
afterEach(() => wrappers.splice(0).forEach(wrapper => wrapper.unmount()))

const snapshot: SettingsSnapshot = { generatedAt: '2026-07-25T12:00:00Z', language: { code: 'en-US', displayName: 'English' }, showLogsInSidebar: true, mainWindowCloseBehavior: 'Ask', closeBehaviorMessage: 'Ask before closing.', launchAtLogin: false, canConfigureLaunchAtLogin: true, startupPresentationMode: 'FloatingBadge', startupBackend: 'Registry Run', startupStatus: 'Healthy', startupMessage: 'Registration is healthy.' }
type PageOptions = {
  bridge?: 'Connecting'|'Connected'|'Unavailable'; status?: 'idle'|'loading'|'ready'|'error'; hasSnapshot?: boolean
  getImpl?: () => Promise<SettingsSnapshot>; logsImpl?: (value: boolean) => Promise<SettingsSnapshot>
  closeImpl?: (value: 'Ask'|'MinimizeToNotificationArea'|'ExitApplication') => Promise<SettingsSnapshot>
  startupImpl?: (value: 'MainWindow'|'Minimized'|'FloatingBadge') => Promise<SettingsSnapshot>
}

function page(options: PageOptions = {}) {
  const pinia = createPinia(); setActivePinia(pinia)
  const app = useAppStore(); app.bridge = options.bridge ?? 'Connected'; app.mode = 'Development'
  app.snapshot = { environmentKind: 'Development', environmentDisplayName: 'Development', hostVersion: '0.2.0-alpha', protocolVersion: 4, totalModuleCount: 0, validModuleCount: 0, runningModuleCount: 0, generatedAt: snapshot.generatedAt }
  const settings = useSettingsStore(); const status = options.status ?? 'ready'; const hasSnapshot = options.hasSnapshot ?? status === 'ready'
  if (hasSnapshot) settings.complete(snapshot)
  if (status === 'loading') settings.begin(); else if (status === 'error') settings.fail(new Error('offline')); else settings.status = status
  const getSnapshot = vi.fn(options.getImpl ?? (async () => snapshot))
  const setShowLogsInSidebar = vi.fn(options.logsImpl ?? (async value => ({ ...snapshot, showLogsInSidebar: value })))
  const setMainWindowCloseBehavior = vi.fn(options.closeImpl ?? (async value => ({ ...snapshot, mainWindowCloseBehavior: value })))
  const setStartupPresentationMode = vi.fn(options.startupImpl ?? (async value => ({ ...snapshot, startupPresentationMode: value })))
  const wrapper = mount(SettingsPage, { global: { plugins: [pinia], provide: { settingsClient: { getSnapshot, setShowLogsInSidebar, setMainWindowCloseBehavior, setStartupPresentationMode } } } })
  wrappers.push(wrapper)
  return { wrapper, app, settings, getSnapshot, setShowLogsInSidebar, setMainWindowCloseBehavior, setStartupPresentationMode, theme: useThemeStore(), toast: useToastStore() }
}

async function openSection(wrapper: VueWrapper, title: string) {
  const button = wrapper.findAll('.settings-section-nav button').find(item => item.text().includes(title))
  if (!button) throw new Error(`Missing settings section: ${title}`)
  await button.trigger('click')
}

describe('SettingsPage information architecture', () => {
  it('shows General by default', () => expect(page().wrapper.get('#settings-general-title').text()).toBe('General'))
  it('provides exactly four native section buttons', () => { const buttons = page().wrapper.findAll('.settings-section-nav button'); expect(buttons).toHaveLength(4); expect(buttons.map(x => x.element.tagName)).toEqual(['BUTTON','BUTTON','BUTTON','BUTTON']); expect(buttons.map(x => x.text())).toEqual(expect.arrayContaining([expect.stringContaining('General'), expect.stringContaining('Window'), expect.stringContaining('Startup'), expect.stringContaining('About')])) })
  it('marks the active section accessibly', async () => { const x = page(); expect(x.wrapper.findAll('.settings-section-nav button')[0].attributes('aria-current')).toBe('page'); await openSection(x.wrapper, 'About'); expect(x.wrapper.findAll('.settings-section-nav button')[3].attributes('aria-current')).toBe('page') })
  it('does not request a snapshot when switching sections', async () => { const x = page(); await openSection(x.wrapper, 'Window'); await openSection(x.wrapper, 'Startup'); await openSection(x.wrapper, 'About'); expect(x.getSnapshot).not.toHaveBeenCalled() })
  it('shows Appearance, Language, and Navigation in General', () => { const text = page().wrapper.text(); expect(text).toContain('Appearance'); expect(text).toContain('Language'); expect(text).toContain('Navigation'); expect(text).toContain('Show logs in sidebar') })
  it('keeps Language read-only and identifies it as a host setting', () => { const wrapper = page().wrapper; expect(wrapper.text()).toContain('Host setting'); expect(wrapper.text()).toContain('English'); expect(wrapper.find('select').exists()).toBe(false); expect(wrapper.find('input').exists()).toBe(false) })
  it('changes Appearance locally without a host request', async () => { const x = page({ bridge: 'Connecting', status: 'idle', hasSnapshot: false }); await x.wrapper.findAll('.settings-theme button')[2].trigger('click'); expect(x.theme.mode).toBe('dark'); expect(x.getSnapshot).not.toHaveBeenCalled(); expect(x.setShowLogsInSidebar).not.toHaveBeenCalled() })
  it('shows three close behavior radios in Window', async () => { const x = page(); await openSection(x.wrapper, 'Window'); expect(x.wrapper.get('[aria-label="Main window close behavior"]').findAll('[role="radio"]')).toHaveLength(3) })
  it('persists close behavior', async () => { const x = page(); await openSection(x.wrapper, 'Window'); await x.wrapper.get('[aria-label="Main window close behavior"]').findAll('[role="radio"]')[2].trigger('click'); await flushPromises(); expect(x.setMainWindowCloseBehavior).toHaveBeenCalledWith('ExitApplication'); expect(x.settings.snapshot?.mainWindowCloseBehavior).toBe('ExitApplication'); expect(x.toast.kind).toBe('success') })
  it('preserves close behavior after failure', async () => { const x = page({ closeImpl: async () => { throw new Error('denied') } }); await openSection(x.wrapper, 'Window'); await x.wrapper.get('[aria-label="Main window close behavior"]').findAll('[role="radio"]')[1].trigger('click'); await flushPromises(); expect(x.settings.snapshot?.mainWindowCloseBehavior).toBe('Ask'); expect(x.wrapper.text()).toContain('close behavior was not changed') })
  it('shows three startup presentation radios', async () => { const x = page(); await openSection(x.wrapper, 'Startup'); expect(x.wrapper.get('[aria-label="Startup presentation mode"]').findAll('[role="radio"]')).toHaveLength(3) })
  it('persists startup presentation', async () => { const x = page(); await openSection(x.wrapper, 'Startup'); await x.wrapper.get('[aria-label="Startup presentation mode"]').findAll('[role="radio"]')[0].trigger('click'); await flushPromises(); expect(x.setStartupPresentationMode).toHaveBeenCalledWith('MainWindow'); expect(x.settings.snapshot?.startupPresentationMode).toBe('MainWindow') })
  it('preserves startup presentation after failure', async () => { const x = page({ startupImpl: async () => { throw new Error('denied') } }); await openSection(x.wrapper, 'Startup'); await x.wrapper.get('[aria-label="Startup presentation mode"]').findAll('[role="radio"]')[0].trigger('click'); await flushPromises(); expect(x.settings.snapshot?.startupPresentationMode).toBe('FloatingBadge'); expect(x.wrapper.text()).toContain('startup presentation preference was not changed') })
  it('shows startup backend and status badge', async () => { const x = page(); await openSection(x.wrapper, 'Startup'); expect(x.wrapper.text()).toContain('Registry Run'); expect(x.wrapper.get('.startup-health-card .q-badge').text()).toBe('Healthy'); expect(x.wrapper.get('.startup-health-card .q-badge').classes()).toContain('is-success') })
  it('refreshes the full snapshot from Refresh status', async () => { const x = page(); await openSection(x.wrapper, 'Startup'); await x.wrapper.get('.startup-health-actions button').trigger('click'); await flushPromises(); expect(x.getSnapshot).toHaveBeenCalledTimes(1) })
  it('does not expose a launch-at-login switch', async () => { const x = page(); await openSection(x.wrapper, 'Startup'); expect(x.wrapper.text()).toContain('Launch at login'); expect(x.wrapper.find('[role="switch"]').exists()).toBe(false) })
  it('shows brand, Preview, host version, environment, and bridge in About', async () => { const x = page(); await openSection(x.wrapper, 'About'); const text = x.wrapper.text(); expect(text).toContain('QingToolbox'); expect(text).toContain('Preview'); expect(text).toContain('0.2.0-alpha'); expect(text).toContain('Development'); expect(text).toContain('Connected'); expect(x.wrapper.find('.settings-about img').exists()).toBe(true) })
  it('keeps About available without a Settings snapshot', async () => { const x = page({ bridge: 'Connecting', status: 'idle', hasSnapshot: false }); await openSection(x.wrapper, 'About'); expect(x.wrapper.text()).toContain('Modular Windows toolbox.'); expect(x.wrapper.text()).toContain('Connecting') })
  it('keeps old content visible while refreshing', () => { const x = page({ status: 'loading', hasSnapshot: true }); expect(x.wrapper.text()).toContain('English'); expect(x.wrapper.text()).toContain('Refreshing host configuration…'); expect(x.wrapper.find('.settings-host-placeholder').exists()).toBe(false) })
  it('keeps old content visible after refresh failure and offers Retry', () => { const x = page({ status: 'error', hasSnapshot: true }); expect(x.wrapper.text()).toContain('English'); expect(x.wrapper.text()).toContain('Settings refresh failed.'); expect(x.wrapper.get('.settings-snapshot-notice button').text()).toBe('Retry') })
  it('retries refresh failure with an old snapshot', async () => { const x = page({ status: 'error', hasSnapshot: true }); await x.wrapper.get('.settings-snapshot-notice button').trigger('click'); await flushPromises(); expect(x.getSnapshot).toHaveBeenCalledTimes(1) })
  it('keeps old content while disconnected', () => { const x = page({ bridge: 'Unavailable', status: 'ready', hasSnapshot: true }); expect(x.wrapper.text()).toContain('English'); expect(x.wrapper.text()).toContain('The host is disconnected. Showing the last available host configuration.') })
  it('disables host writes while disconnected', async () => { const x = page({ bridge: 'Unavailable', status: 'ready', hasSnapshot: true }); expect(x.wrapper.get('[role="switch"]').attributes('disabled')).toBeDefined(); await openSection(x.wrapper, 'Window'); expect(x.wrapper.findAll('[role="radio"]').every(radio => radio.attributes('disabled') !== undefined)).toBe(true); await openSection(x.wrapper, 'Startup'); expect(x.wrapper.findAll('[role="radio"]').every(radio => radio.attributes('disabled') !== undefined)).toBe(true); expect(x.wrapper.get('.startup-health-actions button').attributes('disabled')).toBeDefined() })
  it('keeps Appearance enabled while disconnected', async () => { const x = page({ bridge: 'Unavailable', status: 'ready', hasSnapshot: true }); await x.wrapper.findAll('.settings-theme button')[1].trigger('click'); expect(x.theme.mode).toBe('light') })
  it('shows Waiting for the host without a snapshot', () => { const x = page({ bridge: 'Connecting', status: 'idle', hasSnapshot: false }); expect(x.wrapper.text()).toContain('Waiting for the host'); expect(x.wrapper.text()).not.toContain('English') })
  it('shows skeletons without a snapshot while loading', () => { const x = page({ status: 'loading', hasSnapshot: false }); expect(x.wrapper.findAll('.settings-host-placeholder .q-skeleton')).toHaveLength(2) })
  it('shows a full host error without a snapshot', () => { const x = page({ status: 'error', hasSnapshot: false }); expect(x.wrapper.text()).toContain('Host settings are unavailable'); expect(x.wrapper.text()).not.toContain('English'); expect(x.wrapper.get('.settings-host-placeholder button').text()).toBe('Retry') })
  it('persists the logs switch', async () => { const x = page(); const control = x.wrapper.get('[role="switch"]'); await control.trigger('click'); await flushPromises(); expect(x.setShowLogsInSidebar).toHaveBeenCalledWith(false); expect(x.settings.snapshot?.showLogsInSidebar).toBe(false) })
  it('preserves logs visibility after failure', async () => { const x = page({ logsImpl: async () => { throw new Error('denied') } }); await x.wrapper.get('[role="switch"]').trigger('click'); await flushPromises(); expect(x.settings.snapshot?.showLogsInSidebar).toBe(true); expect(x.wrapper.text()).toContain('preference was not changed') })
  it('does not lock independent host settings while saving logs', async () => { let resolve!: (value: SettingsSnapshot) => void; const pending = new Promise<SettingsSnapshot>(done => { resolve = done }); const x = page({ logsImpl: () => pending }); await x.wrapper.get('[role="switch"]').trigger('click'); expect(x.wrapper.get('[role="switch"]').attributes('disabled')).toBeDefined(); await openSection(x.wrapper, 'Window'); expect(x.wrapper.findAll('[role="radio"]').every(radio => radio.attributes('disabled') === undefined)).toBe(true); resolve({ ...snapshot, showLogsInSidebar: false }); await flushPromises() })
  it('does not expose Save, Apply, or Reset actions', () => expect(page().wrapper.findAll('button').map(button => button.text())).not.toEqual(expect.arrayContaining(['Save','Apply','Reset'])))
  it('uses a stable responsive workspace structure', () => { const wrapper = page().wrapper; expect(wrapper.find('.settings-workspace').exists()).toBe(true); expect(wrapper.find('.settings-section-nav').exists()).toBe(true); expect(wrapper.find('.settings-section-content').exists()).toBe(true) })
})
