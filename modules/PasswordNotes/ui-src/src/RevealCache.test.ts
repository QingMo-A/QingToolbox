import { afterEach, describe, expect, it, vi } from 'vitest'
import { RevealCache } from './RevealCache'
afterEach(() => vi.useRealTimers())
describe('explicit, temporary password reveal', () => {
  it('starts unknown, expires independently and cancels every timer on clear', () => {
    vi.useFakeTimers()
    const cache = new RevealCache(vi.fn())
    expect(cache.get('one')).toBeUndefined()
    cache.set('one', 'synthetic-one')
    vi.advanceTimersByTime(20_000)
    cache.set('two', 'synthetic-two')
    vi.advanceTimersByTime(10_000)
    expect(cache.get('one')).toBeUndefined()
    expect(cache.get('two')).toBe('synthetic-two')
    cache.clear()
    expect(cache.get('two')).toBeUndefined()
    expect(vi.getTimerCount()).toBe(0)
  })
})
