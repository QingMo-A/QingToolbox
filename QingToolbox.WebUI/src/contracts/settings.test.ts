import { describe, expect, it } from 'vitest'
import { DEFAULT_FONT_ID, isSettingsSnapshot, normalizeFont, normalizeSettingsSnapshot } from './settings'

const language = {
  code: 'system',
  effectiveCode: 'en-US',
  displayName: 'System Default',
  options: [
    { code: 'system', displayName: 'System Default', nativeName: '跟随系统' },
    { code: 'zh-CN', displayName: 'Simplified Chinese', nativeName: '简体中文' },
    { code: 'en-US', displayName: 'English', nativeName: 'English' },
  ],
}
const valid = { generatedAt: '2026-07-25T12:00:00Z', language, showLogsInSidebar: true, mainWindowCloseBehavior: 'Ask', closeBehaviorMessage: 'Ready', launchAtLogin: false, canConfigureLaunchAtLogin: true, canRepairStartup: false, startupPresentationMode: 'FloatingBadge', startupBackend: 'Registry Run', startupStatus: 'Healthy', startupMessage: 'Ready' }

describe('settings snapshot contract', () => {
  it('accepts the complete safe host DTO', () => expect(isSettingsSnapshot(valid)).toBe(true))
  it('rejects invalid generatedAt', () => expect(isSettingsSnapshot({ ...valid, generatedAt: 'later' })).toBe(false))
  it.each([
    { ...language, code: 'zh-cn' },
    { ...language, effectiveCode: 'system' },
    { code: 'system', displayName: 'System Default', options: language.options },
    { ...language, options: 'system' },
    { ...language, options: language.options.slice(0, 2) },
    { ...language, options: [...language.options, language.options[0]] },
    { ...language, options: language.options.map((option, index) => index ? option : { ...option, nativeName: 1 }) },
  ])('rejects an invalid or incomplete language projection', invalidLanguage => {
    expect(isSettingsSnapshot({ ...valid, language: invalidLanguage })).toBe(false)
  })
  it('rejects invalid logs preference', () => expect(isSettingsSnapshot({ ...valid, showLogsInSidebar: 'yes' })).toBe(false))
  it.each(['Close', 'Minimize', ''])('rejects close behavior %s', mainWindowCloseBehavior => expect(isSettingsSnapshot({ ...valid, mainWindowCloseBehavior })).toBe(false))
  it.each(['Badge', 'Hidden', ''])('rejects presentation %s', startupPresentationMode => expect(isSettingsSnapshot({ ...valid, startupPresentationMode })).toBe(false))
  it('rejects invalid startup booleans', () => expect(isSettingsSnapshot({ ...valid, launchAtLogin: 'no' })).toBe(false))
  it('rejects invalid display state', () => expect(isSettingsSnapshot({ ...valid, startupStatus: 42 })).toBe(false))
  it('accepts an optional host preset id and leaves unknown ids for UI normalization', () => {
    expect(isSettingsSnapshot({ ...valid, appearancePresetId: 'future-preset' })).toBe(true)
  })
  it('rejects a malformed host preset id', () => {
    expect(isSettingsSnapshot({ ...valid, appearancePresetId: { value: 'neon-circuit' } })).toBe(false)
  })
  it('accepts a safe font projection and normalizes unknown ids to Default', () => {
    const imported = { id: `imported:${'a'.repeat(64)}`, source: 'imported', displayName: 'Imported Sans', familyName: 'Imported Sans', resourceUrl: `https://app.qingtoolbox.local/user-fonts/${'a'.repeat(64)}.ttf` }
    expect(isSettingsSnapshot({ ...valid, font: imported, fonts: [imported] })).toBe(true)
    expect(normalizeFont({ id: 'imported:../../private', source: 'imported', displayName: 'bad', resourceUrl: 'file:///C:/private.ttf' }).id).toBe(DEFAULT_FONT_ID)
    expect(normalizeSettingsSnapshot({ ...valid, font: { id: 'future-font', source: 'system', displayName: 'Future', familyName: 'Future' } } as any).font?.id).toBe(DEFAULT_FONT_ID)
  })
  it('rejects font DTOs that carry non-string fields', () => {
    expect(isSettingsSnapshot({ ...valid, font: { id: 'Default', source: 'default', displayName: 42 } })).toBe(false)
  })
})
