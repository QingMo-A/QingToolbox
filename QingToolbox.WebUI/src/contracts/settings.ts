import { isRecord } from './app'

export type MainWindowCloseBehavior = 'Ask'|'MinimizeToNotificationArea'|'ExitApplication'
export type StartupPresentationMode = 'MainWindow'|'Minimized'|'FloatingBadge'
export type LanguageCode = 'system'|'zh-CN'|'en-US'
export type EffectiveLanguageCode = 'zh-CN'|'en-US'
export type FontSource = 'default'|'system'|'imported'
export const DEFAULT_FONT_ID = 'Default'

export interface SettingsFont {
  id: string
  source: FontSource
  displayName: string
  familyName?: string|null
  resourceUrl?: string|null
}

export type SettingsFontOption = SettingsFont

export interface SettingsLanguageOption {
  code: LanguageCode
  displayName: string
  nativeName: string
}

export interface SettingsLanguage {
  code: LanguageCode
  effectiveCode: EffectiveLanguageCode
  displayName: string
  options: SettingsLanguageOption[]
}

export interface SettingsSnapshot {
  generatedAt: string
  /** Host-selected workspace appearance. Older hosts may omit this field. */
  appearancePresetId?: string
  /** Host-selected font. Older hosts may omit this field and use the built-in Default. */
  font?: SettingsFont
  /** Safe system/imported catalog; paths are never included. */
  fonts?: SettingsFontOption[]
  language: SettingsLanguage
  showLogsInSidebar: boolean
  mainWindowCloseBehavior: MainWindowCloseBehavior
  closeBehaviorMessage: string
  launchAtLogin: boolean
  canConfigureLaunchAtLogin: boolean
  canRepairStartup: boolean
  startupPresentationMode: StartupPresentationMode
  startupBackend: string
  startupStatus: string
  startupMessage: string
}

export interface SettingsFontImportResponse {
  disposition: 'Imported'|'Cancelled'
  snapshot: SettingsSnapshot
}

const languageCodes: readonly LanguageCode[] = ['system', 'zh-CN', 'en-US']
const isLanguageCode = (value: unknown): value is LanguageCode =>
  typeof value === 'string' && languageCodes.includes(value as LanguageCode)
const isEffectiveLanguageCode = (value: unknown): value is EffectiveLanguageCode =>
  value === 'zh-CN' || value === 'en-US'
const isLanguageOption = (value: unknown): value is SettingsLanguageOption =>
  isRecord(value) && isLanguageCode(value.code) && typeof value.displayName === 'string' &&
  typeof value.nativeName === 'string'
const isLanguage = (value: unknown): value is SettingsLanguage => {
  if (!isRecord(value) || !isLanguageCode(value.code) ||
      !isEffectiveLanguageCode(value.effectiveCode) || typeof value.displayName !== 'string' ||
      !Array.isArray(value.options) || !value.options.every(isLanguageOption)) return false
  const codes = value.options.map(option => option.code)
  return codes.length === languageCodes.length && new Set(codes).size === codes.length &&
    languageCodes.every(code => codes.includes(code))
}
const date = (value: unknown): value is string =>
  typeof value === 'string' && !Number.isNaN(Date.parse(value))

const isFontSource = (value: unknown): value is FontSource =>
  value === 'default' || value === 'system' || value === 'imported'
const isFont = (value: unknown): value is SettingsFont =>
  isRecord(value) && typeof value.id === 'string' && value.id.length > 0 && value.id.length <= 256 &&
  isFontSource(value.source) && typeof value.displayName === 'string' && value.displayName.length <= 256 &&
  (value.familyName === undefined || value.familyName === null || typeof value.familyName === 'string') &&
  (value.resourceUrl === undefined || value.resourceUrl === null || typeof value.resourceUrl === 'string')

const safeFontResource = (value: string|null|undefined): string|null => {
  if (!value) return null
  try {
    const url = new URL(value)
    if (url.origin !== 'https://app.qingtoolbox.local' || !/^\/user-fonts\/[0-9a-f]{64}\.(ttf|otf|ttc)$/i.test(url.pathname)) return null
    return url.href
  } catch { return null }
}

export const normalizeFont = (value: SettingsFont|undefined|null): SettingsFont => {
  if (!value || value.id === DEFAULT_FONT_ID || value.source === 'default')
    return { id: DEFAULT_FONT_ID, source: 'default', displayName: 'Default', familyName: null, resourceUrl: null }
  if (value.source === 'system' && value.id.startsWith('system:') && value.familyName)
    return { ...value, familyName: value.familyName.slice(0, 128), resourceUrl: null }
  if (value.source === 'imported' && /^imported:[0-9a-f]{64}$/i.test(value.id) && value.resourceUrl)
    return { ...value, resourceUrl: safeFontResource(value.resourceUrl) }
  return { id: DEFAULT_FONT_ID, source: 'default', displayName: 'Default', familyName: null, resourceUrl: null }
}

export const normalizeFontOptions = (value: SettingsFontOption[]|undefined|null): SettingsFontOption[] => {
  const options = [normalizeFont(null), ...(value ?? []).filter(isFont).map(normalizeFont)]
  return options.filter((option, index, all) => all.findIndex(item => item.id === option.id) === index)
}

export const normalizeSettingsSnapshot = (value: SettingsSnapshot): SettingsSnapshot => {
  if (!('font' in value) && !('fonts' in value)) return value
  return {
    ...value,
    font: normalizeFont(value.font),
    fonts: normalizeFontOptions(value.fonts),
  }
}

export const isSettingsFontImportResponse = (value: unknown): value is SettingsFontImportResponse =>
  isRecord(value) && (value.disposition === 'Imported' || value.disposition === 'Cancelled') &&
  isSettingsSnapshot(value.snapshot)

export const isSettingsSnapshot = (value: unknown): value is SettingsSnapshot =>
  isRecord(value) && date(value.generatedAt) &&
  (!('appearancePresetId' in value) || typeof value.appearancePresetId === 'string') &&
  (!('font' in value) || value.font === null || isFont(value.font)) &&
  (!('fonts' in value) || value.fonts === null || Array.isArray(value.fonts) && value.fonts.every(isFont)) &&
  isLanguage(value.language) &&
  typeof value.showLogsInSidebar === 'boolean' &&
  (value.mainWindowCloseBehavior === 'Ask' || value.mainWindowCloseBehavior === 'MinimizeToNotificationArea' || value.mainWindowCloseBehavior === 'ExitApplication') &&
  typeof value.closeBehaviorMessage === 'string' && typeof value.launchAtLogin === 'boolean' &&
  typeof value.canConfigureLaunchAtLogin === 'boolean' && typeof value.canRepairStartup === 'boolean' &&
  (value.startupPresentationMode === 'MainWindow' || value.startupPresentationMode === 'Minimized' || value.startupPresentationMode === 'FloatingBadge') &&
  typeof value.startupBackend === 'string' && typeof value.startupStatus === 'string' &&
  typeof value.startupMessage === 'string'
