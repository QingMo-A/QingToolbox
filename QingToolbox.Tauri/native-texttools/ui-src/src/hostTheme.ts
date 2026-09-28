/**
 * The appearance a module page is drawn in.
 *
 * A module page is its own document in its own WebView, so nothing the shell
 * paints reaches it: no inherited custom properties, no cascade, no `data-`
 * attributes. The two attributes that select a preset have to be written into
 * this document, and the host writes them — the same script before this page's
 * scripts run, and again through `eval` whenever the user changes the
 * appearance, so an open window follows along without listening for anything.
 *
 * What is left is the case the host cannot cover: a module opened from a
 * development server, or a window whose injection did not take. That is what
 * this asks about, and it settles on the default appearance when there is no
 * host to ask.
 *
 * Both answers are checked against the closed sets the stylesheets are written
 * for, so a host newer than this module cannot put the page into a state that
 * no preset covers.
 */

const presets = ['qing-default', 'neon-circuit', 'greenline', 'aurora-flow', 'qing-nova']
const themes = ['system', 'light', 'dark']

/** The part of a module window context that says how to draw the page. */
export interface HostAppearance {
  appearancePreset?: string
  theme?: string
}

const select = (allowed: string[], value: string | undefined, fallback: string): string =>
  value !== undefined && allowed.includes(value) ? value : fallback

/** Write an appearance onto the document, substituting the default for anything unrecognised. */
export function applyAppearance(value: HostAppearance | null | undefined): void {
  const root = document.documentElement
  root.dataset.appearancePreset = select(presets, value?.appearancePreset, 'qing-default')
  root.dataset.theme = select(themes, value?.theme, 'system')
}

/**
 * Resolve the appearance this page should be drawn in.
 *
 * `load` is the module's own module-window context request. It is only made
 * when the host's injected script has not already answered, which is the
 * ordinary case inside the host.
 */
export async function adoptHostAppearance(load: () => Promise<HostAppearance>): Promise<void> {
  const root = document.documentElement
  if (presets.includes(root.dataset.appearancePreset ?? '') && themes.includes(root.dataset.theme ?? '')) return
  try {
    applyAppearance(await load())
  } catch {
    applyAppearance(null)
  }
}
