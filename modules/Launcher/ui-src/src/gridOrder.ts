export function moveToSlot(ids: string[], moving: string, slot: number): string[] {
  const remaining = ids.filter(id => id !== moving)
  remaining.splice(Math.max(0, Math.min(slot, remaining.length)), 0, moving)
  return remaining
}

// Hit testing uses logical slots, never the animated DOM rectangles.
export function insertionSlot(x: number, y: number, width: number, rowHeight: number, columns: number, count: number): number {
  const column = Math.max(0, Math.min(columns - 1, Math.floor(x / width)))
  return Math.max(0, Math.min(count, Math.floor(Math.max(0, y) / rowHeight) * columns + column))
}

export function previewSlot(ids: string[], id: string, moving: string | null, gap: number | null): number {
  if (!moving || id === moving) return ids.indexOf(id)
  const compact = ids.filter(value => value !== moving).indexOf(id)
  return compact + (gap !== null && compact >= gap ? 1 : 0)
}
