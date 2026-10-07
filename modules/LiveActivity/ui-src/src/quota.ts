import type { RateLimitWindow } from './types'

export function remainingPercent(window: RateLimitWindow): number | null {
  const used = window.usedFraction
  return typeof used === 'number' && Number.isFinite(used) ? Math.max(0, Math.min(100, (1 - used) * 100)) : null
}

export function quotaLabel(window: RateLimitWindow, fallback: string): string {
  const minutes = window.windowMinutes
  if (minutes === 10080) return '每周额度'
  if (typeof minutes !== 'number' || !Number.isFinite(minutes) || minutes <= 0) return fallback
  return minutes % 60 === 0 ? `${minutes / 60} 小时额度` : `${minutes} 分钟额度`
}

export function timeLabel(ms: number | null | undefined, full = false): string {
  if (typeof ms !== 'number' || !Number.isFinite(ms) || ms < 0 || ms > 8.64e15) return '—'
  return new Intl.DateTimeFormat('zh-CN', {
    ...(full ? { month:'2-digit', day:'2-digit' } as const : {}),
    hour:'2-digit', minute:'2-digit', ...(full ? {} : { second:'2-digit' } as const), hour12:false,
  }).format(new Date(ms))
}

export function resetLabel(seconds: number | null, updatedAtMs: number | null, nowMs = Date.now()): string {
  if (typeof seconds !== 'number' || !Number.isFinite(seconds) || seconds < 0 || seconds > 253402300799) return '未提供重置时间'
  const deadline = seconds * 1000
  if (deadline <= nowMs && (updatedAtMs === null || updatedAtMs <= deadline)) return '已到重置时间，等待刷新'
  return `${timeLabel(deadline, true)} 重置`
}
