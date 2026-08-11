import { beforeEach, describe, expect, it } from 'vitest'
import {
  appearancePresetIds,
  applyAppearancePreset,
  normalizeAppearancePresetId,
  readAppearancePreset,
  useAppearancePresetStore,
} from './appearancePresets'
import { createPinia, setActivePinia } from 'pinia'
import { readFileSync } from 'node:fs'
import { join } from 'node:path'

const appearanceCss = readFileSync(join(process.cwd(), 'src/design-system/tokens/appearancePresets.css'), 'utf8')

describe('appearance preset projection', () => {
  beforeEach(() => {
    localStorage.clear()
    document.documentElement.removeAttribute('data-appearance-preset')
    setActivePinia(createPinia())
  })

  it('keeps the five supported ids explicit and falls back unknown values', () => {
    expect(appearancePresetIds).toEqual([
      'qing-default', 'neon-circuit', 'greenline', 'aurora-flow', 'qing-nova',
    ])
    expect(normalizeAppearancePresetId('greenline')).toBe('greenline')
    expect(normalizeAppearancePresetId('missing')).toBe('qing-default')
    expect(normalizeAppearancePresetId(42)).toBe('qing-default')
  })

  it('projects the normalized id to the document and local storage immediately', () => {
    expect(applyAppearancePreset('neon-circuit')).toBe('neon-circuit')
    expect(document.documentElement.dataset.appearancePreset).toBe('neon-circuit')
    expect(readAppearancePreset()).toBe('neon-circuit')
    expect(applyAppearancePreset('malicious-value')).toBe('qing-default')
    expect(document.documentElement.dataset.appearancePreset).toBe('qing-default')
  })

  it('initializes the Pinia store from safe persisted state', () => {
    localStorage.setItem('qing.appearancePreset', 'aurora-flow')
    const store = useAppearancePresetStore()
    expect(store.id).toBe('aurora-flow')
    expect(store.set('unknown')).toBe('qing-default')
    expect(document.documentElement.dataset.appearancePreset).toBe('qing-default')
  })

  it('keeps shared control tokens and state selectors in the design-system layer', () => {
    const tokenNames = [
      '--q-primary', '--q-secondary', '--q-danger', '--q-input', '--q-select', '--q-toggle', '--q-border', '--q-focus',
      '--control-primary-bg', '--control-secondary-bg', '--control-danger-bg', '--control-input-bg',
      '--control-secondary-border', '--control-toggle-track', '--control-toggle-height', '--control-toggle-radius',
      '--control-input-focus-border', '--control-input-focus-shadow', '--control-radio-selected-bg', '--control-focus-ring',
      '--control-shadow', '--control-hover-shadow',
    ]
    for (const id of appearancePresetIds) {
      const start = appearanceCss.indexOf(`:root[data-appearance-preset='${id}']`)
      expect(start, `${id} token block`).toBeGreaterThanOrEqual(0)
      const block = appearanceCss.slice(start, appearanceCss.indexOf('}', start))
      for (const token of tokenNames) expect(block).toContain(token)
    }
    for (const selector of [
      'input:not', 'select', 'textarea', 'input[type=\'checkbox\']', 'input[type=\'radio\']',
      '.q-switch', '.close-radio', '[role=\'tab\']', '.q-icon-button', '.q-button.is-loading',
      ':hover', ':active', ':focus-visible', ':disabled',
    ]) expect(appearanceCss).toContain(selector)

    const defaultStart = appearanceCss.indexOf(":root[data-appearance-preset='qing-default']")
    const defaultBlock = appearanceCss.slice(defaultStart, appearanceCss.indexOf('}', defaultStart))
    expect(defaultBlock).toContain('--control-secondary-bg: var(--q-secondary)')
    expect(defaultBlock).toContain('--control-input-bg: var(--q-input)')
    expect(defaultBlock).toContain('--control-toggle-track: var(--q-toggle)')

    const darkDefaultStart = appearanceCss.indexOf(":root[data-theme='dark'][data-appearance-preset='qing-default']")
    const darkDefaultBlock = appearanceCss.slice(darkDefaultStart, appearanceCss.indexOf('}', darkDefaultStart))
    expect(darkDefaultBlock).toContain('--q-brand-soft: #203a65')
    expect(darkDefaultBlock).toContain('--q-focus: #79a8ff')
  })
})
