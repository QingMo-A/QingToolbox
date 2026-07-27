import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import DevelopmentHomePage from './DevelopmentHomePage.vue'
import { router } from '../app/router'
import { useAppStore } from '../app/store'
import type { AppSnapshot } from '../contracts/app'

const wrappers: VueWrapper[] = []
afterEach(() => wrappers.splice(0).forEach(wrapper => wrapper.unmount()))

const snapshot: AppSnapshot = { environmentKind: 'Development', environmentDisplayName: 'Development Shell', hostVersion: '0.2.0-alpha', protocolVersion: 4, totalModuleCount: 7, validModuleCount: 5, runningModuleCount: 2, generatedAt: '2026-07-27T12:00:00Z' }
type Deferred<T> = { promise: Promise<T>; resolve: (value: T) => void; reject: (reason?: unknown) => void }
function deferred<T>(): Deferred<T> { let resolve!: (value: T) => void; let reject!: (reason?: unknown) => void; const promise = new Promise<T>((ok, fail) => { resolve = ok; reject = fail }); return { promise, resolve, reject } }

type Options = { bridge?: string; withSnapshot?: boolean; pingMs?: number|null; error?: string; ping?: () => Promise<unknown>; getSnapshot?: () => Promise<AppSnapshot> }
function page(options: Options = {}) {
  const pinia = createPinia(); setActivePinia(pinia)
  const store = useAppStore(); store.bridge = options.bridge ?? 'Connected'; store.mode = 'WebView'; store.lastEvent = 'module.changed with a long but safe host event'; store.pingMs = options.pingMs ?? null; store.error = options.error ?? ''; if (options.withSnapshot !== false) store.snapshot = snapshot
  const ping = vi.fn(options.ping ?? (async () => ({ pong: true })))
  const getSnapshot = vi.fn(options.getSnapshot ?? (async () => ({ ...snapshot, generatedAt: '2026-07-27T13:00:00Z' })))
  const wrapper = mount(DevelopmentHomePage, { global: { plugins: [pinia], provide: { appClient: { ping, getSnapshot } } } })
  wrappers.push(wrapper); return { wrapper, store, ping, getSnapshot }
}

const button = (wrapper: VueWrapper, text: string) => wrapper.findAll('button').find(item => item.text().includes(text))!

describe('Development diagnostics workspace', () => {
  it('keeps the /diagnostics route', () => expect(router.resolve('/diagnostics').matched).toHaveLength(1))
  it('uses the QPage diagnostics workspace structure', () => { const x = page().wrapper; expect(x.find('.q-page.diagnostics-page').exists()).toBe(true); expect(x.find('.diagnostics-workspace').exists()).toBe(true) })
  it('shows the Development only marker', () => expect(page().wrapper.text()).toContain('Development only'))
  it.each(['Bridge', 'Environment', 'Mode', 'Host version', 'Protocol'])('shows host field %s', label => expect(page().wrapper.text()).toContain(label))
  it('shows total valid and running counts', () => { const text = page().wrapper.text(); expect(text).toContain('Total modules7'); expect(text).toContain('Valid modules5'); expect(text).toContain('Running modules2') })
  it('derives the invalid module count', () => expect(page().wrapper.text()).toContain('Invalid modules2'))
  it('shows the last snapshot', () => expect(page().wrapper.text()).toContain('Last snapshot'))
  it('shows the unmodified last host event', () => expect(page().wrapper.text()).toContain('module.changed with a long but safe host event'))
  it('shows Not run before a ping', () => expect(page().wrapper.text()).toContain('Last pingNot run'))
  it('updates ping latency and shows a safe success message', async () => { const x = page(); await button(x.wrapper, 'Ping host').trigger('click'); await flushPromises(); expect(x.store.pingMs).toEqual(expect.any(Number)); expect(x.store.pingMs).toBeGreaterThanOrEqual(0); expect(x.wrapper.text()).toContain(`Host responded in ${x.store.pingMs} ms.`) })
  it('uses a safe ping failure and preserves old latency', async () => { const x = page({ pingMs: 17, ping: async () => { throw new Error('secret path C:/private') } }); await button(x.wrapper, 'Ping host').trigger('click'); await flushPromises(); expect(x.store.pingMs).toBe(17); expect(x.wrapper.text()).toContain('The host ping could not be completed.'); expect(x.wrapper.text()).not.toContain('secret path') })
  it('refreshes and rebuilds the snapshot once', async () => { const x = page(); const rebuild = vi.spyOn(x.store, 'rebuild'); await button(x.wrapper, 'Refresh snapshot').trigger('click'); await flushPromises(); expect(x.getSnapshot).toHaveBeenCalledTimes(1); expect(rebuild).toHaveBeenCalledTimes(1); expect(x.wrapper.text()).toContain('Host snapshot refreshed.') })
  it('preserves an old snapshot and hides raw snapshot errors', async () => { const x = page({ getSnapshot: async () => { throw new Error('internal stack and path') } }); const old = x.store.snapshot; await button(x.wrapper, 'Refresh snapshot').trigger('click'); await flushPromises(); expect(x.store.snapshot).toBe(old); expect(x.wrapper.text()).toContain('The host snapshot could not be refreshed.'); expect(x.wrapper.text()).not.toContain('internal stack') })
  it('prevents duplicate ping and disables both host actions while pinging', async () => { const wait = deferred<unknown>(); const x = page({ ping: () => wait.promise }); await button(x.wrapper, 'Ping host').trigger('click'); await button(x.wrapper, 'Pinging').trigger('click'); expect(x.ping).toHaveBeenCalledTimes(1); expect(button(x.wrapper, 'Pinging').attributes('disabled')).toBeDefined(); expect(button(x.wrapper, 'Refresh snapshot').attributes('disabled')).toBeDefined(); wait.resolve({}); await flushPromises() })
  it('disables both host actions while refreshing a snapshot', async () => { const wait = deferred<AppSnapshot>(); const x = page({ getSnapshot: () => wait.promise }); await button(x.wrapper, 'Refresh snapshot').trigger('click'); expect(button(x.wrapper, 'Ping host').attributes('disabled')).toBeDefined(); expect(button(x.wrapper, 'Refreshing').attributes('disabled')).toBeDefined(); wait.resolve(snapshot); await flushPromises() })
  it('shows Waiting and known host identity when disconnected without a snapshot', () => { const x = page({ bridge: 'Connecting', withSnapshot: false }); expect(x.wrapper.text()).toContain('Waiting for the host'); expect(x.wrapper.text()).toContain('Diagnostics will become available after the Development bridge connects.'); expect(x.wrapper.text()).toContain('BridgeConnecting'); expect(x.wrapper.text()).toContain('ModeWebView') })
  it('keeps old data and shows a stale notice after disconnect', () => { const x = page({ bridge: 'Unavailable' }); expect(x.wrapper.text()).toContain('Development Shell'); expect(x.wrapper.text()).toContain('The host is disconnected. Showing the last available diagnostic snapshot.') })
  it('allows snapshot refresh when connected without a snapshot', () => { const x = page({ withSnapshot: false }); expect(x.wrapper.text()).toContain('The bridge is connected, but no host snapshot is currently available.'); expect(button(x.wrapper, 'Refresh snapshot').attributes('disabled')).toBeUndefined() })
  it('shows unavailable host fields without a snapshot', () => { const text = page({ withSnapshot: false }).wrapper.text(); expect(text).toContain('No host snapshot is available.'); expect(text).toContain('Unavailable') })
  it('keeps Reload Web UI available while disconnected', () => expect(button(page({ bridge: 'Unavailable' }).wrapper, 'Reload Web UI').attributes('disabled')).toBeUndefined())
  it('does not show the Diagnostics theme switch', () => expect(page().wrapper.text()).not.toMatch(/Follow system|\bLight\b|\bDark\b/))
  it('does not expose destructive or recovery operations', () => expect(page().wrapper.text()).not.toMatch(/Clear|Export|Repair|Restart host/))
  it('replaces a raw store error with safe text', () => { const text = page({ error: 'C:/private/stack trace' }).wrapper.text(); expect(text).toContain('The Development Web workspace reported a host communication error.'); expect(text).not.toContain('private/stack') })
  it('prioritizes action errors over store errors', async () => { const x = page({ error: 'hidden', ping: async () => { throw new Error('also hidden') } }); await button(x.wrapper, 'Ping host').trigger('click'); await flushPromises(); expect(x.wrapper.text()).toContain('The host ping could not be completed.'); expect(x.wrapper.text()).not.toContain('workspace reported') })
  it('retains responsive section structure', () => { const x = page().wrapper; expect(x.find('.diagnostics-primary-grid').exists()).toBe(true); expect(x.find('.diagnostics-tool-groups').exists()).toBe(true); expect(x.find('.diagnostics-activity-grid').exists()).toBe(true) })
})
