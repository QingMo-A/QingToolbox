<script setup lang="ts">
import { computed, inject, ref, watch } from 'vue'
import type { SettingsClient } from '../bridge/clients/SettingsClient'
import type { ModuleClient } from '../bridge/clients/ModuleClient'
import type { LanguageCode, MainWindowCloseBehavior, StartupPresentationMode } from '../contracts/settings'
import { useAppStore } from '../app/store'
import { useSettingsStore } from '../app/settingsStore'
import { useModuleStore } from '../app/moduleStore'
import { useThemeStore, type ThemeMode } from '../app/themeStore'
import { useToastStore } from '../app/toastStore'
import QPage from '../design-system/components/QPage.vue'
import QButton from '../design-system/components/QButton.vue'
import QIcon from '../design-system/components/QIcon.vue'
import QBadge from '../design-system/components/QBadge.vue'
import QSkeleton from '../design-system/components/QSkeleton.vue'
import brandMark from '../assets/QingToolbox.Mark.svg'
import { useLocalization } from '../localization/localization'
import { bridgeStateKey } from '../presentation/workspacePresentation'
import {
  closeBehaviorPresentation,
  settingsSections,
  startupPresentation,
  themeModeLabelKey,
  type SettingsSection,
} from '../presentation/settingsPresentation'

const client = inject<SettingsClient>('settingsClient')!
const moduleClient = inject<ModuleClient>('moduleClient')!
const app = useAppStore()
const settings = useSettingsStore()
const modules = useModuleStore()
const theme = useThemeStore()
const toast = useToastStore()
const { currentLocale, t } = useLocalization()
const activeSection = ref<SettingsSection>('general')
const isSynchronizingLanguage = ref(false)
const themes: ThemeMode[] = ['system', 'light', 'dark']
const closeBehaviors: MainWindowCloseBehavior[] = ['Ask', 'MinimizeToNotificationArea', 'ExitApplication']
const startupPresentations: StartupPresentationMode[] = ['MainWindow', 'Minimized', 'FloatingBadge']

const hasSnapshot = computed(() => settings.snapshot !== null)
const refreshed = computed(() => settings.generatedAt ? new Date(settings.generatedAt).toLocaleTimeString(currentLocale.value) : null)
const productVersion = computed(() => app.snapshot?.hostVersion || '0.2.0-alpha')
const environment = computed(() => app.snapshot?.environmentDisplayName || app.mode || 'Development')
const hostControlsDisabled = computed(() => app.bridge !== 'Connected')
const languageControlsDisabled = computed(() => hostControlsDisabled.value || settings.status === 'loading' || settings.isHostMutationBusy || isSynchronizingLanguage.value)
const hostMutationDisabled = computed(() => hostControlsDisabled.value || settings.isUpdatingLanguage || isSynchronizingLanguage.value)
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

watch(() => app.bridge, bridge => {
  if (bridge === 'Connected' && settings.status === 'idle') void refresh()
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
        <div><h1>{{ t('settings.page.title') }}</h1><p>{{ t('settings.page.description') }}</p></div>
        <QButton :disabled="app.bridge !== 'Connected' || settings.status === 'loading' || isSynchronizingLanguage" @click="refresh"><QIcon name="refresh" /> {{ t(settings.status === 'loading' ? 'settings.page.refreshing' : 'settings.page.refresh') }}</QButton>
      </header>
      <div class="settings-readonly"><QIcon name="statusInfo" /> {{ t('settings.page.readonly') }}</div>
      <p v-if="refreshed" class="settings-refreshed">{{ t('settings.page.lastRefreshed', { time: refreshed }) }}</p>
      <div v-if="snapshotNotice" class="settings-snapshot-notice" role="status"><QIcon :name="settings.status === 'error' ? 'statusWarning' : 'statusInfo'" /><span>{{ snapshotNotice }}</span><QButton v-if="settings.status === 'error' && app.bridge === 'Connected'" @click="refresh">{{ t('settings.page.retry') }}</QButton></div>

      <div class="settings-workspace">
        <nav class="settings-section-nav" :aria-label="t('settings.section.navigationLabel')">
          <button v-for="section in settingsSections" :key="section.id" type="button" :aria-current="activeSection === section.id ? 'page' : undefined" @click="activeSection = section.id"><QIcon :name="section.icon" /><span><strong>{{ t(section.titleKey) }}</strong><small>{{ t(section.descriptionKey) }}</small></span></button>
        </nav>
        <main class="settings-section-content">
          <section v-if="activeSection === 'general'" aria-labelledby="settings-general-title">
            <header class="settings-section-heading"><h2 id="settings-general-title">{{ t('settings.section.general') }}</h2><p>{{ t('settings.general.description') }}</p></header>
            <article class="settings-card"><h3>{{ t('settings.appearance.title') }}</h3><p>{{ t('settings.appearance.description') }}</p><div class="theme-switch settings-theme"><button v-for="mode in themes" :key="mode" type="button" :class="{ active: theme.mode === mode }" @click="theme.set(mode)">{{ t(themeModeLabelKey(mode)) }}</button></div></article>
            <template v-if="settings.snapshot">
              <article class="settings-card language-settings-card">
                <div class="settings-card-title"><div><h3>{{ t('settings.language.title') }}</h3><p>{{ t('settings.language.description') }}</p></div><QBadge tone="info">{{ t('settings.language.hostSetting') }}</QBadge></div>
                <div class="close-behavior-group language-choice-group" role="radiogroup" :aria-label="t('settings.language.ariaLabel')" :aria-busy="isSynchronizingLanguage"><button v-for="option in settings.snapshot.language.options" :key="option.code" type="button" role="radio" :aria-checked="settings.snapshot.language.code === option.code" :disabled="languageControlsDisabled" @click="selectLanguage(option.code)"><span class="close-radio" /><span><strong>{{ option.code === 'system' ? t('settings.appearance.system') : option.nativeName }}</strong><small>{{ languageAuxiliaryName(option.displayName, option.nativeName) }}</small></span></button></div>
                <p v-if="isSynchronizingLanguage" class="settings-saving">{{ t('settings.language.saving') }}</p><p v-else-if="settings.languageError" class="settings-inline-error">{{ t('settings.error.languageUnchanged') }}</p>
                <dl class="settings-values language-values"><div><dt>{{ t('settings.language.configured') }}</dt><dd>{{ configuredLanguageName }}</dd></div><div><dt>{{ t('settings.language.effective') }}</dt><dd>{{ effectiveLanguageName }}</dd></div></dl>
              </article>
              <article class="settings-card"><h3>{{ t('settings.navigation.title') }}</h3><p>{{ t('settings.navigation.description') }}</p><div class="settings-switch-row"><div><strong>{{ t('settings.navigation.showLogs') }}</strong><small>{{ t('settings.navigation.hostSaved') }}</small></div><button class="q-switch" type="button" role="switch" :aria-checked="settings.snapshot.showLogsInSidebar" :disabled="hostMutationDisabled || settings.isUpdatingLogsVisibility" @click="toggleLogs"><span /><em>{{ settings.isUpdatingLogsVisibility ? t('settings.language.saving') : t(settings.snapshot.showLogsInSidebar ? 'settings.navigation.on' : 'settings.navigation.off') }}</em></button></div><p v-if="settings.logsVisibilityError" class="settings-inline-error">{{ t('settings.navigation.unchanged') }}</p></article>
            </template>
            <div v-else class="settings-host-placeholder"><template v-if="settings.status === 'loading'"><QSkeleton v-for="item in 2" :key="item" /></template><template v-else-if="settings.status === 'error'"><QIcon name="statusDanger" :size="26" /><h3>{{ t('settings.error.hostUnavailable') }}</h3><p>{{ t('settings.error.hostUnreadable') }}</p><QButton v-if="app.bridge === 'Connected'" @click="refresh">{{ t('settings.page.retry') }}</QButton></template><template v-else><QIcon name="statusInfo" :size="26" /><h3>{{ t('settings.error.waiting') }}</h3><p>{{ t('settings.error.generalWaiting') }}</p></template></div>
          </section>

          <section v-else-if="activeSection === 'window'" aria-labelledby="settings-window-title">
            <header class="settings-section-heading"><h2 id="settings-window-title">{{ t('settings.section.window') }}</h2><p>{{ t('settings.window.description') }}</p></header>
            <article v-if="settings.snapshot" class="settings-card"><h3>{{ t('settings.window.closeBehavior') }}</h3><div class="close-behavior-group" role="radiogroup" :aria-label="t('settings.window.ariaLabel')" :aria-busy="settings.isUpdatingCloseBehavior"><button v-for="value in closeBehaviors" :key="value" type="button" role="radio" :aria-checked="settings.snapshot.mainWindowCloseBehavior === value" :disabled="hostMutationDisabled || settings.isUpdatingCloseBehavior" @click="selectCloseBehavior(value)"><span class="close-radio" /><span><strong>{{ t(closeBehaviorPresentation(value).labelKey) }}</strong><small>{{ t(closeBehaviorPresentation(value).descriptionKey) }}</small></span></button></div><p v-if="settings.isUpdatingCloseBehavior" class="settings-saving">{{ t('settings.language.saving') }}</p><p v-else-if="settings.closeBehaviorError" class="settings-inline-error">{{ t('settings.window.unchanged') }}</p><p v-else-if="settings.snapshot.closeBehaviorMessage" class="settings-host-message">{{ settings.snapshot.closeBehaviorMessage }}</p></article>
            <div v-else class="settings-host-placeholder"><template v-if="settings.status === 'loading'"><QSkeleton v-for="item in 3" :key="item" /></template><template v-else-if="settings.status === 'error'"><QIcon name="statusDanger" :size="26" /><h3>{{ t('settings.window.unavailable') }}</h3><p>{{ t('settings.error.hostUnreadable') }}</p><QButton v-if="app.bridge === 'Connected'" @click="refresh">{{ t('settings.page.retry') }}</QButton></template><template v-else><QIcon name="statusInfo" :size="26" /><h3>{{ t('settings.error.waiting') }}</h3><p>{{ t('settings.window.waiting') }}</p></template></div>
          </section>

          <section v-else-if="activeSection === 'startup'" aria-labelledby="settings-startup-title">
            <header class="settings-section-heading"><h2 id="settings-startup-title">{{ t('settings.section.startup') }}</h2><p>{{ t('settings.startup.description') }}</p></header>
            <template v-if="settings.snapshot">
              <article class="settings-card"><div class="settings-card-title"><div><h3>{{ t('settings.startup.presentation') }}</h3><p>{{ t('settings.startup.presentationDescription') }}</p></div><QBadge tone="info">{{ t('settings.startup.editable') }}</QBadge></div><div class="close-behavior-group presentation-mode-group" role="radiogroup" :aria-label="t('settings.startup.presentationAriaLabel')" :aria-busy="settings.isUpdatingStartupPresentation"><button v-for="value in startupPresentations" :key="value" type="button" role="radio" :aria-checked="settings.snapshot.startupPresentationMode === value" :disabled="hostMutationDisabled || settings.isUpdatingStartupPresentation || settings.startupRepairBusy" @click="selectStartupPresentation(value)"><span class="close-radio" /><span><strong>{{ t(startupPresentation(value).labelKey) }}</strong><small>{{ t(startupPresentation(value).descriptionKey) }}</small></span></button></div><p v-if="settings.isUpdatingStartupPresentation" class="settings-saving">{{ t('settings.language.saving') }}</p><p v-else-if="settings.startupPresentationError" class="settings-inline-error">{{ t('settings.startup.unchanged') }}</p></article>
              <article class="settings-card windows-startup-card"><div class="settings-card-title"><div><h3>{{ t('settings.startup.windows') }}</h3><p>{{ t('settings.startup.windowsDescription') }}</p></div></div><div class="settings-switch-row"><div><strong>{{ t('settings.startup.launchAtLogin') }}</strong><small>{{ t('settings.startup.launchDescription') }}</small></div><button type="button" class="q-switch" role="switch" :aria-checked="settings.snapshot.launchAtLogin" :disabled="hostMutationDisabled || !settings.snapshot.canConfigureLaunchAtLogin || settings.launchAtLoginBusy || settings.startupRepairBusy" @click="toggleLaunchAtLogin"><span /><em>{{ settings.launchAtLoginBusy ? t(settings.snapshot.launchAtLogin ? 'settings.startup.disabling' : 'settings.startup.enabling') : t(settings.snapshot.launchAtLogin ? 'settings.navigation.on' : 'settings.navigation.off') }}</em></button></div><p v-if="!settings.snapshot.canConfigureLaunchAtLogin" class="settings-inline-error">{{ t('settings.startup.unavailableEnvironment') }}</p><p v-else-if="settings.launchAtLoginError" class="settings-inline-error">{{ t('settings.error.launchUnchanged') }}</p></article>
              <article class="settings-card startup-health-card"><div class="settings-card-title"><div><h3>{{ t('settings.startup.health') }}</h3><p>{{ t('settings.startup.healthDescription') }}</p></div><QBadge :tone="startupTone">{{ settings.snapshot.startupStatus }}</QBadge></div><dl class="settings-values startup-health-values"><div><dt>{{ t('settings.startup.launchAtLogin') }}</dt><dd>{{ yesNo(settings.snapshot.launchAtLogin) }}</dd></div><div><dt>{{ t('settings.startup.canConfigure') }}</dt><dd>{{ yesNo(settings.snapshot.canConfigureLaunchAtLogin) }}</dd></div><div><dt>{{ t('settings.startup.backend') }}</dt><dd>{{ settings.snapshot.startupBackend }}</dd></div><div v-if="settings.snapshot.startupMessage"><dt>{{ t('settings.startup.message') }}</dt><dd>{{ settings.snapshot.startupMessage }}</dd></div></dl><p v-if="settings.snapshot.canRepairStartup" class="settings-host-message">{{ t('settings.startup.repairDescription') }}</p><p v-if="settings.startupRepairError" class="settings-inline-error">{{ t('settings.error.repairUnchanged') }}</p><div class="startup-health-actions"><QButton v-if="settings.snapshot.canRepairStartup" :disabled="hostMutationDisabled || settings.status === 'loading' || settings.startupRepairBusy || settings.launchAtLoginBusy" @click="repairStartup"><QIcon name="refresh" /> {{ t(settings.startupRepairBusy ? 'settings.startup.repairing' : 'settings.startup.repair') }}</QButton><QButton :disabled="hostMutationDisabled || settings.status === 'loading' || settings.startupRepairBusy" @click="refresh"><QIcon name="refresh" /> {{ t('settings.startup.refreshStatus') }}</QButton></div></article>
            </template>
            <div v-else class="settings-host-placeholder"><template v-if="settings.status === 'loading'"><QSkeleton v-for="item in 3" :key="item" /></template><template v-else-if="settings.status === 'error'"><QIcon name="statusDanger" :size="26" /><h3>{{ t('settings.startup.unavailable') }}</h3><p>{{ t('settings.error.hostUnreadable') }}</p><QButton v-if="app.bridge === 'Connected'" @click="refresh">{{ t('settings.page.retry') }}</QButton></template><template v-else><QIcon name="statusInfo" :size="26" /><h3>{{ t('settings.error.waiting') }}</h3><p>{{ t('settings.startup.waiting') }}</p></template></div>
          </section>

          <section v-else aria-labelledby="settings-about-title">
            <header class="settings-section-heading"><h2 id="settings-about-title">{{ t('settings.section.about') }}</h2><p>{{ t('settings.about.description') }}</p></header>
            <article class="settings-card settings-about" :aria-label="t('settings.about.ariaLabel')"><img :src="brandMark" alt="" aria-hidden="true" /><div><div class="settings-about-title"><strong>QingToolbox</strong><span>{{ t('settings.about.preview') }}</span></div><small>{{ productVersion }}</small><p>{{ t('settings.about.productDescription') }}</p></div></article>
            <article class="settings-card"><h3>{{ t('settings.about.workspace') }}</h3><dl class="settings-values"><div><dt>{{ t('settings.about.environment') }}</dt><dd>{{ environment }}</dd></div><div><dt>{{ t('settings.about.bridge') }}</dt><dd><QBadge :tone="app.bridge === 'Connected' ? 'success' : 'warning'">{{ bridgeLabel }}</QBadge></dd></div></dl></article>
          </section>
        </main>
      </div>
    </div>
  </QPage>
</template>
