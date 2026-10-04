import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { PopupCountdown } from './countdown'

describe('independent popup countdowns', () => {
  beforeEach(() => { vi.useFakeTimers(); vi.setSystemTime(0) })
  afterEach(() => { vi.useRealTimers() })
  const timer = (expire = vi.fn()) => ({ expire, countdown: new PopupCountdown(expire, () => Date.now()) })

  it('pauses only the hovered card and resumes its remaining time', () => {
    const first = timer(); const second = timer()
    first.countdown.start(10_000)
    vi.advanceTimersByTime(3_000)
    first.countdown.pause()
    second.countdown.start(5_000)
    vi.advanceTimersByTime(20_000)
    expect(first.expire).not.toHaveBeenCalled()
    expect(second.expire).toHaveBeenCalledTimes(1)
    first.countdown.resume()
    vi.advanceTimersByTime(6_999)
    expect(first.expire).not.toHaveBeenCalled()
    vi.advanceTimersByTime(1)
    expect(first.expire).toHaveBeenCalledTimes(1)
  })

  it('does not extend an existing card when a new card arrives', () => {
    const first = timer(); const second = timer()
    first.countdown.start(15_000)
    vi.advanceTimersByTime(10_000)
    second.countdown.start(15_000)
    vi.advanceTimersByTime(5_000)
    expect(first.expire).toHaveBeenCalledTimes(1)
    expect(second.expire).not.toHaveBeenCalled()
    vi.advanceTimersByTime(10_000)
    expect(second.expire).toHaveBeenCalledTimes(1)
  })

  it('handles repeated hover events, an initially hovered card, and disposal', () => {
    const x = timer()
    x.countdown.start(3_000, true)
    vi.advanceTimersByTime(10_000)
    x.countdown.resume(); x.countdown.resume()
    vi.advanceTimersByTime(1_000)
    x.countdown.pause(); x.countdown.pause()
    vi.advanceTimersByTime(10_000)
    x.countdown.resume()
    vi.advanceTimersByTime(1_999)
    expect(x.expire).not.toHaveBeenCalled()
    x.countdown.stop()
    vi.advanceTimersByTime(10_000)
    expect(x.expire).not.toHaveBeenCalled()
  })

  it('expires after leaving if hovering intercepted the exact deadline', () => {
    let now = 0
    const expire = vi.fn()
    const countdown = new PopupCountdown(expire, () => now)
    countdown.start(3_000)
    now = 3_000
    countdown.pause()
    vi.advanceTimersByTime(10_000)
    expect(expire).not.toHaveBeenCalled()
    countdown.resume()
    vi.runOnlyPendingTimers()
    expect(expire).toHaveBeenCalledOnce()
    countdown.resume()
    vi.runOnlyPendingTimers()
    expect(expire).toHaveBeenCalledOnce()
  })
})
