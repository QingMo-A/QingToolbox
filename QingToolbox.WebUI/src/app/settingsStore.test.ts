import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { useSettingsStore } from './settingsStore'
import type { SettingsSnapshot } from '../contracts/settings'

const snapshot: SettingsSnapshot = {
  generatedAt: '2026-07-25T12:00:00Z',
  appearancePresetId: 'qing-default',
  language: {
    code: 'en-US',
    effectiveCode: 'en-US',
    displayName: 'English',
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

describe('Settings Store language mutation', () => {
  beforeEach(() => setActivePinia(createPinia()))

  it('does not request the current language', async () => {
    const store = useSettingsStore(); store.complete(snapshot)
    const setLanguage = vi.fn()
    await expect(store.updateLanguage({ setLanguage } as any, 'en-US')).resolves.toBe('unchanged')
    expect(setLanguage).not.toHaveBeenCalled()
  })

  it('keeps the confirmed snapshot pending and applies the complete response', async () => {
    let resolve!: (value: SettingsSnapshot) => void
    const pending = new Promise<SettingsSnapshot>(done => { resolve = done })
    const store = useSettingsStore(); store.complete(snapshot)
    const operation = store.updateLanguage({ setLanguage: vi.fn(() => pending) } as any, 'zh-CN')
    expect(store.snapshot).toEqual(snapshot)
    expect(store.isUpdatingLanguage).toBe(true)
    const changed = { ...snapshot, generatedAt: '2026-07-25T13:00:00Z', language: { ...snapshot.language, code: 'zh-CN' as const, effectiveCode: 'zh-CN' as const, displayName: 'Simplified Chinese' } }
    resolve(changed)
    await expect(operation).resolves.toBe('success')
    expect(store.snapshot).toEqual(changed)
  })

  it('restores the old snapshot and exposes only the fixed safe error', async () => {
    const store = useSettingsStore(); store.complete(snapshot)
    const result = await store.updateLanguage({ setLanguage: vi.fn().mockRejectedValue(new Error('C:\\private\\settings.json')) } as any, 'system')
    expect(result).toBe('failure')
    expect(store.snapshot).toEqual(snapshot)
    expect(store.status).toBe('ready')
    expect(store.languageError).toBe('The language setting could not be updated.')
    expect(store.languageError).not.toContain('settings.json')
  })

  it('blocks language overlap with another host mutation and blocks host writes while saving language', async () => {
    const store = useSettingsStore(); store.complete(snapshot)
    store.isUpdatingLogsVisibility = true
    expect(await store.updateLanguage({ setLanguage: vi.fn() } as any, 'system')).toBe('busy')
    store.isUpdatingLogsVisibility = false
    store.isUpdatingLanguage = true
    expect(await store.updateCloseBehavior({ setMainWindowCloseBehavior: vi.fn() } as any, 'ExitApplication')).toBe('busy')
  })
})

describe('Settings Store appearance mutation', () => {
  beforeEach(() => setActivePinia(createPinia()))

  it('commits only the complete host-confirmed preset snapshot', async () => {
    let resolve!: (value: SettingsSnapshot) => void
    const pending = new Promise<SettingsSnapshot>(done => { resolve = done })
    const store = useSettingsStore(); store.complete(snapshot)
    const operation = store.updateAppearancePreset({ setAppearancePreset: vi.fn(() => pending) } as any, 'qing-nova')
    expect(store.snapshot).toEqual(snapshot)
    expect(store.isUpdatingAppearance).toBe(true)
    const changed = { ...snapshot, generatedAt: '2026-07-25T13:00:00Z', appearancePresetId: 'qing-nova' }
    resolve(changed)
    await expect(operation).resolves.toBe('success')
    expect(store.snapshot).toEqual(changed)
  })

  it('restores the previous confirmed preset when persistence fails', async () => {
    const store = useSettingsStore(); store.complete(snapshot)
    const result = await store.updateAppearancePreset({
      setAppearancePreset: vi.fn().mockRejectedValue(new Error('C:\\private\\settings.json')),
    } as any, 'aurora-flow')
    expect(result).toBe('failure')
    expect(store.snapshot).toEqual(snapshot)
    expect(store.status).toBe('ready')
    expect(store.appearanceError).not.toContain('settings.json')
  })
})
