import { afterEach, describe, expect, it, vi } from 'vitest'
import { applyFontPresentation, fontFamilyAliasFor, fontStackFor } from './fontPresentation'

const hash = 'b'.repeat(64)
const imported = { id: `imported:${hash}`, source: 'imported' as const, displayName: 'Imported Sans', familyName: 'Imported Sans', resourceUrl: `https://app.qingtoolbox.local/user-fonts/${hash}.ttf` }

afterEach(() => {
  vi.unstubAllGlobals()
  document.documentElement.style.removeProperty('--q-font-family')
  delete document.documentElement.dataset.fontId
})

describe('font presentation', () => {
  it('keeps the built-in Default stack readable', async () => {
    expect(fontStackFor(null)).toContain('Segoe UI Variable')
    await expect(applyFontPresentation(null)).resolves.toBe(true)
    expect(document.documentElement.dataset.fontId).toBe('Default')
  })

  it('uses a sanitized system family without allowing path injection', () => {
    const system = { id: 'system:Inter', source: 'system' as const, displayName: 'Inter', familyName: 'Inter' }
    expect(fontStackFor(system)).toContain('"Inter"')
    expect(fontStackFor({ ...system, familyName: 'x\\";url(evil)' })).toMatch(/^"x;url\(evil\)"/)
  })

  it('loads imported fonts with a unique alias and same-origin URL', async () => {
    const added: unknown[] = []
    Object.defineProperty(document, 'fonts', { configurable: true, value: { add: (font: unknown) => added.push(font) } })
    class FakeFontFace { family: string; constructor(family: string) { this.family = family } load() { return Promise.resolve(this) } }
    vi.stubGlobal('FontFace', FakeFontFace)
    await expect(applyFontPresentation(imported)).resolves.toBe(true)
    expect(fontFamilyAliasFor(imported)).toContain(hash.slice(0, 16))
    expect(document.documentElement.style.getPropertyValue('--q-font-family')).toContain(fontFamilyAliasFor(imported))
    expect(document.documentElement.dataset.fontId).toBe(imported.id)
    expect(added).toHaveLength(1)
  })

  it('falls back to Default when an imported resource fails', async () => {
    Object.defineProperty(document, 'fonts', { configurable: true, value: { add: vi.fn() } })
    class BrokenFontFace { load() { return Promise.reject(new Error('bad font')) } }
    vi.stubGlobal('FontFace', BrokenFontFace)
    await expect(applyFontPresentation(imported)).resolves.toBe(false)
    expect(document.documentElement.dataset.fontId).toBe('Default')
    expect(document.documentElement.style.getPropertyValue('--q-font-family')).toContain('Segoe UI Variable')
  })

  it('does not let a late imported load overwrite a newer system choice', async () => {
    const added: unknown[] = []
    Object.defineProperty(document, 'fonts', { configurable: true, value: { add: (font: unknown) => added.push(font) } })
    let resolve!: () => void
    class SlowFontFace { constructor(public readonly family: string) {} load() { return new Promise<SlowFontFace>(done => { resolve = () => done(this) }) } }
    vi.stubGlobal('FontFace', SlowFontFace)
    const pending = applyFontPresentation(imported)
    await applyFontPresentation({ id: 'system:Inter', source: 'system', displayName: 'Inter', familyName: 'Inter' })
    resolve()
    await pending
    expect(document.documentElement.dataset.fontId).toBe('system:Inter')
    expect(document.documentElement.style.getPropertyValue('--q-font-family')).toContain('Inter')
    expect(added).toHaveLength(0)
  })
})
