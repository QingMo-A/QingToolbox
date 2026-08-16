import type { Hotkey, Item } from './types'

export const POINTER_DRAG_THRESHOLD = 8
export const LAUNCHER_MAX_ROWS = 3
export const LAUNCHER_TILE_MIN_WIDTH = 112
export const RECENT_TILE_MIN_WIDTH = 140
export const RECENT_TILE_GAP = 8

export function normalizeSearchQuery(query: string) {
  return query.trim().normalize('NFKC').toLocaleLowerCase()
}

export function searchLauncherItems(items: readonly Item[], query: string): Item[] {
  const normalized = normalizeSearchQuery(query)
  if (!normalized) return [...items]
  return items.filter(item => normalizeSearchQuery(item.name).includes(normalized))
}

export function recentColumnCapacity(width: number, minWidth = RECENT_TILE_MIN_WIDTH, gap = RECENT_TILE_GAP) {
  if (!Number.isFinite(width) || width <= 0) return 1
  return Math.max(1, Math.floor((width + gap) / (minWidth + gap)))
}

export function visibleRecentItems(items: readonly Item[], capacity: number, query = '') {
  return searchLauncherItems(items, query).slice(0, Math.max(0, Math.floor(capacity)))
}

export function canStartPointerGesture(sortMode: 'custom' | 'alphabetical' | 'desktop', button: number, onControl: boolean) {
  return sortMode !== 'alphabetical' && button === 0 && !onControl
}

export type PointerGesture = {
  pointerId: number
  movingId: string
  originIds: string[]
  scopeIds: string[]
  startX: number
  startY: number
  active: boolean
  overId: string | null
}

export type PointerPreview = { itemId: string; x: number; y: number }

export function beginPointerGesture(
  pointerId: number,
  movingId: string,
  startX: number,
  startY: number,
  ids: readonly string[],
  scopeIds: readonly string[] = ids,
): PointerGesture {
  return {
    pointerId,
    movingId,
    originIds: [...ids],
    scopeIds: [...scopeIds],
    startX,
    startY,
    active: false,
    overId: null,
  }
}

export function movePointerGesture(
  gesture: PointerGesture,
  x: number,
  y: number,
  threshold = POINTER_DRAG_THRESHOLD,
): PointerGesture {
  if (gesture.active) return gesture
  const dx = x - gesture.startX
  const dy = y - gesture.startY
  return Math.hypot(dx, dy) >= threshold ? { ...gesture, active: true } : gesture
}

export function pointerPreview(gesture: PointerGesture, x: number, y: number): PointerPreview | null {
  return gesture.active ? { itemId: gesture.movingId, x, y } : null
}

export function targetPointerGesture(
  gesture: PointerGesture,
  ids: readonly string[],
  overId: string | null,
): { gesture: PointerGesture; ids: string[] } {
  if (!gesture.active || !overId || overId === gesture.movingId || overId === gesture.overId) {
    return { gesture, ids: [...ids] }
  }
  return {
    gesture: { ...gesture, overId },
    ids: reorderVisibleIds(ids, gesture.scopeIds, gesture.movingId, overId),
  }
}

export function targetPointerInsertion(
  gesture: PointerGesture,
  ids: readonly string[],
  insertionIndex: number,
): { gesture: PointerGesture; ids: string[] } {
  if (!gesture.active) return { gesture, ids: [...ids] }
  const visible = gesture.scopeIds.filter(id => ids.includes(id))
  const withoutMoving = visible.filter(id => id !== gesture.movingId)
  const bounded = Math.max(0, Math.min(Math.floor(insertionIndex), withoutMoving.length))
  const next = reorderVisibleToIndex(ids, visible, gesture.movingId, bounded)
  const overId = withoutMoving[bounded] ?? null
  return { gesture: { ...gesture, overId }, ids: next }
}

export function completePointerGesture(gesture: PointerGesture, ids: readonly string[]) {
  return { dragged: gesture.active, persist: gesture.active, ids: [...ids] }
}

export function cancelPointerGesture(gesture: PointerGesture) {
  return { dragged: gesture.active, ids: [...gesture.originIds] }
}

export function reorderIds(ids: readonly string[], movingId: string, overId: string): string[] {
  const next = [...ids]
  const from = next.indexOf(movingId)
  const to = next.indexOf(overId)
  if (from < 0 || to < 0 || from === to) return next
  next.splice(from, 1)
  next.splice(from < to ? to - 1 : to, 0, movingId)
  return next
}

/** Reorders only the currently visible projection while preserving filtered items in place. */
export function reorderVisibleIds(
  ids: readonly string[],
  visibleIds: readonly string[],
  movingId: string,
  overId: string,
): string[] {
  const visible = visibleIds.filter(id => ids.includes(id))
  const reordered = reorderIds(visible, movingId, overId)
  if (reordered.join('\u0000') === visible.join('\u0000')) return [...ids]
  const next = [...ids]
  const positions = visible.map(id => next.indexOf(id)).filter(index => index >= 0)
  positions.forEach((position, index) => { next[position] = reordered[index] })
  return next
}

export function reorderVisibleToIndex(
  ids: readonly string[],
  visibleIds: readonly string[],
  movingId: string,
  insertionIndex: number,
): string[] {
  const visible = visibleIds.filter(id => ids.includes(id))
  if (!visible.includes(movingId)) return [...ids]
  const withoutMoving = visible.filter(id => id !== movingId)
  const bounded = Math.max(0, Math.min(Math.floor(insertionIndex), withoutMoving.length))
  const reordered = [...withoutMoving]
  reordered.splice(bounded, 0, movingId)
  const next = [...ids]
  const positions = visible.map(id => next.indexOf(id)).filter(index => index >= 0)
  positions.forEach((position, index) => { next[position] = reordered[index] })
  return next
}

/**
 * Maps a pointer location in a stable row-major grid to an insertion slot.
 * The returned value is a gap index (0..remainingCount), not an item index.
 * Keeping this calculation independent of the rendered tile DOM means Vue
 * transitions/reordering cannot change the projection while a pointer is down.
 */
export function gridSlotFromPoint(
  localX: number,
  localY: number,
  cellWidth: number,
  cellHeight: number,
  columns: number,
  gapX: number,
  gapY: number,
  remainingCount: number,
): number {
  const count = Math.max(0, Math.floor(remainingCount))
  if (count === 0) return 0
  const width = Math.max(1, cellWidth)
  const height = Math.max(1, cellHeight)
  const columnCount = Math.max(1, Math.floor(columns))
  const strideX = width + Math.max(0, gapX)
  const strideY = height + Math.max(0, gapY)
  const safeX = Math.max(0, Number.isFinite(localX) ? localX : 0)
  const safeY = Math.max(0, Number.isFinite(localY) ? localY : 0)
  let column = Math.floor(safeX / strideX)
  let row = Math.floor(safeY / strideY)
  const withinX = safeX - column * strideX
  const withinY = safeY - row * strideY
  if (withinX > width / 2) column += 1
  if (withinY > height / 2) row += 1
  if (column >= columnCount) {
    row += Math.floor(column / columnCount)
    column %= columnCount
  }
  return Math.max(0, Math.min(count, row * columnCount + column))
}

export type FrozenGridMetrics = {
  cellWidth: number
  cellHeight: number
  columns: number
  gapX: number
  gapY: number
}

/**
 * Returns a candidate gap only while the pointer is in a boundary corridor.
 * Tile centres deliberately return null so the compact grid stays settled
 * until the pointer actually enters an insertion zone.
 */
export function gridInsertionCandidate(
  localX: number,
  localY: number,
  metrics: FrozenGridMetrics,
  remainingCount: number,
  previousSlot: number | null = null,
): number | null {
  const count = Math.max(0, Math.floor(remainingCount))
  if (count === 0) return 0
  if (localX < 0 || localY < 0) return 0
  const width = Math.max(1, metrics.cellWidth)
  const height = Math.max(1, metrics.cellHeight)
  const gapX = Math.max(0, metrics.gapX)
  const gapY = Math.max(0, metrics.gapY)
  const columns = Math.max(1, Math.floor(metrics.columns))
  const strideX = width + gapX
  const strideY = height + gapY

  // Once a full-cell placeholder has opened, treat that rendered vacancy as
  // a sticky drop target. Without this retention zone the compact-grid
  // boundary calculation returns null in the vacancy centre and makes the
  // following tiles snap back underneath the pointer.
  if (previousSlot !== null) {
    const retainedSlot = Math.max(0, Math.min(count, Math.floor(previousSlot)))
    const retainedColumn = retainedSlot % columns
    const retainedRow = Math.floor(retainedSlot / columns)
    const retentionX = Math.max(8, gapX / 2)
    const retentionY = Math.max(8, gapY / 2)
    const retainedLeft = retainedColumn * strideX - retentionX
    const retainedTop = retainedRow * strideY - retentionY
    if (localX >= retainedLeft && localX <= retainedLeft + width + retentionX * 2 &&
        localY >= retainedTop && localY <= retainedTop + height + retentionY * 2) {
      return retainedSlot
    }
  }

  const slot = gridSlotFromPoint(localX, localY, width, height, columns, gapX, gapY, count)
  if (slot >= count) return count
  const column = Math.floor(localX / strideX)
  const row = Math.floor(localY / strideY)
  const withinX = localX - column * strideX
  const withinY = localY - row * strideY
  const corridor = Math.max(14, Math.min(width, height) * 0.18)
  const inGap = withinX > width || withinY > height
  const nearBoundary = withinX <= corridor || withinX >= width - corridor || withinY <= corridor || withinY >= height - corridor
  return inGap || nearBoundary ? slot : null
}

export function alphabeticalItems(items: readonly Item[]): Item[] {
  return items.map((item, index) => ({ item, index }))
    .sort((left, right) => left.item.name.localeCompare(right.item.name, undefined, { sensitivity: 'base' }) || left.index - right.index)
    .map(({ item }) => item)
}

export function recentItems(items: readonly Item[], limit = 10): Item[] {
  return items.filter(item => item.lastLaunchedAt !== null)
    .sort((left, right) => Date.parse(right.lastLaunchedAt!) - Date.parse(left.lastLaunchedAt!))
    .slice(0, limit)
}

export function shortcutFromKeyboard(event: KeyboardEvent): Hotkey | null {
  const key = event.key.toUpperCase()
  if (!(event.ctrlKey || event.altKey || event.shiftKey || event.metaKey) || key === 'CONTROL' || key === 'ALT' || key === 'SHIFT' || key === 'META') return null
  return { ctrl: event.ctrlKey, alt: event.altKey, shift: event.shiftKey, win: event.metaKey, virtualKey: key === ' ' ? 32 : event.keyCode, keyLabel: key === ' ' ? 'Space' : key }
}
