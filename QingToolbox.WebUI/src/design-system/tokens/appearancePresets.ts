import { defineStore } from 'pinia'

export const appearancePresetIds = [
  'qing-default',
  'neon-circuit',
  'greenline',
  'aurora-flow',
  'qing-nova',
] as const

export type AppearancePresetId = typeof appearancePresetIds[number]

export const appearancePresetStorageKey = 'qing.appearancePreset'

export function normalizeAppearancePresetId(value: unknown): AppearancePresetId {
  return typeof value === 'string' && appearancePresetIds.includes(value as AppearancePresetId)
    ? value as AppearancePresetId
    : 'qing-default'
}

export function readAppearancePreset(): AppearancePresetId {
  if (typeof localStorage === 'undefined') return 'qing-default'
  return normalizeAppearancePresetId(localStorage.getItem(appearancePresetStorageKey))
}

export function applyAppearancePreset(value: unknown): AppearancePresetId {
  const id = normalizeAppearancePresetId(value)
  if (typeof document !== 'undefined') document.documentElement.dataset.appearancePreset = id
  if (typeof localStorage !== 'undefined') localStorage.setItem(appearancePresetStorageKey, id)
  return id
}

export const useAppearancePresetStore = defineStore('appearancePreset', {
  state: () => ({ id: readAppearancePreset() as AppearancePresetId }),
  actions: {
    set(value: unknown) {
      this.id = applyAppearancePreset(value)
      return this.id
    },
  },
})
