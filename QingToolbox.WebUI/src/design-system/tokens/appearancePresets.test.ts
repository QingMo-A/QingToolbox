import { beforeEach, describe, expect, it } from 'vitest'
import {
  appearancePresetIds,
  applyAppearancePreset,
  normalizeAppearancePresetId,
  readAppearancePreset,
  useAppearancePresetStore,
} from './appearancePresets'
import { createPinia, setActivePinia } from 'pinia'

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
})
