export function messageOf(reason: unknown): string {
  if (typeof reason === 'string' && reason.trim()) return reason.trim()
  if (reason instanceof Error && reason.message) return reason.message
  if (typeof reason === 'object' && reason !== null) {
    const value = reason as { message?: unknown; error?: unknown }
    if (typeof value.message === 'string' && value.message.trim()) return value.message.trim()
    if (typeof value.error === 'string' && value.error.trim()) return value.error.trim()
    if (typeof value.error === 'object' && value.error !== null) return messageOf(value.error)
  }
  return 'PDF 操作未能完成，请重试。'
}

export function formatSize(value: number): string {
  if (!Number.isFinite(value) || value < 0) return '—'
  if (value < 1024) return `${Math.round(value)} B`
  const units = ['KB', 'MB', 'GB', 'TB']
  let size = value / 1024
  let index = 0
  while (size >= 1024 && index < units.length - 1) { size /= 1024; index += 1 }
  return `${size.toFixed(size < 10 ? 1 : 0)} ${units[index]}`
}

export function splitPreview(total: number, parts: number): string[] {
  if (!Number.isInteger(parts) || parts < 2 || parts > total || !Number.isInteger(total)) return []
  const base = Math.floor(total / parts)
  const remainder = total % parts
  let start = 1
  // A long document must not render thousands of preview chips.
  return Array.from({ length: Math.min(parts, 12) }, (_, index) => {
    const end = start + base + (index < remainder ? 1 : 0) - 1
    const range = start === end ? `${start}` : `${start}–${end}`
    start = end + 1
    return range
  })
}
