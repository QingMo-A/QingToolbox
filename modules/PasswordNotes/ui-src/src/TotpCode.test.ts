import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import TotpCode from './TotpCode.vue'
import { defaultTotpSettings, type TotpCode as Code } from './types'
const mocks = vi.hoisted(() => ({ call: vi.fn() }))
vi.mock('./bridge', () => ({ call: mocks.call }))
let wrapper: VueWrapper | undefined
const code = (): Code => ({ code: Date.now() >= 60_000 ? '359152' : '287082', generatedAtMs: Date.now(), validFromMs: Math.floor(Date.now() / 30_000) * 30_000, validUntilMs: (Math.floor(Date.now() / 30_000) + 1) * 30_000, periodSeconds: 30 })
const open = () => wrapper = mount(TotpCode, { attachTo: document.body, props: { entryId: 'entry', credentialId: 'account', config: defaultTotpSettings(), privacyEpoch: 0 } })
beforeEach(() => { vi.useFakeTimers(); vi.setSystemTime(59_000); mocks.call.mockReset(); mocks.call.mockImplementation(async () => code()) })
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.innerHTML = ''; vi.useRealTimers() })
describe('TOTP display lifetime and fresh native copying', () => {
  it('does not read or schedule a timer until explicitly viewed', async () => {
    open(); expect(mocks.call).not.toHaveBeenCalled(); expect(vi.getTimerCount()).toBe(0)
    await wrapper!.get('button').trigger('click'); await flushPromises()
    expect(wrapper!.get('.totp-value').text()).toBe('287 082')
    expect(wrapper!.get('.totp-countdown').attributes('aria-label')).toBe('1 秒后更新')
    expect(mocks.call).toHaveBeenCalledWith('getTotp', { entryId: 'entry', credentialId: 'account' })
  })
  it('removes an expired code immediately, then fetches the new period', async () => {
    open(); await wrapper!.get('button').trigger('click'); await flushPromises()
    await vi.advanceTimersByTimeAsync(1000); await flushPromises()
    expect(mocks.call).toHaveBeenCalledTimes(2)
    expect(wrapper!.get('.totp-value').text()).toBe('359 152')
    expect(wrapper!.get('.totp-countdown').attributes('aria-label')).toBe('30 秒后更新')
  })
  it('copies through the native operation rather than sending a cached code', async () => {
    open(); await wrapper!.get('button').trigger('click'); await flushPromises()
    mocks.call.mockImplementation(async () => ({ ...code(), code: '111222' }))
    await wrapper!.findAll('button')[1].trigger('click'); await flushPromises()
    expect(mocks.call).toHaveBeenLastCalledWith('copyTotp', {entryId:'entry',credentialId:'account'})
    expect(wrapper!.get('.totp-value').text()).toBe('111 222')
    expect(wrapper!.text()).toContain('已复制')
  })
  it('ignores and clears a response arriving after blur or a privacy change', async () => {
    let resolve!: (code: Code) => void
    mocks.call.mockImplementation(() => new Promise<Code>(done => { resolve = done }))
    open(); await wrapper!.get('button').trigger('click')
    window.dispatchEvent(new Event('blur'))
    const response = code(); resolve(response); await flushPromises()
    expect(wrapper!.find('.totp-value').exists()).toBe(false)
    expect(response.code).toBe('')
    expect(vi.getTimerCount()).toBe(0)
    mocks.call.mockImplementation(async () => code())
    await wrapper!.get('button').trigger('click'); await flushPromises()
    await wrapper!.setProps({privacyEpoch:1})
    expect(wrapper!.find('.totp-value').exists()).toBe(false)
  })
  it('rejects delayed expired replies instead of displaying stale digits', async () => {
    const stale = { ...code(), validFromMs:0, validUntilMs:30_000, generatedAtMs:1000 }
    mocks.call.mockResolvedValue(stale)
    open(); await wrapper!.get('button').trigger('click'); await flushPromises()
    expect(wrapper!.text()).not.toContain('287 082')
    expect(wrapper!.find('.totp-countdown').exists()).toBe(false)
  })
  it('recovers when the system clock moves backward to a different time window', async () => {
    open(); await wrapper!.get('button').trigger('click'); await flushPromises()
    vi.setSystemTime(10_000); await vi.advanceTimersByTimeAsync(200); await flushPromises()
    expect(mocks.call).toHaveBeenCalledTimes(2)
    expect(wrapper!.get('.totp-countdown').attributes('aria-label')).toBe('20 秒后更新')
  })
  it('cleans up timers and never re-exposes a late reply after unmount', async () => {
    open(); await wrapper!.get('button').trigger('click'); await flushPromises()
    wrapper!.unmount(); wrapper = undefined
    expect(vi.getTimerCount()).toBe(0)
  })
})
