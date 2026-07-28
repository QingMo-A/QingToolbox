import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import LogsPage from './LogsPage.vue'
import { router } from '../app/router'
import { useAppStore } from '../app/store'
import { useLogStore } from '../app/logStore'
import type { LogSnapshot } from '../contracts/logs'
import {useSettingsStore}from'../app/settingsStore'
const chinese=()=>useSettingsStore().complete({generatedAt:new Date().toISOString(),language:{code:'system',effectiveCode:'zh-CN',displayName:'System',options:[{code:'system',displayName:'System',nativeName:'跟随系统'},{code:'zh-CN',displayName:'Chinese',nativeName:'简体中文'},{code:'en-US',displayName:'English',nativeName:'English'}]},showLogsInSidebar:true,mainWindowCloseBehavior:'Ask',closeBehaviorMessage:'',launchAtLogin:false,canConfigureLaunchAtLogin:true,canRepairStartup:false,startupPresentationMode:'FloatingBadge',startupBackend:'None',startupStatus:'Unavailable',startupMessage:''})

const wrappers: VueWrapper[] = []
afterEach(() => wrappers.splice(0).forEach(wrapper => wrapper.unmount()))

const snapshot: LogSnapshot = { generatedAt: '2026-07-25T12:00:04Z', entries: [
  { timestamp: '2026-07-25T12:00:01Z', level: 'Information', category: 'Application', message: 'Workspace ready' },
  { timestamp: '2026-07-25T12:00:02Z', level: 'Warning', category: 'Modules', message: 'Module response was slow' },
  { timestamp: '2026-07-25T12:00:03Z', level: 'Error', category: 'Bridge', message: 'Request failed safely' },
  { timestamp: '2026-07-25T12:00:04Z', level: 'Information', category: 'Modules', message: 'Snapshot refreshed' }
] }

type PageOptions = { bridge?: 'Connecting'|'Connected'|'Unavailable'; status?: 'idle'|'loading'|'ready'|'error'; hasSnapshot?: boolean; data?: LogSnapshot; getImpl?: () => Promise<LogSnapshot> }
function page(options: PageOptions = {}) {
  const pinia = createPinia(); setActivePinia(pinia)
  useAppStore().bridge = options.bridge ?? 'Connected'
  const logs = useLogStore(); const status = options.status ?? 'ready'; const hasSnapshot = options.hasSnapshot ?? status === 'ready'
  if (hasSnapshot) logs.complete(options.data ?? snapshot)
  if (status === 'loading') logs.begin(); else if (status === 'error') logs.fail(new Error('private failure detail')); else logs.status = status
  const getSnapshot = vi.fn(options.getImpl ?? (async () => snapshot))
  const wrapper = mount(LogsPage, { global: { plugins: [pinia], provide: { logClient: { getSnapshot } } } })
  wrappers.push(wrapper)
  return { wrapper, logs, getSnapshot }
}

const severityButtons = (wrapper: VueWrapper) => wrapper.findAll('.logs-severity-overview button')

describe('LogsPage everyday workspace', () => {
  it('reactively localizes levels and searches Chinese and English labels without reloading',async()=>{const x=page();chinese();await x.wrapper.vm.$nextTick();expect(x.wrapper.text()).toContain('会话日志');expect(severityButtons(x.wrapper).map(b=>b.text())).toEqual(expect.arrayContaining([expect.stringContaining('全部'),expect.stringContaining('警告')]));await x.wrapper.get('input').setValue('警告');expect(x.wrapper.text()).toContain('Module response was slow');await x.wrapper.get('input').setValue('warning');expect(x.wrapper.text()).toContain('Module response was slow');expect(x.getSnapshot).not.toHaveBeenCalled()})
  it('keeps the /logs route', () => expect(router.resolve('/logs').matched).toHaveLength(1))
  it('loads once from connected idle state', async () => { const x = page({ status: 'idle', hasSnapshot: false }); await flushPromises(); expect(x.getSnapshot).toHaveBeenCalledTimes(1) })
  it('does not reload a ready snapshot automatically', async () => { const x = page(); await flushPromises(); expect(x.getSnapshot).not.toHaveBeenCalled() })
  it('shows the snapshot refresh time', () => expect(page().wrapper.text()).toContain('Last refreshed'))
  it('computes the All count', () => expect(severityButtons(page().wrapper)[0].text()).toContain('4'))
  it('computes the Information count', () => expect(severityButtons(page().wrapper)[1].text()).toContain('2'))
  it('computes the Warning count', () => expect(severityButtons(page().wrapper)[2].text()).toContain('1'))
  it('computes the Error count', () => expect(severityButtons(page().wrapper)[3].text()).toContain('1'))
  it('filters from the severity overview', async () => { const x = page(); await severityButtons(x.wrapper)[2].trigger('click'); expect(x.wrapper.findAll('.logs-row')).toHaveLength(1); expect(x.wrapper.text()).toContain('Module response was slow'); expect(x.wrapper.text()).not.toContain('Request failed safely') })
  it('marks the active severity with aria-pressed', async () => { const x = page(); expect(severityButtons(x.wrapper).map(x => x.attributes('aria-pressed'))).toEqual(['true','false','false','false']); await severityButtons(x.wrapper)[3].trigger('click'); expect(severityButtons(x.wrapper).map(x => x.attributes('aria-pressed'))).toEqual(['false','false','false','true']) })
  it('searches category', async () => { const x = page(); await x.wrapper.get('[aria-label="Search logs"]').setValue('bridge'); expect(x.wrapper.findAll('.logs-row')).toHaveLength(1) })
  it('searches message', async () => { const x = page(); await x.wrapper.get('[aria-label="Search logs"]').setValue('workspace ready'); expect(x.wrapper.findAll('.logs-row')).toHaveLength(1) })
  it('searches level', async () => { const x = page(); await x.wrapper.get('[aria-label="Search logs"]').setValue('warning'); expect(x.wrapper.findAll('.logs-row')).toHaveLength(1) })
  it('searches without case sensitivity', async () => { const x = page(); await x.wrapper.get('[aria-label="Search logs"]').setValue('SNAPSHOT REFRESHED'); expect(x.wrapper.findAll('.logs-row')).toHaveLength(1) })
  it('combines search and severity filters', async () => { const x = page(); await severityButtons(x.wrapper)[1].trigger('click'); await x.wrapper.get('[aria-label="Search logs"]').setValue('modules'); expect(x.wrapper.findAll('.logs-row')).toHaveLength(1); expect(x.wrapper.text()).toContain('Snapshot refreshed') })
  it('shows filtered and total result counts', async () => { const x = page(); await severityButtons(x.wrapper)[1].trigger('click'); expect(x.wrapper.text()).toContain('Showing 2 of 4 entries') })
  it('shows a no-match state', async () => { const x = page(); await x.wrapper.get('[aria-label="Search logs"]').setValue('missing value'); expect(x.wrapper.text()).toContain('No matching session entries'); expect(x.wrapper.text()).toContain('Try changing the search or severity filter.') })
  it('clears filters without requesting a snapshot', async () => { const x = page(); await severityButtons(x.wrapper)[3].trigger('click'); await x.wrapper.get('[aria-label="Search logs"]').setValue('none'); await x.wrapper.get('.logs-inline-state button').trigger('click'); expect(x.wrapper.findAll('.logs-row')).toHaveLength(4); expect(x.getSnapshot).not.toHaveBeenCalled() })
  it('preserves source order', () => expect(page().wrapper.findAll('.logs-row strong').map(x => x.text())).toEqual(['Application','Modules','Bridge','Modules']))
  it('renders semantic time values', () => { const times = page().wrapper.findAll('.logs-row time'); expect(times).toHaveLength(4); expect(times[0].attributes('datetime')).toBe(snapshot.entries[0].timestamp) })
  it('maps all levels to existing badge tones', () => { const badges = page().wrapper.findAll('.logs-row .q-badge'); expect(badges[0].classes()).toContain('is-info'); expect(badges[1].classes()).toContain('is-warning'); expect(badges[2].classes()).toContain('is-danger') })
  it('shows a valid empty snapshot state and zero overview counts', () => { const x = page({ data: { generatedAt: snapshot.generatedAt, entries: [] } }); expect(x.wrapper.text()).toContain('No session entries yet'); expect(severityButtons(x.wrapper).map(x => x.text())).toEqual(expect.arrayContaining([expect.stringContaining('All0'), expect.stringContaining('Information0'), expect.stringContaining('Warning0'), expect.stringContaining('Error0')])) })
  it('waits for a disconnected host without a snapshot', () => { const x = page({ bridge: 'Connecting', status: 'idle', hasSnapshot: false }); expect(x.wrapper.text()).toContain('Waiting for the host'); expect(x.wrapper.find('.logs-severity-overview').exists()).toBe(false) })
  it('shows skeletons while initially loading', () => expect(page({ status: 'loading', hasSnapshot: false }).wrapper.findAll('.q-skeleton')).toHaveLength(6))
  it('shows a safe initial error and Retry', async () => { const x = page({ status: 'error', hasSnapshot: false }); expect(x.wrapper.text()).toContain('The current session log snapshot could not be read.'); expect(x.wrapper.text()).not.toContain('private failure detail'); await x.wrapper.get('.logs-state button').trigger('click'); await flushPromises(); expect(x.getSnapshot).toHaveBeenCalledTimes(1) })
  it('keeps old entries visible while refreshing', () => { const x = page({ status: 'loading', hasSnapshot: true }); expect(x.wrapper.findAll('.logs-row')).toHaveLength(4); expect(x.wrapper.text()).toContain('Refreshing session logs…'); expect(x.wrapper.find('.logs-skeleton').exists()).toBe(false) })
  it('keeps old entries visible after refresh failure', () => { const x = page({ status: 'error', hasSnapshot: true }); expect(x.wrapper.findAll('.logs-row')).toHaveLength(4); expect(x.wrapper.text()).toContain('Log refresh failed. Showing the last available session entries.'); expect(x.wrapper.find('.logs-snapshot-notice button').exists()).toBe(true) })
  it('keeps old entries visible after disconnect', () => { const x = page({ bridge: 'Unavailable', status: 'ready', hasSnapshot: true }); expect(x.wrapper.findAll('.logs-row')).toHaveLength(4); expect(x.wrapper.text()).toContain('The host is disconnected. Showing the last available session entries.') })
  it('keeps local search and filtering usable after disconnect', async () => { const x = page({ bridge: 'Unavailable', status: 'ready', hasSnapshot: true }); await severityButtons(x.wrapper)[3].trigger('click'); await x.wrapper.get('[aria-label="Search logs"]').setValue('bridge'); expect(x.wrapper.findAll('.logs-row')).toHaveLength(1) })
  it('disables Refresh after disconnect', () => expect(page({ bridge: 'Unavailable', status: 'ready', hasSnapshot: true }).wrapper.get('.logs-header button').attributes('disabled')).toBeDefined())
  it('does not expose destructive or file operations', () => expect(page().wrapper.text()).not.toMatch(/Delete|Export|Open file|Clear logs|Copy all/))
  it('retains responsive workspace structure', () => { const wrapper = page().wrapper; expect(wrapper.find('.logs-workspace').exists()).toBe(true); expect(wrapper.find('.logs-severity-overview').exists()).toBe(true); expect(wrapper.find('.logs-tools').exists()).toBe(true); expect(wrapper.find('.logs-table').exists()).toBe(true) })
})
