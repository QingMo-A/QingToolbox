<script setup lang="ts">
import { computed, inject, ref, watch } from 'vue'
import { invoke } from '@tauri-apps/api/core'
import type { SettingsClient } from '../bridge/clients/SettingsClient'
import type { ModuleClient } from '../bridge/clients/ModuleClient'
import { normalizeFont, normalizeFontOptions, type InfoPopupCorner, type LanguageCode, type MainWindowCloseBehavior, type SettingsFont, type StartupPresentationMode } from '../contracts/settings'
import { useAppStore } from '../app/store'
import { useSettingsStore } from '../app/settingsStore'
import { useModuleStore } from '../app/moduleStore'
import { useThemeStore, type ThemeMode } from '../app/themeStore'
import { useAppearancePresetStore, normalizeAppearancePresetId, type AppearancePresetId } from '../design-system/tokens/appearancePresets'
import { useToastStore } from '../app/toastStore'
import QPage from '../design-system/components/QPage.vue'
import QButton from '../design-system/components/QButton.vue'
import QIcon from '../design-system/components/QIcon.vue'
import QBadge from '../design-system/components/QBadge.vue'
import QSkeleton from '../design-system/components/QSkeleton.vue'
import QHostUpdatePanel from '../design-system/components/QHostUpdatePanel.vue'
import brandMark from '../assets/QingToolbox.Mark.svg'
import { projectAuthor, projectRepositoryUrl } from '../contracts/project'
import { useLocalization } from '../localization/localization'
import { bridgeStateKey } from '../presentation/workspacePresentation'
import { TauriTransport } from '../bridge/transport/TauriTransport'
import {
  closeBehaviorPresentation,
  settingsSections,
  startupPresentation,
  themeModeLabelKey,
  appearancePresetOptions,
  appearancePresetPresentation,
  type SettingsSection,
} from '../presentation/settingsPresentation'

const client = inject<SettingsClient>('settingsClient')!
const moduleClient = inject<ModuleClient>('moduleClient')!
const app = useAppStore()
const settings = useSettingsStore()
const modules = useModuleStore()
const theme = useThemeStore()
const appearance = useAppearancePresetStore()
const toast = useToastStore()
const { currentLocale, t } = useLocalization()
const activeSection = ref<SettingsSection>('general')
const isSynchronizingLanguage = ref(false)
const isSynchronizingAppearance = ref(false)
const fontSearch = ref('')
const isSynchronizingFont = ref(false)
const themes: ThemeMode[] = ['system', 'light', 'dark']
const closeBehaviors: MainWindowCloseBehavior[] = ['Ask', 'MinimizeToNotificationArea', 'ExitApplication']
const startupPresentations: StartupPresentationMode[] = ['MainWindow', 'Minimized', 'FloatingBadge']
const windowWidth = ref('1100')
const windowHeight = ref('720')
const isSavingWindow = ref(false)
const isSavingInfoPopup = ref(false)
const infoPopupDuration = ref('380')
const infoPopupDismissSeconds = ref('15')
const infoPopupCorners: InfoPopupCorner[] = ['rightTop', 'rightBottom', 'leftTop', 'leftBottom']
const infoPopupDurationValid = computed(() => {
  const value = Number(infoPopupDuration.value)
  return Number.isInteger(value) && value >= 100 && value <= 2000
})
const infoPopupDismissValid = computed(() => {
  const value = Number(infoPopupDismissSeconds.value)
  return Number.isInteger(value) && value >= 3 && value <= 60
})
const windowSizeValid = computed(() => {
  const width = Number(windowWidth.value)
  const height = Number(windowHeight.value)
  return Number.isInteger(width) && width >= 760 && width <= 7680 &&
    Number.isInteger(height) && height >= 520 && height <= 4320
})

const hasSnapshot = computed(() => settings.snapshot !== null)
const refreshed = computed(() => settings.generatedAt ? new Date(settings.generatedAt).toLocaleTimeString(currentLocale.value) : null)
const productVersion = computed(() => app.snapshot?.hostVersion || '0.2.1-alpha')
const moduleApiVersion = computed(() => app.snapshot?.apiVersion ? `v${app.snapshot.apiVersion}` : '—')
const environment = computed(() => app.snapshot?.environmentDisplayName || app.mode || 'Development')
const hostControlsDisabled = computed(() => app.bridge !== 'Connected')
const languageControlsDisabled = computed(() => hostControlsDisabled.value || settings.status === 'loading' || settings.isHostMutationBusy || isSynchronizingLanguage.value)
const hostMutationDisabled = computed(() => hostControlsDisabled.value || settings.isUpdatingLanguage || isSynchronizingLanguage.value)
const appearanceControlsDisabled = computed(() => hostControlsDisabled.value || !settings.snapshot ||
  settings.status === 'loading' || settings.isHostMutationBusy || isSynchronizingAppearance.value)
const appearancePresetId = computed<AppearancePresetId>(() => normalizeAppearancePresetId(settings.snapshot?.appearancePresetId ?? appearance.id))
const selectedFont = computed(() => normalizeFont(settings.snapshot?.font))
const fontOptions = computed(() => normalizeFontOptions(settings.snapshot?.fonts))
const filteredFonts = computed(() => {
  const query = fontSearch.value.trim().toLocaleLowerCase(currentLocale.value)
  return fontOptions.value.filter(option => !query || `${option.displayName} ${option.familyName ?? ''}`.toLocaleLowerCase(currentLocale.value).includes(query))
})
const systemFonts = computed(() => filteredFonts.value.filter(font => font.source === 'system'))
const importedFonts = computed(() => filteredFonts.value.filter(font => font.source === 'imported'))
const fontControlsDisabled = computed(() => hostControlsDisabled.value || !settings.snapshot || settings.status === 'loading' || settings.isHostMutationBusy || isSynchronizingFont.value)
const languageName = (code: string) => code === 'system'
  ? t('settings.appearance.system')
  : settings.snapshot?.language.options.find(option => option.code === code)?.nativeName ?? code
const languageAuxiliaryName = (displayName: string, nativeName: string) => currentLocale.value === 'en-US'
  ? displayName
  : [nativeName, displayName].filter((value, index, values) => value && values.indexOf(value) === index).join(' · ')
const configuredLanguageName = computed(() => languageName(settings.snapshot?.language.code ?? ''))
const effectiveLanguageName = computed(() => languageName(settings.snapshot?.language.effectiveCode ?? ''))
const snapshotNotice = computed(() => {
  if (!hasSnapshot.value) return ''
  if (settings.status === 'error') return t('settings.status.refreshFailed')
  if (app.bridge !== 'Connected') return t('settings.status.disconnected')
  if (settings.status === 'loading') return t('settings.status.refreshing')
  return ''
})
const startupTone = computed<'success'|'warning'|'danger'|'info'>(() => {
  const status = settings.snapshot?.startupStatus.toLocaleLowerCase()
  if (status === 'healthy') return 'success'
  if (status === 'failed' || status === 'error') return 'danger'
  if (status === 'unavailable' || status === 'disabled' || status === 'degraded') return 'warning'
  return 'info'
})

async function refresh() {
  settings.begin()
  try { settings.complete(await client.getSnapshot()) }
  catch (error) { settings.fail(error) }
}

async function openRepository() {
  try { await client.openRepository() }
  catch { toast.show(t('settings.about.openRepositoryFailed'), 'error') }
}

watch(() => app.bridge, bridge => {
  if (bridge === 'Connected' && settings.status === 'idle') void refresh()
}, { immediate: true })
watch(() => settings.snapshot?.appearancePresetId, value => {
  if (value !== undefined) appearance.set(value)
}, { immediate: true })
watch(() => [settings.snapshot?.windowWidth, settings.snapshot?.windowHeight], ([width, height]) => {
  if (width !== undefined) windowWidth.value = String(width)
  if (height !== undefined) windowHeight.value = String(height)
}, { immediate: true })
watch(() => settings.snapshot?.infoPopupDurationMs, value => {
  if (value !== undefined) infoPopupDuration.value = String(value)
}, { immediate: true })
watch(() => settings.snapshot?.infoPopupDismissSeconds, value => {
  if (value !== undefined) infoPopupDismissSeconds.value = String(value)
}, { immediate: true })

const yesNo = (value: boolean) => t(value ? 'settings.startup.yes' : 'settings.startup.no')
const bridgeLabel = computed(() => {
  const key = bridgeStateKey(app.bridge)
  return key ? t(key) : app.bridge
})
async function selectLanguage(languageCode: LanguageCode) {
  if (!settings.snapshot || languageControlsDisabled.value || settings.snapshot.language.code === languageCode) return
  isSynchronizingLanguage.value = true
  try {
    const result = await settings.updateLanguage(client, languageCode)
    if (result !== 'success') {
      if (result === 'failure') toast.show(t('settings.toast.languageFailed'), 'error')
      return
    }
    modules.begin()
    try {
      modules.complete(await moduleClient.getSnapshot())
      toast.show(t('settings.toast.languageSaved'), 'success')
    } catch (error) {
      modules.fail(error)
      toast.show(t('settings.toast.moduleRefreshFailed'), 'error')
    }
  } finally {
    isSynchronizingLanguage.value = false
  }
}
async function selectAppearancePreset(value: AppearancePresetId) {
  const next = normalizeAppearancePresetId(value)
  if (appearanceControlsDisabled.value || appearancePresetId.value === next) return
  const previous = appearancePresetId.value
  appearance.set(next)
  isSynchronizingAppearance.value = true
  try {
    const result = await settings.updateAppearancePreset(client, next)
    if (result === 'success' || result === 'unchanged') toast.show(t('settings.toast.appearanceSaved'), 'success')
    else {
      appearance.set(previous)
      toast.show(t('settings.toast.appearanceFailed'), 'error')
    }
  } catch {
    appearance.set(previous)
    toast.show(t('settings.toast.appearanceFailed'), 'error')
  } finally {
    isSynchronizingAppearance.value = false
  }
}
async function restoreDefaultAppearance() {
  await selectAppearancePreset('qing-default')
}
async function selectFont(font: SettingsFont) {
  if (fontControlsDisabled.value || selectedFont.value.id === font.id) return
  isSynchronizingFont.value = true
  try {
    const result = await settings.updateFont(client, font.id)
    if (result === 'success' || result === 'unchanged') {
      toast.show(t('settings.font.saving'), 'success')
    } else toast.show(t('settings.font.updateFailed'), 'error')
  } finally { isSynchronizingFont.value = false }
}
async function importFont() {
  if (fontControlsDisabled.value) return
  isSynchronizingFont.value = true
  try {
    const result = await settings.importFont(client)
    if (result === 'success') {
      toast.show(t('settings.font.saving'), 'success')
    } else if (result === 'failure') toast.show(t('settings.font.importFailed'), 'error')
  } finally { isSynchronizingFont.value = false }
}
async function refreshFonts() {
  if (fontControlsDisabled.value) return
  isSynchronizingFont.value = true
  try {
    const result = await settings.refreshFonts(client)
    if (result === 'failure') toast.show(t('settings.font.refreshFailed'), 'error')
  } finally { isSynchronizingFont.value = false }
}
async function restoreDefaultFont() { await selectFont(normalizeFont(null)) }
async function toggleLogs() {
  if (!settings.snapshot || hostMutationDisabled.value || settings.isUpdatingLogsVisibility) return
  const result = await settings.updateLogsVisibility(client, !settings.snapshot.showLogsInSidebar)
  if (result === 'success') toast.show(t('settings.toast.logsSaved'), 'success')
  else if (result === 'failure') toast.show(t('settings.toast.logsFailed'), 'error')
}
async function selectCloseBehavior(value: MainWindowCloseBehavior) {
  if (!settings.snapshot || hostMutationDisabled.value || settings.isUpdatingCloseBehavior || settings.snapshot.mainWindowCloseBehavior === value) return
  const result = await settings.updateCloseBehavior(client, value)
  if (result === 'success') toast.show(t('settings.toast.closeSaved'), 'success')
  else if (result === 'failure') toast.show(t('settings.toast.closeFailed'), 'error')
}
async function saveWindowSize(apply: boolean) {
  if (!settings.snapshot || hostMutationDisabled.value || settings.isHostMutationBusy || isSavingWindow.value) return
  if (!windowSizeValid.value) { toast.show(t('settings.window.sizeInvalid'), 'error'); return }
  isSavingWindow.value = true
  try {
    settings.complete(await client.setWindowSize(Number(windowWidth.value), Number(windowHeight.value)))
    if (apply) await client.applyWindowSize()
    toast.show(t(apply ? 'settings.window.sizeApplied' : 'settings.window.sizeSaved'), 'success')
  } catch {
    toast.show(t('settings.window.sizeFailed'), 'error')
  } finally { isSavingWindow.value = false }
}
async function updateInfoPopup(update: Record<string, unknown>) {
  if (!settings.snapshot || hostMutationDisabled.value || isSavingInfoPopup.value || !TauriTransport.isAvailable()) return
  isSavingInfoPopup.value = true
  try {
    await invoke('update_settings', { update })
    settings.complete(await client.getSnapshot())
    toast.show(t('settings.window.infoPopupSaved'), 'success')
  } catch { toast.show(t('settings.window.infoPopupFailed'), 'error') }
  finally { isSavingInfoPopup.value = false }
}
function saveInfoPopupTimings() {
  if (!infoPopupDismissValid.value || settings.snapshot?.infoPopupAnimation !== false && !infoPopupDurationValid.value) return
  void updateInfoPopup({
    infoPopupDismissSeconds: Number(infoPopupDismissSeconds.value),
    ...(settings.snapshot?.infoPopupAnimation === false ? {} : { infoPopupDurationMs: Number(infoPopupDuration.value) }),
  })
}
async function useCurrentWindowSize() {
  if (!settings.snapshot || hostMutationDisabled.value || settings.isHostMutationBusy || isSavingWindow.value) return
  isSavingWindow.value = true
  try {
    const size = await client.getCurrentWindowSize()
    windowWidth.value = String(size.width)
    windowHeight.value = String(size.height)
    toast.show(t('settings.window.currentSizeLoaded'), 'success')
  } catch { toast.show(t('settings.window.currentSizeFailed'), 'error') }
  finally { isSavingWindow.value = false }
}
async function toggleStartupFullscreen(event: Event) {
  if (!settings.snapshot || hostMutationDisabled.value || settings.isHostMutationBusy || isSavingWindow.value) return
  isSavingWindow.value = true
  try {
    settings.complete(await client.setStartupFullscreen(!settings.snapshot.startupFullscreen))
    toast.show(t('settings.window.fullscreenSaved'), 'success')
  } catch {
    (event.target as HTMLInputElement).checked = Boolean(settings.snapshot?.startupFullscreen)
    toast.show(t('settings.window.fullscreenFailed'), 'error')
  }
  finally { isSavingWindow.value = false }
}
async function selectStartupPresentation(value: StartupPresentationMode) {
  if (!settings.snapshot || hostMutationDisabled.value || settings.isUpdatingStartupPresentation || settings.snapshot.startupPresentationMode === value) return
  const result = await settings.updateStartupPresentation(client, value)
  if (result === 'success') toast.show(t('settings.toast.presentationSaved'), 'success')
  else if (result === 'failure') toast.show(t('settings.toast.presentationFailed'), 'error')
}
async function toggleLaunchAtLogin() {
  if (!settings.snapshot || hostMutationDisabled.value || !settings.snapshot.canConfigureLaunchAtLogin || settings.launchAtLoginBusy || settings.startupRepairBusy) return
  const enabled = !settings.snapshot.launchAtLogin
  const result = await settings.updateLaunchAtLogin(client, enabled)
  if (result === 'success') toast.show(t(enabled ? 'settings.toast.launchEnabled' : 'settings.toast.launchDisabled'), 'success')
  else if (result === 'failure') toast.show(t('settings.toast.launchFailed'), 'error')
}
async function repairStartup() {
  if (!settings.snapshot?.canRepairStartup || hostMutationDisabled.value || settings.startupRepairBusy || settings.launchAtLoginBusy || settings.status === 'loading') return
  const result = await settings.repairStartupRegistration(client)
  if (result === 'success') toast.show(t('settings.toast.repairSaved'), 'success')
  else if (result === 'failure') toast.show(t('settings.toast.repairFailed'), 'error')
}
</script>

<template>
  <QPage class="settings-page">
    <div class="settings-workspace-shell">
      <header class="settings-header">
        <div><h1>{{ t('settings.page.title') }}</h1></div>
        <QButton :disabled="app.bridge !== 'Connected' || settings.status === 'loading' || isSynchronizingLanguage" @click="refresh"><QIcon name="refresh" /> {{ t(settings.status === 'loading' ? 'settings.page.refreshing' : 'settings.page.refresh') }}</QButton>
      </header>
      <div class="settings-readonly"><QIcon name="statusInfo" /> {{ t('settings.page.readonly') }}</div>
      <p v-if="refreshed" class="settings-refreshed">{{ t('settings.page.lastRefreshed', { time: refreshed }) }}</p>
      <div v-if="snapshotNotice" class="settings-snapshot-notice" role="status"><QIcon :name="settings.status === 'error' ? 'statusWarning' : 'statusInfo'" /><span>{{ snapshotNotice }}</span><QButton v-if="settings.status === 'error' && app.bridge === 'Connected'" @click="refresh">{{ t('settings.page.retry') }}</QButton></div>

      <div class="settings-workspace">
        <nav class="settings-section-nav" :aria-label="t('settings.section.navigationLabel')">
          <button v-for="section in settingsSections" :key="section.id" type="button" :aria-current="activeSection === section.id ? 'page' : undefined" @click="activeSection = section.id"><QIcon :name="section.icon" /><span><strong>{{ t(section.titleKey) }}</strong></span></button>
        </nav>
        <main class="settings-section-content">
          <section v-if="activeSection === 'general'" aria-labelledby="settings-general-title">
            <header class="settings-section-heading"><h2 id="settings-general-title">{{ t('settings.section.general') }}</h2></header>
            <article class="settings-card"><h3>{{ t('settings.appearance.title') }}</h3><div class="theme-switch settings-theme"><button v-for="mode in themes" :key="mode" type="button" :class="{ active: theme.mode === mode }" @click="theme.set(mode)">{{ t(themeModeLabelKey(mode)) }}</button></div></article>
            <article class="settings-card appearance-preset-card">
              <div class="settings-card-title"><div><h3>{{ t('settings.appearancePreset.title') }}</h3></div><QButton class="appearance-restore-button" :disabled="appearancePresetId === 'qing-default' || appearanceControlsDisabled" @click="restoreDefaultAppearance">{{ t('settings.appearancePreset.restoreDefault') }}</QButton></div>
              <div class="appearance-preset-grid" role="radiogroup" :aria-label="t('settings.appearancePreset.ariaLabel')" :aria-busy="isSynchronizingAppearance">
                <button v-for="preset in appearancePresetOptions" :key="preset" type="button" role="radio" :aria-checked="appearancePresetId === preset" :disabled="appearanceControlsDisabled" @click="selectAppearancePreset(preset)">
                  <span class="appearance-preset-swatch" :data-preset="preset"><span /><span /></span>
                  <span><strong>{{ t(appearancePresetPresentation(preset).labelKey) }}</strong></span>
                </button>
              </div>
            </article>
            <article class="settings-card font-settings-card">
              <div class="settings-card-title"><div><h3>{{ t('settings.font.title') }}</h3></div><div class="font-card-actions"><QButton :disabled="selectedFont.id === 'Default' || fontControlsDisabled" @click="restoreDefaultFont">{{ t('settings.font.restoreDefault') }}</QButton><QButton :disabled="fontControlsDisabled" @click="importFont"><QIcon name="import" /> {{ t(isSynchronizingFont ? 'settings.font.importing' : 'settings.font.import') }}</QButton><QButton class="font-refresh-button" :loading="settings.isRefreshingFonts" :disabled="fontControlsDisabled" @click="refreshFonts"><QIcon name="refresh" /> {{ t(settings.isRefreshingFonts ? 'settings.font.refreshing' : 'settings.font.refresh') }}</QButton></div></div>
              <div class="font-search-row"><label for="settings-font-search">{{ t('settings.font.searchLabel') }}</label><input id="settings-font-search" v-model="fontSearch" type="search" :placeholder="t('settings.font.searchPlaceholder')" :disabled="fontControlsDisabled" /></div>
              <div class="font-groups" role="radiogroup" :aria-label="t('settings.font.ariaLabel')" :aria-busy="isSynchronizingFont">
                <section class="font-group"><h4>{{ t('settings.font.system') }}</h4><button v-for="font in systemFonts" :key="font.id" type="button" role="radio" :aria-checked="selectedFont.id === font.id" :disabled="fontControlsDisabled" @click="selectFont(font)"><span class="font-option-name">{{ font.displayName }}</span><QBadge tone="info">{{ t('settings.font.systemBadge') }}</QBadge></button><p v-if="systemFonts.length === 0" class="font-empty">{{ t('settings.font.none') }}</p></section>
                <section class="font-group"><h4>{{ t('settings.font.imported') }}</h4><button v-for="font in importedFonts" :key="font.id" type="button" role="radio" :aria-checked="selectedFont.id === font.id" :disabled="fontControlsDisabled" @click="selectFont(font)"><span class="font-option-name">{{ font.displayName }}</span><QBadge tone="success">{{ t('settings.font.importedBadge') }}</QBadge></button><p v-if="importedFonts.length === 0" class="font-empty">{{ t('settings.font.none') }}</p></section>
              </div>
              <p v-if="selectedFont.id === 'Default'" class="font-current-label">{{ t('settings.font.default') }}</p>
              <p v-if="settings.fontError" class="settings-inline-error">{{ t('settings.font.updateFailed') }}</p>
              <p v-if="settings.fontRefreshError" class="settings-inline-error">{{ t('settings.font.refreshFailed') }}</p>
            </article>
            <template v-if="settings.snapshot">
              <article class="settings-card language-settings-card">
                <div class="settings-card-title"><div><h3>{{ t('settings.language.title') }}</h3></div><QBadge tone="info">{{ t('settings.language.hostSetting') }}</QBadge></div>
                <div class="close-behavior-group language-choice-group" role="radiogroup" :aria-label="t('settings.language.ariaLabel')" :aria-busy="isSynchronizingLanguage"><button v-for="option in settings.snapshot.language.options" :key="option.code" type="button" role="radio" :aria-checked="settings.snapshot.language.code === option.code" :disabled="languageControlsDisabled" @click="selectLanguage(option.code)"><span class="close-radio" /><span><strong>{{ option.code === 'system' ? t('settings.appearance.system') : option.nativeName }}</strong><small>{{ languageAuxiliaryName(option.displayName, option.nativeName) }}</small></span></button></div>
                <p v-if="isSynchronizingLanguage" class="settings-saving">{{ t('settings.language.saving') }}</p><p v-else-if="settings.languageError" class="settings-inline-error">{{ t('settings.error.languageUnchanged') }}</p>
                <dl class="settings-values language-values"><div><dt>{{ t('settings.language.configured') }}</dt><dd>{{ configuredLanguageName }}</dd></div><div><dt>{{ t('settings.language.effective') }}</dt><dd>{{ effectiveLanguageName }}</dd></div></dl>
              </article>
              <article class="settings-card"><h3>{{ t('settings.navigation.title') }}</h3><div class="settings-switch-row"><div><strong>{{ t('settings.navigation.showLogs') }}</strong></div><button class="q-switch" type="button" role="switch" :aria-checked="settings.snapshot.showLogsInSidebar" :aria-busy="settings.isUpdatingLogsVisibility" :disabled="hostMutationDisabled || settings.isUpdatingLogsVisibility" @click="toggleLogs"><span /><em>{{ t(settings.snapshot.showLogsInSidebar ? 'settings.navigation.on' : 'settings.navigation.off') }}</em></button></div><p v-if="settings.logsVisibilityError" class="settings-inline-error">{{ t('settings.navigation.unchanged') }}</p></article>
            </template>
            <div v-else class="settings-host-placeholder"><template v-if="settings.status === 'loading'"><QSkeleton v-for="item in 2" :key="item" /></template><template v-else-if="settings.status === 'error'"><QIcon name="statusDanger" :size="26" /><h3>{{ t('settings.error.hostUnavailable') }}</h3><p>{{ t('settings.error.hostUnreadable') }}</p><QButton v-if="app.bridge === 'Connected'" @click="refresh">{{ t('settings.page.retry') }}</QButton></template><template v-else><QIcon name="statusInfo" :size="26" /><h3>{{ t('settings.error.waiting') }}</h3><p>{{ t('settings.error.generalWaiting') }}</p></template></div>
          </section>

          <section v-else-if="activeSection === 'window'" aria-labelledby="settings-window-title">
            <header class="settings-section-heading"><h2 id="settings-window-title">{{ t('settings.section.window') }}</h2></header>
            <article v-if="settings.snapshot" class="settings-card window-size-card">
              <h3>{{ t('settings.window.startupSize') }}</h3>
              <div class="window-size-fields">
                <label><span>{{ t('settings.window.width') }}</span><input v-model="windowWidth" type="number" min="760" max="7680" step="1" inputmode="numeric" :disabled="hostMutationDisabled || isSavingWindow" /></label>
                <label><span>{{ t('settings.window.height') }}</span><input v-model="windowHeight" type="number" min="520" max="4320" step="1" inputmode="numeric" :disabled="hostMutationDisabled || isSavingWindow" /></label>
              </div>
              <label class="window-fullscreen-choice"><input type="checkbox" :checked="settings.snapshot.startupFullscreen ?? false" :disabled="hostMutationDisabled || isSavingWindow" @change="toggleStartupFullscreen" />{{ t('settings.window.startupFullscreen') }}</label>
              <div class="window-size-actions"><QButton :disabled="hostMutationDisabled || isSavingWindow" @click="useCurrentWindowSize">{{ t('settings.window.useCurrentSize') }}</QButton><QButton :disabled="hostMutationDisabled || isSavingWindow || !windowSizeValid" @click="saveWindowSize(false)">{{ t('settings.window.saveSize') }}</QButton><QButton :disabled="hostMutationDisabled || isSavingWindow || !windowSizeValid" @click="saveWindowSize(true)">{{ t('settings.window.applySize') }}</QButton></div>
            </article>
            <article v-if="settings.snapshot && TauriTransport.isAvailable()" class="settings-card info-popup-settings">
              <h3>{{ t('settings.window.infoPopup') }}</h3>
              <div class="info-popup-corners" role="radiogroup" :aria-label="t('settings.window.infoPopupCorner')">
                <button v-for="corner in infoPopupCorners" :key="corner" type="button" role="radio"
                  :aria-checked="(settings.snapshot.infoPopupCorner ?? 'rightTop') === corner"
                  :disabled="hostMutationDisabled || isSavingInfoPopup"
                  @click="updateInfoPopup({ infoPopupCorner: corner })">{{ t(`settings.window.corner.${corner}`) }}</button>
              </div>
              <label class="window-fullscreen-choice"><input type="checkbox"
                :checked="settings.snapshot.infoPopupAnimation ?? true"
                :disabled="hostMutationDisabled || isSavingInfoPopup"
                @change="updateInfoPopup({ infoPopupAnimation: ($event.target as HTMLInputElement).checked })" />{{ t('settings.window.infoPopupAnimation') }}</label>
              <div class="info-popup-duration"><label><span>{{ t('settings.window.infoPopupDuration') }}</span><input v-model="infoPopupDuration" type="number" min="100" max="2000" step="10"
                :disabled="hostMutationDisabled || isSavingInfoPopup || settings.snapshot.infoPopupAnimation === false" /></label>
                <label><span>{{ t('settings.window.infoPopupDismissSeconds') }}</span><input v-model="infoPopupDismissSeconds" type="number" min="3" max="60" step="1"
                  :disabled="hostMutationDisabled || isSavingInfoPopup" /></label>
                <QButton :disabled="hostMutationDisabled || isSavingInfoPopup || !infoPopupDismissValid || settings.snapshot.infoPopupAnimation !== false && !infoPopupDurationValid" @click="saveInfoPopupTimings">{{ t('settings.window.infoPopupSave') }}</QButton></div>
            </article>
            <article v-if="settings.snapshot" class="settings-card"><h3>{{ t('settings.window.closeBehavior') }}</h3><div class="close-behavior-group" role="radiogroup" :aria-label="t('settings.window.ariaLabel')" :aria-busy="settings.isUpdatingCloseBehavior"><button v-for="value in closeBehaviors" :key="value" type="button" role="radio" :aria-checked="settings.snapshot.mainWindowCloseBehavior === value" :disabled="hostMutationDisabled || settings.isUpdatingCloseBehavior" @click="selectCloseBehavior(value)"><span class="close-radio" /><span><strong>{{ t(closeBehaviorPresentation(value).labelKey) }}</strong></span></button></div><p v-if="settings.isUpdatingCloseBehavior" class="settings-saving">{{ t('settings.language.saving') }}</p><p v-else-if="settings.closeBehaviorError" class="settings-inline-error">{{ t('settings.window.unchanged') }}</p><p v-else-if="settings.snapshot.closeBehaviorMessage" class="settings-host-message">{{ settings.snapshot.closeBehaviorMessage }}</p></article>
            <div v-else class="settings-host-placeholder"><template v-if="settings.status === 'loading'"><QSkeleton v-for="item in 3" :key="item" /></template><template v-else-if="settings.status === 'error'"><QIcon name="statusDanger" :size="26" /><h3>{{ t('settings.window.unavailable') }}</h3><p>{{ t('settings.error.hostUnreadable') }}</p><QButton v-if="app.bridge === 'Connected'" @click="refresh">{{ t('settings.page.retry') }}</QButton></template><template v-else><QIcon name="statusInfo" :size="26" /><h3>{{ t('settings.error.waiting') }}</h3><p>{{ t('settings.window.waiting') }}</p></template></div>
          </section>

          <section v-else-if="activeSection === 'startup'" aria-labelledby="settings-startup-title">
            <header class="settings-section-heading"><h2 id="settings-startup-title">{{ t('settings.section.startup') }}</h2></header>
            <template v-if="settings.snapshot">
              <article class="settings-card"><div class="settings-card-title"><div><h3>{{ t('settings.startup.presentation') }}</h3></div><QBadge tone="info">{{ t('settings.startup.editable') }}</QBadge></div><div class="close-behavior-group presentation-mode-group" role="radiogroup" :aria-label="t('settings.startup.presentationAriaLabel')" :aria-busy="settings.isUpdatingStartupPresentation"><button v-for="value in startupPresentations" :key="value" type="button" role="radio" :aria-checked="settings.snapshot.startupPresentationMode === value" :disabled="hostMutationDisabled || settings.isUpdatingStartupPresentation || settings.startupRepairBusy" @click="selectStartupPresentation(value)"><span class="close-radio" /><span><strong>{{ t(startupPresentation(value).labelKey) }}</strong></span></button></div><p v-if="settings.isUpdatingStartupPresentation" class="settings-saving">{{ t('settings.language.saving') }}</p><p v-else-if="settings.startupPresentationError" class="settings-inline-error">{{ t('settings.startup.unchanged') }}</p></article>
              <article class="settings-card windows-startup-card"><div class="settings-card-title"><div><h3>{{ t('settings.startup.windows') }}</h3></div></div><div class="settings-switch-row"><div><strong>{{ t('settings.startup.launchAtLogin') }}</strong></div><button type="button" class="q-switch" role="switch" :aria-checked="settings.snapshot.launchAtLogin" :aria-busy="settings.launchAtLoginBusy" :disabled="hostMutationDisabled || !settings.snapshot.canConfigureLaunchAtLogin || settings.launchAtLoginBusy || settings.startupRepairBusy" @click="toggleLaunchAtLogin"><span /><em>{{ t(settings.snapshot.launchAtLogin ? 'settings.navigation.on' : 'settings.navigation.off') }}</em></button></div><p v-if="!settings.snapshot.canConfigureLaunchAtLogin" class="settings-inline-error">{{ t('settings.startup.unavailableEnvironment') }}</p><p v-else-if="settings.launchAtLoginError" class="settings-inline-error">{{ t('settings.error.launchUnchanged') }}</p></article>
              <article class="settings-card startup-health-card"><div class="settings-card-title"><div><h3>{{ t('settings.startup.health') }}</h3></div><QBadge :tone="startupTone">{{ settings.snapshot.startupStatus }}</QBadge></div><dl class="settings-values startup-health-values"><div><dt>{{ t('settings.startup.launchAtLogin') }}</dt><dd>{{ yesNo(settings.snapshot.launchAtLogin) }}</dd></div><div><dt>{{ t('settings.startup.canConfigure') }}</dt><dd>{{ yesNo(settings.snapshot.canConfigureLaunchAtLogin) }}</dd></div><div><dt>{{ t('settings.startup.backend') }}</dt><dd>{{ settings.snapshot.startupBackend }}</dd></div><div v-if="settings.snapshot.startupMessage"><dt>{{ t('settings.startup.message') }}</dt><dd>{{ settings.snapshot.startupMessage }}</dd></div></dl><p v-if="settings.snapshot.canRepairStartup" class="settings-host-message">{{ t('settings.startup.repairDescription') }}</p><p v-if="settings.startupRepairError" class="settings-inline-error">{{ t('settings.error.repairUnchanged') }}</p><div class="startup-health-actions"><QButton v-if="settings.snapshot.canRepairStartup" :disabled="hostMutationDisabled || settings.status === 'loading' || settings.startupRepairBusy || settings.launchAtLoginBusy" @click="repairStartup"><QIcon name="refresh" /> {{ t(settings.startupRepairBusy ? 'settings.startup.repairing' : 'settings.startup.repair') }}</QButton><QButton :disabled="hostMutationDisabled || settings.status === 'loading' || settings.startupRepairBusy" @click="refresh"><QIcon name="refresh" /> {{ t('settings.startup.refreshStatus') }}</QButton></div></article>
            </template>
            <div v-else class="settings-host-placeholder"><template v-if="settings.status === 'loading'"><QSkeleton v-for="item in 3" :key="item" /></template><template v-else-if="settings.status === 'error'"><QIcon name="statusDanger" :size="26" /><h3>{{ t('settings.startup.unavailable') }}</h3><p>{{ t('settings.error.hostUnreadable') }}</p><QButton v-if="app.bridge === 'Connected'" @click="refresh">{{ t('settings.page.retry') }}</QButton></template><template v-else><QIcon name="statusInfo" :size="26" /><h3>{{ t('settings.error.waiting') }}</h3><p>{{ t('settings.startup.waiting') }}</p></template></div>
          </section>

          <section v-else aria-labelledby="settings-about-title">
            <header class="settings-section-heading"><h2 id="settings-about-title">{{ t('settings.section.about') }}</h2></header>
            <article class="settings-card settings-about" :aria-label="t('settings.about.ariaLabel')"><img :src="brandMark" alt="" aria-hidden="true" /><div><div class="settings-about-title"><strong>QingToolbox</strong><span>{{ t('settings.about.preview') }}</span></div><small>{{ productVersion }}</small></div></article>
            <article class="settings-card"><h3>{{ t('settings.about.workspace') }}</h3><dl class="settings-values"><div><dt>{{ t('settings.about.environment') }}</dt><dd>{{ environment }}</dd></div><div><dt>{{ t('settings.about.bridge') }}</dt><dd><QBadge :tone="app.bridge === 'Connected' ? 'success' : 'warning'">{{ bridgeLabel }}</QBadge></dd></div><div><dt>{{ t('settings.about.author') }}</dt><dd>{{ projectAuthor }}</dd></div><div><dt>{{ t('settings.about.apiVersion') }}</dt><dd>{{ moduleApiVersion }}</dd></div><div><dt>{{ t('settings.about.repository') }}</dt><dd><a class="settings-repository-link" :href="projectRepositoryUrl" target="_blank" rel="noopener noreferrer" @click.prevent="openRepository">{{ projectRepositoryUrl }}</a></dd></div></dl></article>
            <QHostUpdatePanel v-if="app.snapshot?.environmentKind === 'Production'" />
          </section>
        </main>
      </div>
    </div>
  </QPage>
</template>

<style scoped>
.window-size-fields { display: flex; flex-wrap: wrap; gap: 12px; margin-top: 15px; }
.window-size-fields label { display: grid; gap: 6px; min-width: 120px; color: var(--q-text-2); font-size: 12px; }
.window-size-fields input { width: 140px; min-height: 38px; padding: 0 10px; border: 1px solid var(--q-border); border-radius: 9px; background: var(--q-surface-soft); color: var(--q-text); font: inherit; font-size: 14px; }
.window-size-fields input:focus-visible { outline: 2px solid var(--q-brand); outline-offset: 1px; }
.window-fullscreen-choice { display: flex; align-items: center; gap: 8px; margin-top: 17px; color: var(--q-text); font-size: 13px; cursor: pointer; }
.window-fullscreen-choice input { accent-color: var(--q-brand); width: 16px; height: 16px; }
.window-size-actions { display: flex; flex-wrap: wrap; gap: 8px; margin-top: 16px; }
.info-popup-corners { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 8px; margin-top: 14px; }
.info-popup-corners button { min-height: 36px; border: 1px solid var(--q-border); border-radius: 9px; background: var(--q-surface-soft); color: var(--q-text-2); cursor: pointer; }
.info-popup-corners button[aria-checked="true"] { border-color: var(--q-brand); background: var(--q-brand-soft); color: var(--q-brand); }
.info-popup-corners button:disabled { opacity: .55; cursor: default; }
.info-popup-duration { display: flex; flex-wrap: wrap; align-items: end; gap: 10px; margin-top: 14px; }
.info-popup-duration label { display: grid; gap: 6px; color: var(--q-text-2); font-size: 12px; }
.info-popup-duration input { width: 135px; min-height: 36px; padding: 0 10px; border: 1px solid var(--q-border); border-radius: 9px; background: var(--q-surface-soft); color: var(--q-text); }
.info-popup-duration input:disabled { opacity: .48; }
.font-settings-card {
  font-family: inherit;
}

.settings-workspace {
  grid-template-columns: 184px minmax(0, 1fr);
}

.settings-section-nav {
  gap: 4px;
  padding: 6px;
}

.settings-section-nav button {
  grid-template-columns: 20px minmax(0, 1fr);
  align-items: center;
  gap: 10px;
  min-height: 44px;
  padding: 10px;
  line-height: 22px;
}

.settings-section-nav strong {
  line-height: inherit;
}

@media (max-width: 980px) {
  .settings-workspace {
    grid-template-columns: 1fr;
  }
}
</style>
