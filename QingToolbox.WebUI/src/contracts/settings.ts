import { isRecord } from './app'

export type MainWindowCloseBehavior = 'Ask'|'MinimizeToNotificationArea'|'ExitApplication'
export type StartupPresentationMode = 'MainWindow'|'Minimized'|'FloatingBadge'
export type LanguageCode = 'system'|'zh-CN'|'en-US'
export type EffectiveLanguageCode = 'zh-CN'|'en-US'

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

export const isSettingsSnapshot = (value: unknown): value is SettingsSnapshot =>
  isRecord(value) && date(value.generatedAt) && isLanguage(value.language) &&
  typeof value.showLogsInSidebar === 'boolean' &&
  (value.mainWindowCloseBehavior === 'Ask' || value.mainWindowCloseBehavior === 'MinimizeToNotificationArea' || value.mainWindowCloseBehavior === 'ExitApplication') &&
  typeof value.closeBehaviorMessage === 'string' && typeof value.launchAtLogin === 'boolean' &&
  typeof value.canConfigureLaunchAtLogin === 'boolean' && typeof value.canRepairStartup === 'boolean' &&
  (value.startupPresentationMode === 'MainWindow' || value.startupPresentationMode === 'Minimized' || value.startupPresentationMode === 'FloatingBadge') &&
  typeof value.startupBackend === 'string' && typeof value.startupStatus === 'string' &&
  typeof value.startupMessage === 'string'
