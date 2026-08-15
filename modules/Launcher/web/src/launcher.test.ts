import { describe, expect, it } from 'vitest'
import { LAUNCHER_MAX_ROWS, alphabeticalItems, beginPointerGesture, canStartPointerGesture, cancelPointerGesture, completePointerGesture, movePointerGesture, pointerPreview, recentColumnCapacity, recentItems, reorderIds, reorderVisibleIds, reorderVisibleToIndex, searchLauncherItems, shortcutFromKeyboard, targetPointerGesture, targetPointerInsertion, visibleRecentItems } from './launcher'

const item = (id: string, name: string, lastLaunchedAt: string | null = null) => ({ id, name, iconKey: null, lastLaunchedAt })

describe('launcher projections', () => {
  it('moves a tile before the hovered tile', () => expect(reorderIds(['a', 'b', 'c', 'd'], 'd', 'b')).toEqual(['a', 'd', 'b', 'c']))
  it('moves the first tile to the end without changing the other ids', () =>
    expect(reorderIds(['a', 'b', 'c', 'd'], 'a', 'd')).toEqual(['b', 'c', 'a', 'd']))
  it('keeps custom order untouched by alphabetical projection', () => {
    const values = [item('a', 'Steam'), item('b', 'VS Code'), item('c', 'OBS'), item('d', 'IDEA')]
    expect(values.map(value => value.id)).toEqual(['a', 'b', 'c', 'd'])
    expect(alphabeticalItems(values).map(value => value.id)).toEqual(['d', 'c', 'a', 'b'])
    expect(values.map(value => value.id)).toEqual(['a', 'b', 'c', 'd'])
  })
  it('sorts recent items newest first and limits the list', () => {
    const values = [item('a', 'A', '2025-01-01T00:00:00Z'), item('b', 'B', '2025-01-03T00:00:00Z'), item('c', 'C')]
    expect(recentItems(values, 1).map(value => value.id)).toEqual(['b'])
  })
})

describe('pointer reorder gestures', () => {
  it('only starts after the movement threshold', () => {
    const gesture = beginPointerGesture(7, 'a', 10, 10, ['a', 'b', 'c'])
    expect(movePointerGesture(gesture, 14, 14).active).toBe(false)
    const active = movePointerGesture(gesture, 18, 10)
    expect(active.active).toBe(true)
    expect(pointerPreview(gesture, 18, 10)).toBeNull()
    expect(pointerPreview(active, 31, 42)).toEqual({ itemId: 'a', x: 31, y: 42 })
  })
  it('guards custom mode and controls', () => {
    expect(canStartPointerGesture('custom', 0, false)).toBe(true)
    expect(canStartPointerGesture('alphabetical', 0, false)).toBe(false)
    expect(canStartPointerGesture('custom', 0, true)).toBe(false)
    expect(canStartPointerGesture('custom', 2, false)).toBe(false)
  })
  it('reorders once per target and persists only a completed gesture', () => {
    const started = movePointerGesture(beginPointerGesture(1, 'a', 0, 0, ['a', 'b', 'c']), 12, 0)
    const moved = targetPointerGesture(started, ['a', 'b', 'c'], 'c')
    expect(moved.ids).toEqual(['b', 'a', 'c'])
    expect(targetPointerGesture(moved.gesture, moved.ids, 'c').ids).toEqual(moved.ids)
    expect(completePointerGesture(moved.gesture, moved.ids)).toMatchObject({ dragged: true, persist: true, ids: ['b', 'a', 'c'] })
  })
  it('reorders a filtered projection without moving hidden items', () => {
    expect(reorderVisibleIds(['a', 'hidden', 'b', 'c'], ['a', 'b', 'c'], 'c', 'a')).toEqual(['c', 'hidden', 'a', 'b'])
    expect(reorderVisibleIds(['a', 'hidden', 'b', 'c'], ['a', 'b', 'c'], 'b', 'b')).toEqual(['a', 'hidden', 'b', 'c'])
  })
  it('supports insertion slots before first, after last and same slot', () => {
    expect(reorderVisibleToIndex(['a', 'b', 'c'], ['a', 'b', 'c'], 'c', 0)).toEqual(['c', 'a', 'b'])
    expect(reorderVisibleToIndex(['a', 'b', 'c'], ['a', 'b', 'c'], 'a', 2)).toEqual(['b', 'c', 'a'])
    expect(reorderVisibleToIndex(['a', 'b', 'c'], ['a', 'b', 'c'], 'b', 1)).toEqual(['a', 'b', 'c'])
    const active = movePointerGesture(beginPointerGesture(1, 'a', 0, 0, ['a', 'b', 'c']), 12, 0)
    expect(targetPointerInsertion(active, ['a', 'b', 'c'], 2).ids).toEqual(['b', 'c', 'a'])
  })
  it('cancels back to the original order without persistence', () => {
    const started = movePointerGesture(beginPointerGesture(1, 'a', 0, 0, ['a', 'b', 'c']), 12, 0)
    const canceled = cancelPointerGesture(started)
    expect(canceled).toEqual({ dragged: true, ids: ['a', 'b', 'c'] })
  })
})

describe('launcher search and layout projections', () => {
  it('searches names case-insensitively without exposing targets', () => {
    const values = [item('a', 'Visual Studio Code'), item('b', 'OBS Studio')]
    expect(searchLauncherItems(values, '  visual studio  ').map(value => value.id)).toEqual(['a'])
    expect(searchLauncherItems(values, '').map(value => value.id)).toEqual(['a', 'b'])
  })
  it('limits recent items to the measured one-line capacity', () => {
    const values = [item('a', 'A', '2025-01-03T00:00:00Z'), item('b', 'B', '2025-01-02T00:00:00Z'), item('c', 'C', '2025-01-01T00:00:00Z')]
    expect(recentColumnCapacity(452)).toBe(3)
    expect(visibleRecentItems(values, 2).map(value => value.id)).toEqual(['a', 'b'])
    expect(visibleRecentItems(values, 3, 'c').map(value => value.id)).toEqual(['c'])
  })
  it('uses a bounded three-row launcher grid contract', () => {
    expect(LAUNCHER_MAX_ROWS).toBe(3)
  })
})

describe('shortcut recorder', () => {
  it('accepts a modifier plus a key', () => {
    const event = { key: 'l', keyCode: 76, ctrlKey: true, altKey: true, shiftKey: false, metaKey: false } as KeyboardEvent
    expect(shortcutFromKeyboard(event)).toMatchObject({ ctrl: true, alt: true, shift: false, win: false, keyLabel: 'L' })
  })
  it('ignores modifier-only key presses', () => {
    expect(shortcutFromKeyboard({ key: 'Control', keyCode: 17, ctrlKey: true, altKey: false, shiftKey: false, metaKey: false } as KeyboardEvent)).toBeNull()
  })
})
