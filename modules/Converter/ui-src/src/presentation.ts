export function formatSize(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes < 0) return '—'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let size = bytes, index = 0
  while (size >= 1024 && index < units.length - 1) { size /= 1024; index++ }
  return `${index === 0 ? Math.round(size) : size.toFixed(size >= 100 ? 0 : size >= 10 ? 1 : 2)} ${units[index]}`
}
export function messageOf(error: unknown): string {
  if (typeof error === 'string' && error.trim()) return error
  if (error instanceof Error && error.message) return error.message
  if (error && typeof error === 'object' && 'message' in error && typeof error.message === 'string') return error.message
  return '操作未能完成。'
}
export function batchSummary(files: { status: string }[]): { success: number; failed: number; cancelled: number; finished: number } {
  const success = files.filter(v => v.status === 'succeeded').length
  const failed = files.filter(v => v.status === 'failed').length
  const cancelled = files.filter(v => v.status === 'cancelled').length
  return { success, failed, cancelled, finished: success + failed + cancelled }
}
