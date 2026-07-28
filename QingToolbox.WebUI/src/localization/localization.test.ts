import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { useSettingsStore } from '../app/settingsStore'
import type { EffectiveLanguageCode, LanguageCode, SettingsSnapshot } from '../contracts/settings'
import { translate, useLocalization } from './localization'
import { enUSMessages, type TranslationKey } from './messages/en-US'
import { zhCNMessages } from './messages/zh-CN'

const snapshot = (code: LanguageCode, effectiveCode: EffectiveLanguageCode): SettingsSnapshot => ({
  generatedAt: new Date().toISOString(),
  language: { code, effectiveCode, displayName: code, options: [
    { code: 'system', displayName: 'System Default', nativeName: '跟随系统' },
    { code: 'zh-CN', displayName: 'Simplified Chinese', nativeName: '简体中文' },
    { code: 'en-US', displayName: 'English', nativeName: 'English' },
  ] },
  showLogsInSidebar: true, mainWindowCloseBehavior: 'Ask', closeBehaviorMessage: '',
  launchAtLogin: false, canConfigureLaunchAtLogin: true, canRepairStartup: false,
  startupPresentationMode: 'FloatingBadge', startupBackend: 'None', startupStatus: 'Unavailable', startupMessage: '',
})

beforeEach(() => setActivePinia(createPinia()))

describe('localization', () => {
  it('defaults to English until a Settings Snapshot exists', () => {
    expect(useLocalization().currentLocale.value).toBe('en-US')
  })

  it.each([
    ['system', 'zh-CN', '首页'],
    ['zh-CN', 'zh-CN', '首页'],
    ['en-US', 'en-US', 'Home'],
  ] as const)('uses effective language for %s', (code, effective, expected) => {
    useSettingsStore().complete(snapshot(code, effective))
    const localization = useLocalization()
    expect(localization.currentLocale.value).toBe(effective)
    expect(localization.t('navigation.home')).toBe(expected)
  })

  it('reacts when the complete Settings Snapshot is replaced', () => {
    const store = useSettingsStore(); const { currentLocale, t } = useLocalization()
    store.complete(snapshot('en-US', 'en-US')); expect(t('navigation.modules')).toBe('Modules')
    store.complete(snapshot('zh-CN', 'zh-CN')); expect(currentLocale.value).toBe('zh-CN'); expect(t('navigation.modules')).toBe('模块')
  })

  it('translates both resources and performs simple text interpolation', () => {
    expect(translate('en-US', 'navigation.settings')).toBe('Settings')
    expect(translate('zh-CN', 'navigation.settings')).toBe('设置')
    expect(translate('en-US', 'example.count', { count: 3 })).toBe('3 items')
    expect(translate('en-US', 'example.count')).toBe('{count} items')
    expect(translate('en-US', 'example.count', { count: '<b>3</b>' })).toBe('<b>3</b> items')
  })

  it('falls back to English, then returns and warns with the missing key', () => {
    const mutableChinese = zhCNMessages as Partial<Record<TranslationKey, string>>
    const chinese = mutableChinese['common.open']; delete mutableChinese['common.open']
    expect(translate('zh-CN', 'common.open')).toBe('Open')
    mutableChinese['common.open'] = chinese

    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {})
    const missing = 'missing.runtime.key' as TranslationKey
    expect(translate('zh-CN', missing)).toBe(missing)
    expect(warn).toHaveBeenCalled()
    warn.mockRestore()
  })

  it('keeps the Chinese resource complete for every English key', () => {
    expect(Object.keys(zhCNMessages).sort()).toEqual(Object.keys(enUSMessages).sort())
  })
})
