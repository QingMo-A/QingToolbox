import { describe, expect, it } from 'vitest'
import { alphabeticalItems, recentItems, reorderIds, shortcutFromKeyboard } from './launcher'

const item = (id: string, name: string, lastLaunchedAt: string | null = null) => ({ id, name, iconKey: null, lastLaunchedAt })

describe('launcher projections', () => {
  it('moves a tile before the hovered tile', () => expect(reorderIds(['a', 'b', 'c', 'd'], 'd', 'b')).toEqual(['a', 'd', 'b', 'c']))
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

describe('shortcut recorder', () => {
  it('accepts a modifier plus a key', () => {
    const event = { key: 'l', keyCode: 76, ctrlKey: true, altKey: true, shiftKey: false, metaKey: false } as KeyboardEvent
    expect(shortcutFromKeyboard(event)).toMatchObject({ ctrl: true, alt: true, shift: false, win: false, keyLabel: 'L' })
  })
  it('ignores modifier-only key presses', () => {
    expect(shortcutFromKeyboard({ key: 'Control', keyCode: 17, ctrlKey: true, altKey: false, shiftKey: false, metaKey: false } as KeyboardEvent)).toBeNull()
  })
})
