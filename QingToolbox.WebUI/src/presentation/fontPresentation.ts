import { DEFAULT_FONT_ID, normalizeFont, type SettingsFont } from '../contracts/settings'

export const WEB_FONT_FAMILY_ALIAS = 'QingToolbox User Font'
const DEFAULT_FONT_STACK = '"Segoe UI Variable","Segoe UI",sans-serif'
const SYSTEM_FALLBACK_STACK = '"Segoe UI Variable","Segoe UI",sans-serif'
let applicationVersion = 0

const quoteFamily = (value: string) => `"${value.replaceAll('"', '').replaceAll('\\', '')}"`
export const fontFamilyAliasFor = (value: SettingsFont|undefined|null): string => {
  const font = normalizeFont(value)
  if (font.source !== 'imported') return WEB_FONT_FAMILY_ALIAS
  const suffix = font.id.replace(/^imported:/i, '').slice(0, 16).replace(/[^0-9a-f]/gi, '')
  return `QingToolbox Font ${suffix || 'Imported'}`
}

export const fontStackFor = (value: SettingsFont|undefined|null): string => {
  const font = normalizeFont(value)
  if (font.id === DEFAULT_FONT_ID || font.source === 'default') return DEFAULT_FONT_STACK
  if (font.source === 'system' && font.familyName)
    return `${quoteFamily(font.familyName)},${SYSTEM_FALLBACK_STACK}`
  if (font.source === 'imported') return `${quoteFamily(fontFamilyAliasFor(font))},${DEFAULT_FONT_STACK}`
  return DEFAULT_FONT_STACK
}

const resourceUrlFor = (value: SettingsFont) => {
  if (value.source !== 'imported' || !value.resourceUrl) return null
  try {
    const url = new URL(value.resourceUrl)
    return url.origin === 'https://app.qingtoolbox.local' &&
      /^\/user-fonts\/[0-9a-f]{64}\.(ttf|otf|ttc)$/i.test(url.pathname) ? url.href : null
  } catch { return null }
}

export async function applyFontPresentation(value: SettingsFont|undefined|null): Promise<boolean> {
  const version = ++applicationVersion
  const font = normalizeFont(value)
  const root = typeof document === 'undefined' ? null : document.documentElement
  if (!root) return false
  const resource = resourceUrlFor(font)
  if (resource && typeof FontFace !== 'undefined' && document.fonts) {
    try {
      const face = new FontFace(fontFamilyAliasFor(font), `url("${resource}")`, { display: 'swap' })
      await face.load()
      if (version !== applicationVersion) return false
      document.fonts.add(face)
      root.style.setProperty('--q-font-family', fontStackFor(font))
      root.dataset.fontId = font.id
      return true
    } catch {
      if (version !== applicationVersion) return false
      root.style.setProperty('--q-font-family', DEFAULT_FONT_STACK)
      root.dataset.fontId = DEFAULT_FONT_ID
      return false
    }
  }
  if (font.source === 'imported') {
    if (version !== applicationVersion) return false
    root.style.setProperty('--q-font-family', DEFAULT_FONT_STACK)
    root.dataset.fontId = DEFAULT_FONT_ID
    return false
  }
  root.style.setProperty('--q-font-family', fontStackFor(font))
  root.dataset.fontId = font.id
  return true
}
