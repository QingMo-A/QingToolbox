import { defineStore } from 'pinia'
import type {
  LanguageCode,
  MainWindowCloseBehavior,
  SettingsSnapshot,
  StartupPresentationMode,
} from '../contracts/settings'
import type { SettingsClient } from '../bridge/clients/SettingsClient'

const languageFailure = 'The language setting could not be updated.'
const appearanceFailure = 'The appearance preset could not be updated.'
const fontFailure = 'The font setting could not be updated.'

export const useSettingsStore = defineStore('settings', {
  state: () => ({
    status: 'idle' as 'idle'|'loading'|'ready'|'error',
    snapshot: null as SettingsSnapshot|null,
    generatedAt: null as string|null,
    errorMessage: '',
    isUpdatingLanguage: false,
    languageError: '',
    isUpdatingAppearance: false,
    appearanceError: '',
    isUpdatingFont: false,
    fontError: '',
    isRefreshingFonts: false,
    fontRefreshError: '',
    isUpdatingLogsVisibility: false,
    logsVisibilityError: '',
    isUpdatingCloseBehavior: false,
    closeBehaviorError: '',
    isUpdatingStartupPresentation: false,
    startupPresentationError: '',
    launchAtLoginBusy: false,
    launchAtLoginError: '',
    startupRepairBusy: false,
    startupRepairError: '',
  }),
  getters: {
    isHostMutationBusy: state => state.isUpdatingLanguage || state.isUpdatingAppearance || state.isUpdatingLogsVisibility ||
      state.isUpdatingCloseBehavior || state.isUpdatingStartupPresentation ||
      state.launchAtLoginBusy || state.startupRepairBusy || state.isUpdatingFont || state.isRefreshingFonts,
  },
  actions: {
    begin() { this.status = 'loading'; this.errorMessage = '' },
    complete(snapshot: SettingsSnapshot) {
      this.snapshot = snapshot
      this.generatedAt = snapshot.generatedAt
      this.status = 'ready'
      this.errorMessage = ''
    },
    fail(error: unknown) {
      this.status = 'error'
      this.errorMessage = error instanceof Error ? error.message : String(error)
    },
    async updateLanguage(client: SettingsClient, languageCode: LanguageCode) {
      const previous = this.snapshot
      if (!previous) return 'failure' as const
      if (previous.language.code === languageCode) return 'unchanged' as const
      if (this.isHostMutationBusy) return 'busy' as const
      this.isUpdatingLanguage = true
      this.languageError = ''
      try {
        this.complete(await client.setLanguage(languageCode))
        return 'success' as const
      } catch {
        this.snapshot = previous
        this.generatedAt = previous.generatedAt
        this.status = 'ready'
        this.languageError = languageFailure
        return 'failure' as const
      } finally {
        this.isUpdatingLanguage = false
      }
    },
    async updateAppearancePreset(client: SettingsClient, appearancePresetId: string) {
      const previous = this.snapshot
      if (!previous) return 'failure' as const
      if (previous.appearancePresetId === appearancePresetId) return 'unchanged' as const
      if (this.isHostMutationBusy) return 'busy' as const
      this.isUpdatingAppearance = true
      this.appearanceError = ''
      try {
        this.complete(await client.setAppearancePreset(appearancePresetId))
        return 'success' as const
      } catch {
        this.snapshot = previous
        this.generatedAt = previous.generatedAt
        this.status = 'ready'
        this.appearanceError = appearanceFailure
        return 'failure' as const
      } finally {
        this.isUpdatingAppearance = false
      }
    },
    async updateFont(client: SettingsClient, fontId: string) {
      const previous = this.snapshot
      if (!previous) return 'failure' as const
      if (previous.font?.id === fontId) return 'unchanged' as const
      if (this.isHostMutationBusy) return 'busy' as const
      this.isUpdatingFont = true; this.fontError = ''
      try { this.complete(await client.setFont(fontId)); return 'success' as const }
      catch { this.snapshot = previous; this.generatedAt = previous.generatedAt; this.status = 'ready'; this.fontError = fontFailure; return 'failure' as const }
      finally { this.isUpdatingFont = false }
    },
    async importFont(client: SettingsClient) {
      const previous = this.snapshot
      if (!previous || this.isHostMutationBusy) return 'failure' as const
      this.isUpdatingFont = true; this.fontError = ''
      try {
        const response = await client.importFont()
        if (response.disposition === 'Cancelled') return 'cancelled' as const
        this.complete(response.snapshot)
        return 'success' as const
      }
      catch { this.snapshot = previous; this.generatedAt = previous.generatedAt; this.status = 'ready'; this.fontError = fontFailure; return 'failure' as const }
      finally { this.isUpdatingFont = false }
    },
    async refreshFonts(client: SettingsClient) {
      const previous = this.snapshot
      if (!previous) return 'failure' as const
      if (this.isHostMutationBusy) return 'busy' as const
      this.isRefreshingFonts = true; this.fontRefreshError = ''
      try { this.complete(await client.refreshFonts()); return 'success' as const }
      catch { this.snapshot = previous; this.generatedAt = previous.generatedAt; this.status = 'ready'; this.fontRefreshError = fontFailure; return 'failure' as const }
      finally { this.isRefreshingFonts = false }
    },
    async updateLogsVisibility(client: SettingsClient, value: boolean) {
      if (this.isUpdatingLanguage || this.isUpdatingLogsVisibility) return 'busy' as const
      const previous = this.snapshot
      if (!previous) return 'failure' as const
      this.isUpdatingLogsVisibility = true; this.logsVisibilityError = ''
      try { this.complete(await client.setShowLogsInSidebar(value)); return 'success' as const }
      catch (error) { this.snapshot = previous; this.status = 'ready'; this.logsVisibilityError = error instanceof Error ? error.message : String(error); return 'failure' as const }
      finally { this.isUpdatingLogsVisibility = false }
    },
    async updateCloseBehavior(client: SettingsClient, value: MainWindowCloseBehavior) {
      if (this.isUpdatingLanguage || this.isUpdatingCloseBehavior) return 'busy' as const
      const previous = this.snapshot
      if (!previous) return 'failure' as const
      this.isUpdatingCloseBehavior = true; this.closeBehaviorError = ''
      try { this.complete(await client.setMainWindowCloseBehavior(value)); return 'success' as const }
      catch (error) { this.snapshot = previous; this.status = 'ready'; this.closeBehaviorError = error instanceof Error ? error.message : String(error); return 'failure' as const }
      finally { this.isUpdatingCloseBehavior = false }
    },
    async updateStartupPresentation(client: SettingsClient, value: StartupPresentationMode) {
      if (this.isUpdatingLanguage || this.isUpdatingStartupPresentation || this.startupRepairBusy) return 'busy' as const
      const previous = this.snapshot
      if (!previous) return 'failure' as const
      this.isUpdatingStartupPresentation = true; this.startupPresentationError = ''
      try { this.complete(await client.setStartupPresentationMode(value)); return 'success' as const }
      catch (error) { this.snapshot = previous; this.status = 'ready'; this.startupPresentationError = error instanceof Error ? error.message : String(error); return 'failure' as const }
      finally { this.isUpdatingStartupPresentation = false }
    },
    async updateLaunchAtLogin(client: SettingsClient, value: boolean) {
      if (this.isUpdatingLanguage || this.launchAtLoginBusy || this.startupRepairBusy) return 'busy' as const
      const previous = this.snapshot
      if (!previous) return 'failure' as const
      this.launchAtLoginBusy = true; this.launchAtLoginError = ''
      try { this.complete(await client.setLaunchAtLogin(value)); return 'success' as const }
      catch { this.snapshot = previous; this.status = 'ready'; this.launchAtLoginError = 'The Windows startup setting could not be updated.'; return 'failure' as const }
      finally { this.launchAtLoginBusy = false }
    },
    async repairStartupRegistration(client: SettingsClient) {
      if (this.isUpdatingLanguage || this.startupRepairBusy) return 'busy' as const
      const previous = this.snapshot
      if (!previous || !previous.canRepairStartup) return 'failure' as const
      this.startupRepairBusy = true; this.startupRepairError = ''
      try { this.complete(await client.repairStartupRegistration()); return 'success' as const }
      catch { this.snapshot = previous; this.status = 'ready'; this.startupRepairError = 'The Windows startup registration could not be repaired.'; return 'failure' as const }
      finally { this.startupRepairBusy = false }
    },
  },
})
