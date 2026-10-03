const units = ['B', 'KB', 'MB', 'GB', 'TB'] as const

/** Human-readable file sizes, using 1024 bytes per unit and at most two decimals. */
export function formatFileSize(bytes: number): string {
  let size = Number.isFinite(bytes) ? Math.max(0, Math.floor(bytes)) : 0
  let unit = 0
  while (size >= 1024 && unit < units.length - 1) {
    size /= 1024
    unit++
  }
  size = Math.round(size * 100) / 100
  // Rounding at a boundary should display 1 MB, not 1024 KB.
  if (size >= 1024 && unit < units.length - 1) {
    size /= 1024
    unit++
  }
  return `${size} ${units[unit]}`
}
