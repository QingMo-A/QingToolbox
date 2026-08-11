import { describe, expect, it, vi } from 'vitest'
import { SettingsClient } from './SettingsClient'

const snapshot = {
  generatedAt: new Date().toISOString(),
  language: {
    code: 'zh-CN',
    effectiveCode: 'zh-CN',
    displayName: 'Simplified Chinese',
    options: [
      { code: 'system', displayName: 'System Default', nativeName: '跟随系统' },
      { code: 'zh-CN', displayName: 'Simplified Chinese', nativeName: '简体中文' },
      { code: 'en-US', displayName: 'English', nativeName: 'English' },
    ],
  },
  showLogsInSidebar: true,
  mainWindowCloseBehavior: 'Ask',
  closeBehaviorMessage: '',
  launchAtLogin: false,
  canConfigureLaunchAtLogin: true,
  canRepairStartup: false,
  startupPresentationMode: 'FloatingBadge',
  startupBackend: 'None',
  startupStatus: 'Disabled',
  startupMessage: '',
}

describe('SettingsClient language mutation', () => {
  it('uses the exact command and payload and validates the returned snapshot', async () => {
    const request = vi.fn().mockResolvedValue(snapshot)
    const client = new SettingsClient({ request } as any)
    await expect(client.setLanguage('zh-CN')).resolves.toEqual(snapshot)
    expect(request).toHaveBeenCalledWith('settings.setLanguage', { languageCode: 'zh-CN' })
  })

  it('rejects an incomplete returned snapshot', async () => {
    const request = vi.fn().mockResolvedValue({ ...snapshot, language: { code: 'zh-CN' } })
    await expect(new SettingsClient({ request } as any).setLanguage('zh-CN'))
      .rejects.toThrow('Settings snapshot validation failed.')
  })
})

describe('SettingsClient appearance preset mutation', () => {
  it('uses the host command and preserves the preset id in the response', async () => {
    const request = vi.fn().mockResolvedValue({ ...snapshot, appearancePresetId: 'neon-circuit' })
    const client = new SettingsClient({ request } as any)
    await expect(client.setAppearancePreset('neon-circuit')).resolves.toMatchObject({ appearancePresetId: 'neon-circuit' })
    expect(request).toHaveBeenCalledWith('settings.setAppearancePreset', { appearancePresetId: 'neon-circuit' })
  })
})

describe('SettingsClient font mutations', () => {
  it('uses the exact font command and payload', async () => {
    const request = vi.fn().mockResolvedValue({ ...snapshot, font: { id: 'Default', source: 'default', displayName: 'Default' }, fonts: [] })
    const client = new SettingsClient({ request } as any)
    await expect(client.setFont('Default')).resolves.toMatchObject({ font: { id: 'Default' } })
    expect(request).toHaveBeenCalledWith('settings.setFont', { fontId: 'Default' })
  })

  it('distinguishes an import cancellation from a completed import', async () => {
    const response = { disposition: 'Cancelled', snapshot: { ...snapshot, font: { id: 'Default', source: 'default', displayName: 'Default' }, fonts: [] } }
    const request = vi.fn().mockResolvedValue(response)
    const client = new SettingsClient({ request } as any)
    await expect(client.importFont()).resolves.toMatchObject({ disposition: 'Cancelled' })
    expect(request).toHaveBeenCalledWith('settings.importFont', {})
  })

  it('refreshes the system catalog with an empty payload', async () => {
    const request = vi.fn().mockResolvedValue({ ...snapshot, font: { id: 'Default', source: 'default', displayName: 'Default' }, fonts: [] })
    const client = new SettingsClient({ request } as any)
    await expect(client.refreshFonts()).resolves.toMatchObject({ font: { id: 'Default' } })
    expect(request).toHaveBeenCalledWith('settings.refreshFonts', {})
  })
})
