import { describe, expect, it } from 'vitest'
import {
  closeBehaviorPresentation,
  settingsSections,
  startupPresentation,
  themeModeLabelKey,
} from './settingsPresentation'

describe('settings presentation', () => {
  it('defines the four stable settings sections', () => {
    expect(settingsSections.map(section => section.id)).toEqual(['general', 'window', 'startup', 'about'])
    expect(settingsSections.every(section => section.titleKey.startsWith('settings.section.'))).toBe(true)
  })

  it('maps every theme mode to a translation key', () => {
    expect(['system', 'light', 'dark'].map(mode => themeModeLabelKey(mode as 'system' | 'light' | 'dark'))).toEqual([
      'settings.appearance.system',
      'settings.appearance.light',
      'settings.appearance.dark',
    ])
  })

  it('maps every close behavior without consulting stores or clients', () => {
    expect(closeBehaviorPresentation('Ask').labelKey).toBe('settings.window.ask')
    expect(closeBehaviorPresentation('MinimizeToNotificationArea').labelKey).toBe('settings.window.minimize')
    expect(closeBehaviorPresentation('ExitApplication').labelKey).toBe('settings.window.exit')
  })

  it('maps every startup presentation without consulting stores or clients', () => {
    expect(startupPresentation('MainWindow').labelKey).toBe('settings.startup.mainWindow')
    expect(startupPresentation('Minimized').labelKey).toBe('settings.startup.minimized')
    expect(startupPresentation('FloatingBadge').labelKey).toBe('settings.startup.floatingBadge')
  })
})
