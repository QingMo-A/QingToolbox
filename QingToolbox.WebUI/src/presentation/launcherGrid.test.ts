import { describe, expect, it } from 'vitest'
import { insertionSlot, moveToSlot, previewSlot } from '../../../QingToolbox.Tauri/native-launcher/ui-src/src/gridOrder'

describe('Launcher stable drag slots', () => {
  const ids = ['a', 'b', 'c', 'folder']
  it('compacts following icons at lift, without moving previous icons', () => {
    expect(previewSlot(ids, 'a', 'b', null)).toBe(0)
    expect(previewSlot(ids, 'c', 'b', null)).toBe(1)
    expect(previewSlot(ids, 'folder', 'b', null)).toBe(2)
  })
  it('opens a gap and preserves exactly the same slot positions on release', () => {
    const after = moveToSlot(ids, 'a', 2)
    expect(after).toEqual(['b', 'c', 'a', 'folder'])
    for (const id of ['b', 'c', 'folder']) expect(previewSlot(ids, id, 'a', 2)).toBe(after.indexOf(id))
  })
  it('accepts the last slot and folders in the same order', () => {
    expect(moveToSlot(ids, 'a', 3)).toEqual(['b', 'c', 'folder', 'a'])
    expect(moveToSlot(ids, 'folder', 0)).toEqual(['folder', 'a', 'b', 'c'])
    expect(insertionSlot(710, 30, 120, 132, 6, 3)).toBe(3)
  })
  it('calculates wrapped rows without consulting animated element bounds', () => {
    expect(insertionSlot(10, 160, 120, 132, 3, 8)).toBe(3)
    expect(insertionSlot(250, 160, 120, 132, 3, 8)).toBe(5)
  })
})
