const presets = ['qing-default', 'neon-circuit', 'greenline', 'aurora-flow', 'qing-nova']
const themes = ['system', 'light', 'dark']

export interface HostAppearance { appearancePreset?: string; theme?: string }
const select = (allowed: string[], value: unknown, fallback: string): string =>
  typeof value === 'string' && allowed.includes(value) ? value : fallback

export function applyAppearance(value: unknown): void {
  const appearance = typeof value === 'object' && value !== null ? value as HostAppearance : null
  const root = document.documentElement
  root.dataset.appearancePreset = select(presets, appearance?.appearancePreset, 'qing-default')
  root.dataset.theme = select(themes, appearance?.theme, 'system')
}

// The host injects these attributes before first paint and updates them on
// appearance changes. Only old hosts / standalone previews need the fallback.
export async function adoptHostAppearance(load: () => Promise<unknown>): Promise<void> {
  const root = document.documentElement
  root.classList.add('q-module-document')
  if (presets.includes(root.dataset.appearancePreset ?? '') && themes.includes(root.dataset.theme ?? '')) return
  try { applyAppearance(await load()) }
  catch { applyAppearance(null) }
}
