import type { Hotkey, Item } from './types'

export function reorderIds(ids: readonly string[], movingId: string, overId: string): string[] {
  const next = [...ids]
  const from = next.indexOf(movingId)
  const to = next.indexOf(overId)
  if (from < 0 || to < 0 || from === to) return next
  next.splice(from, 1)
  next.splice(from < to ? to - 1 : to, 0, movingId)
  return next
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
  return { ctrl: event.ctrlKey, alt: event.altKey, shift: event.shiftKey, win: event.metaKey, virtualKey: event.keyCode, keyLabel: key }
}
