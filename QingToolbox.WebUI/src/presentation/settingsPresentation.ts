import type {
  MainWindowCloseBehavior,
  StartupPresentationMode,
} from '../contracts/settings'
import type { ThemeMode } from '../app/themeStore'
import type { TranslationKey } from '../localization/messages/en-US'

export type SettingsSection = 'general' | 'window' | 'startup' | 'about'

export interface SettingsSectionDefinition {
  id: SettingsSection
  titleKey: TranslationKey
  descriptionKey: TranslationKey
  icon: 'settings' | 'close' | 'running' | 'statusInfo'
}

export const settingsSections: readonly SettingsSectionDefinition[] = [
  { id: 'general', titleKey: 'settings.section.general', descriptionKey: 'settings.section.generalDescription', icon: 'settings' },
  { id: 'window', titleKey: 'settings.section.window', descriptionKey: 'settings.section.windowDescription', icon: 'close' },
  { id: 'startup', titleKey: 'settings.section.startup', descriptionKey: 'settings.section.startupDescription', icon: 'running' },
  { id: 'about', titleKey: 'settings.section.about', descriptionKey: 'settings.section.aboutDescription', icon: 'statusInfo' },
]

export function themeModeLabelKey(mode: ThemeMode): TranslationKey {
  return ({
    system: 'settings.appearance.system',
    light: 'settings.appearance.light',
    dark: 'settings.appearance.dark',
  } satisfies Record<ThemeMode, TranslationKey>)[mode]
}

export function closeBehaviorPresentation(value: MainWindowCloseBehavior): {
  labelKey: TranslationKey
  descriptionKey: TranslationKey
} {
  return ({
    Ask: { labelKey: 'settings.window.ask', descriptionKey: 'settings.window.askDescription' },
    MinimizeToNotificationArea: { labelKey: 'settings.window.minimize', descriptionKey: 'settings.window.minimizeDescription' },
    ExitApplication: { labelKey: 'settings.window.exit', descriptionKey: 'settings.window.exitDescription' },
  } satisfies Record<MainWindowCloseBehavior, { labelKey: TranslationKey; descriptionKey: TranslationKey }>)[value]
}

export function startupPresentation(value: StartupPresentationMode): {
  labelKey: TranslationKey
  descriptionKey: TranslationKey
} {
  return ({
    MainWindow: { labelKey: 'settings.startup.mainWindow', descriptionKey: 'settings.startup.mainWindowDescription' },
    Minimized: { labelKey: 'settings.startup.minimized', descriptionKey: 'settings.startup.minimizedDescription' },
    FloatingBadge: { labelKey: 'settings.startup.floatingBadge', descriptionKey: 'settings.startup.floatingBadgeDescription' },
  } satisfies Record<StartupPresentationMode, { labelKey: TranslationKey; descriptionKey: TranslationKey }>)[value]
}
