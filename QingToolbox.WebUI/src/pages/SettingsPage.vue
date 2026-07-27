<script setup lang="ts">
import { computed, inject, ref, watch } from 'vue'
import type { SettingsClient } from '../bridge/clients/SettingsClient'
import type { MainWindowCloseBehavior, StartupPresentationMode } from '../contracts/settings'
import { useAppStore } from '../app/store'
import { useSettingsStore } from '../app/settingsStore'
import { useThemeStore, type ThemeMode } from '../app/themeStore'
import { useToastStore } from '../app/toastStore'
import QPage from '../design-system/components/QPage.vue'
import QButton from '../design-system/components/QButton.vue'
import QIcon from '../design-system/components/QIcon.vue'
import QBadge from '../design-system/components/QBadge.vue'
import QSkeleton from '../design-system/components/QSkeleton.vue'
import brandMark from '../assets/QingToolbox.Mark.svg'

type SettingsSection = 'general' | 'window' | 'startup' | 'about'

const client = inject<SettingsClient>('settingsClient')!
const app = useAppStore()
const settings = useSettingsStore()
const theme = useThemeStore()
const toast = useToastStore()
const activeSection = ref<SettingsSection>('general')
const sections: { id: SettingsSection; title: string; description: string; icon: 'settings'|'close'|'running'|'statusInfo' }[] = [
  { id: 'general', title: 'General', description: 'Appearance, language and navigation', icon: 'settings' },
  { id: 'window', title: 'Window', description: 'Main window close behavior', icon: 'close' },
  { id: 'startup', title: 'Startup', description: 'Startup presentation and health', icon: 'running' },
  { id: 'about', title: 'About', description: 'Version and workspace information', icon: 'statusInfo' }
]
const themes: { mode: ThemeMode; label: string }[] = [{ mode: 'system', label: 'Follow system' }, { mode: 'light', label: 'Light' }, { mode: 'dark', label: 'Dark' }]
const closeBehaviors: { value: MainWindowCloseBehavior; label: string; description: string }[] = [
  { value: 'Ask', label: 'Ask every time', description: 'Ask what to do whenever the main window is closed.' },
  { value: 'MinimizeToNotificationArea', label: 'Minimize to notification area', description: 'Hide the main window while QingToolbox continues running.' },
  { value: 'ExitApplication', label: 'Exit application', description: 'Exit QingToolbox when the main window is closed.' }
]
const startupPresentations: { value: StartupPresentationMode; label: string; description: string }[] = [
  { value: 'MainWindow', label: 'Show the main window', description: 'Open QingToolbox with the main window visible.' },
  { value: 'Minimized', label: 'Start minimized', description: 'Start QingToolbox minimized and ready in the background.' },
  { value: 'FloatingBadge', label: 'Show the floating badge', description: 'Start with the compact floating badge instead of the main window.' }
]

const hasSnapshot = computed(() => settings.snapshot !== null)
const refreshed = computed(() => settings.generatedAt ? new Date(settings.generatedAt).toLocaleTimeString() : null)
const productVersion = computed(() => app.snapshot?.hostVersion || '0.2.0-alpha')
const environment = computed(() => app.snapshot?.environmentDisplayName || app.mode || 'Development')
const hostControlsDisabled = computed(() => app.bridge !== 'Connected')
const snapshotNotice = computed(() => {
  if (!hasSnapshot.value) return ''
  if (settings.status === 'error') return 'Settings refresh failed. Showing the last available host configuration.'
  if (app.bridge !== 'Connected') return 'The host is disconnected. Showing the last available host configuration.'
  if (settings.status === 'loading') return 'Refreshing host configuration…'
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

const yesNo = (value: boolean) => value ? 'Yes' : 'No'
async function toggleLogs() {
  if (!settings.snapshot || hostControlsDisabled.value || settings.isUpdatingLogsVisibility) return
  const result = await settings.updateLogsVisibility(client, !settings.snapshot.showLogsInSidebar)
  if (result === 'success') toast.show('Logs navigation preference saved.', 'success')
  else if (result === 'failure') toast.show('Logs navigation preference could not be saved.', 'error')
}
async function selectCloseBehavior(value: MainWindowCloseBehavior) {
  if (!settings.snapshot || hostControlsDisabled.value || settings.isUpdatingCloseBehavior || settings.snapshot.mainWindowCloseBehavior === value) return
  const result = await settings.updateCloseBehavior(client, value)
  if (result === 'success') toast.show('Main window close behavior saved.', 'success')
  else if (result === 'failure') toast.show('Main window close behavior could not be saved.', 'error')
}
async function selectStartupPresentation(value: StartupPresentationMode) {
  if (!settings.snapshot || hostControlsDisabled.value || settings.isUpdatingStartupPresentation || settings.snapshot.startupPresentationMode === value) return
  const result = await settings.updateStartupPresentation(client, value)
  if (result === 'success') toast.show('Startup presentation preference saved.', 'success')
  else if (result === 'failure') toast.show('Startup presentation preference could not be saved.', 'error')
}
async function toggleLaunchAtLogin() {
  if (!settings.snapshot || hostControlsDisabled.value || !settings.snapshot.canConfigureLaunchAtLogin || settings.launchAtLoginBusy || settings.startupRepairBusy) return
  const enabled = !settings.snapshot.launchAtLogin
  const result = await settings.updateLaunchAtLogin(client, enabled)
  if (result === 'success') toast.show(enabled ? 'QingToolbox will start when you sign in to Windows.' : 'QingToolbox will no longer start when you sign in to Windows.', 'success')
  else if (result === 'failure') toast.show('The Windows startup setting could not be updated.', 'error')
}
async function repairStartup() {
  if (!settings.snapshot?.canRepairStartup || hostControlsDisabled.value || settings.startupRepairBusy || settings.launchAtLoginBusy || settings.status === 'loading') return
  const result = await settings.repairStartupRegistration(client)
  if (result === 'success') toast.show('Windows startup registration repaired.', 'success')
  else if (result === 'failure') toast.show('The Windows startup registration could not be repaired.', 'error')
}
</script>

<template>
  <QPage class="settings-page">
    <div class="settings-workspace-shell">
      <header class="settings-header">
        <div><h1>Settings</h1><p>Manage the Development workspace and review host configuration.</p></div>
        <QButton :disabled="app.bridge !== 'Connected' || settings.status === 'loading'" @click="refresh">
          <QIcon name="refresh" /> {{ settings.status === 'loading' ? 'Refreshing…' : 'Refresh' }}
        </QButton>
      </header>
      <div class="settings-readonly"><QIcon name="statusInfo" /> Some settings are saved through the QingToolbox host. Appearance changes apply only to the Development Web workspace.</div>
      <p v-if="refreshed" class="settings-refreshed">Last refreshed {{ refreshed }}</p>
      <div v-if="snapshotNotice" class="settings-snapshot-notice" role="status">
        <QIcon :name="settings.status === 'error' ? 'statusWarning' : 'statusInfo'" />
        <span>{{ snapshotNotice }}</span>
        <QButton v-if="settings.status === 'error' && app.bridge === 'Connected'" @click="refresh">Retry</QButton>
      </div>

      <div class="settings-workspace">
        <nav class="settings-section-nav" aria-label="Settings sections">
          <button v-for="section in sections" :key="section.id" type="button" :aria-current="activeSection === section.id ? 'page' : undefined" @click="activeSection = section.id">
            <QIcon :name="section.icon" /><span><strong>{{ section.title }}</strong><small>{{ section.description }}</small></span>
          </button>
        </nav>

        <main class="settings-section-content">
          <section v-if="activeSection === 'general'" aria-labelledby="settings-general-title">
            <header class="settings-section-heading"><h2 id="settings-general-title">General</h2><p>Personalize the workspace and review host language and navigation preferences.</p></header>
            <article class="settings-card"><h3>Appearance</h3><p>Applies only to the Development Web workspace.</p><div class="theme-switch settings-theme"><button v-for="item in themes" :key="item.mode" type="button" :class="{ active: theme.mode === item.mode }" @click="theme.set(item.mode)">{{ item.label }}</button></div></article>
            <template v-if="settings.snapshot">
              <article class="settings-card"><div class="settings-card-title"><div><h3>Language</h3><p>The language currently selected by the host.</p></div><QBadge tone="info">Host setting</QBadge></div><dl class="settings-values"><div><dt>Language</dt><dd>{{ settings.snapshot.language.displayName }}</dd></div><div><dt>Language code</dt><dd>{{ settings.snapshot.language.code }}</dd></div></dl></article>
              <article class="settings-card"><h3>Navigation</h3><p>Choose whether Session Logs appears in workspace navigation.</p><div class="settings-switch-row"><div><strong>Show logs in sidebar</strong><small>Saved through the QingToolbox host.</small></div><button class="q-switch" type="button" role="switch" :aria-checked="settings.snapshot.showLogsInSidebar" :disabled="hostControlsDisabled || settings.isUpdatingLogsVisibility" @click="toggleLogs"><span /><em>{{ settings.isUpdatingLogsVisibility ? 'Saving…' : settings.snapshot.showLogsInSidebar ? 'On' : 'Off' }}</em></button></div><p v-if="settings.logsVisibilityError" class="settings-inline-error">The preference was not changed.</p></article>
            </template>
            <div v-else class="settings-host-placeholder">
              <template v-if="settings.status === 'loading'"><QSkeleton v-for="item in 2" :key="item" /></template>
              <template v-else-if="settings.status === 'error'"><QIcon name="statusDanger" :size="26" /><h3>Host settings are unavailable</h3><p>The current host configuration could not be read.</p><QButton v-if="app.bridge === 'Connected'" @click="refresh">Retry</QButton></template>
              <template v-else><QIcon name="statusInfo" :size="26" /><h3>Waiting for the host</h3><p>Language and navigation settings will appear after the Development bridge is connected.</p></template>
            </div>
          </section>

          <section v-else-if="activeSection === 'window'" aria-labelledby="settings-window-title">
            <header class="settings-section-heading"><h2 id="settings-window-title">Window</h2><p>Choose what QingToolbox does when the main window is closed.</p></header>
            <article v-if="settings.snapshot" class="settings-card"><h3>Main window close behavior</h3><div class="close-behavior-group" role="radiogroup" aria-label="Main window close behavior" :aria-busy="settings.isUpdatingCloseBehavior"><button v-for="option in closeBehaviors" :key="option.value" type="button" role="radio" :aria-checked="settings.snapshot.mainWindowCloseBehavior === option.value" :disabled="hostControlsDisabled || settings.isUpdatingCloseBehavior" @click="selectCloseBehavior(option.value)"><span class="close-radio" /><span><strong>{{ option.label }}</strong><small>{{ option.description }}</small></span></button></div><p v-if="settings.isUpdatingCloseBehavior" class="settings-saving">Saving…</p><p v-else-if="settings.closeBehaviorError" class="settings-inline-error">The close behavior was not changed.</p><p v-else-if="settings.snapshot.closeBehaviorMessage" class="settings-host-message">{{ settings.snapshot.closeBehaviorMessage }}</p></article>
            <div v-else class="settings-host-placeholder"><template v-if="settings.status === 'loading'"><QSkeleton v-for="item in 3" :key="item" /></template><template v-else-if="settings.status === 'error'"><QIcon name="statusDanger" :size="26" /><h3>Window settings are unavailable</h3><p>The current host configuration could not be read.</p><QButton v-if="app.bridge === 'Connected'" @click="refresh">Retry</QButton></template><template v-else><QIcon name="statusInfo" :size="26" /><h3>Waiting for the host</h3><p>Window settings will appear after the Development bridge is connected.</p></template></div>
          </section>

          <section v-else-if="activeSection === 'startup'" aria-labelledby="settings-startup-title">
            <header class="settings-section-heading"><h2 id="settings-startup-title">Startup</h2><p>Choose the next-launch presentation and review the current startup health.</p></header>
            <template v-if="settings.snapshot">
              <article class="settings-card"><div class="settings-card-title"><div><h3>Startup presentation</h3><p>Changes take effect the next time QingToolbox starts.</p></div><QBadge tone="info">Editable</QBadge></div><div class="close-behavior-group presentation-mode-group" role="radiogroup" aria-label="Startup presentation mode" :aria-busy="settings.isUpdatingStartupPresentation"><button v-for="option in startupPresentations" :key="option.value" type="button" role="radio" :aria-checked="settings.snapshot.startupPresentationMode === option.value" :disabled="hostControlsDisabled || settings.isUpdatingStartupPresentation || settings.startupRepairBusy" @click="selectStartupPresentation(option.value)"><span class="close-radio" /><span><strong>{{ option.label }}</strong><small>{{ option.description }}</small></span></button></div><p v-if="settings.isUpdatingStartupPresentation" class="settings-saving">Saving…</p><p v-else-if="settings.startupPresentationError" class="settings-inline-error">The startup presentation preference was not changed.</p></article>
              <article class="settings-card windows-startup-card"><div class="settings-card-title"><div><h3>Windows startup</h3><p>This setting controls whether QingToolbox starts after you sign in to Windows.</p></div></div><div class="settings-switch-row"><div><strong>Launch at login</strong><small>Launch QingToolbox when I sign in to Windows.</small></div><button type="button" class="q-switch" role="switch" :aria-checked="settings.snapshot.launchAtLogin" :disabled="hostControlsDisabled || !settings.snapshot.canConfigureLaunchAtLogin || settings.launchAtLoginBusy || settings.startupRepairBusy" @click="toggleLaunchAtLogin"><span /><em>{{ settings.launchAtLoginBusy ? (settings.snapshot.launchAtLogin ? 'Disabling…' : 'Enabling…') : (settings.snapshot.launchAtLogin ? 'On' : 'Off') }}</em></button></div><p v-if="!settings.snapshot.canConfigureLaunchAtLogin" class="settings-inline-error">Windows startup registration is not available in this environment.</p><p v-else-if="settings.launchAtLoginError" class="settings-inline-error">{{ settings.launchAtLoginError }}</p></article>
              <article class="settings-card startup-health-card"><div class="settings-card-title"><div><h3>Startup health</h3><p>Read-only host registration status.</p></div><QBadge :tone="startupTone">{{ settings.snapshot.startupStatus }}</QBadge></div><dl class="settings-values startup-health-values"><div><dt>Launch at login</dt><dd>{{ yesNo(settings.snapshot.launchAtLogin) }}</dd></div><div><dt>Can configure startup</dt><dd>{{ yesNo(settings.snapshot.canConfigureLaunchAtLogin) }}</dd></div><div><dt>Startup backend</dt><dd>{{ settings.snapshot.startupBackend }}</dd></div><div v-if="settings.snapshot.startupMessage"><dt>Message</dt><dd>{{ settings.snapshot.startupMessage }}</dd></div></dl><p v-if="settings.snapshot.canRepairStartup" class="settings-host-message">Recreate or clean up the QingToolbox Windows startup registration using the existing host configuration.</p><p v-if="settings.startupRepairError" class="settings-inline-error">{{ settings.startupRepairError }}</p><div class="startup-health-actions"><QButton v-if="settings.snapshot.canRepairStartup" :disabled="hostControlsDisabled || settings.status === 'loading' || settings.startupRepairBusy || settings.launchAtLoginBusy" @click="repairStartup"><QIcon name="refresh" /> {{ settings.startupRepairBusy ? 'Repairing…' : 'Repair startup registration' }}</QButton><QButton :disabled="hostControlsDisabled || settings.status === 'loading' || settings.startupRepairBusy" @click="refresh"><QIcon name="refresh" /> Refresh status</QButton></div></article>
            </template>
            <div v-else class="settings-host-placeholder"><template v-if="settings.status === 'loading'"><QSkeleton v-for="item in 3" :key="item" /></template><template v-else-if="settings.status === 'error'"><QIcon name="statusDanger" :size="26" /><h3>Startup settings are unavailable</h3><p>The current host configuration could not be read.</p><QButton v-if="app.bridge === 'Connected'" @click="refresh">Retry</QButton></template><template v-else><QIcon name="statusInfo" :size="26" /><h3>Waiting for the host</h3><p>Startup settings will appear after the Development bridge is connected.</p></template></div>
          </section>

          <section v-else aria-labelledby="settings-about-title">
            <header class="settings-section-heading"><h2 id="settings-about-title">About</h2><p>Version and Development workspace information.</p></header>
            <article class="settings-card settings-about" aria-label="About QingToolbox"><img :src="brandMark" alt="" aria-hidden="true" /><div><div class="settings-about-title"><strong>QingToolbox</strong><span>Preview</span></div><small>{{ productVersion }}</small><p>Modular Windows toolbox.</p></div></article>
            <article class="settings-card"><h3>Workspace</h3><dl class="settings-values"><div><dt>Environment</dt><dd>{{ environment }}</dd></div><div><dt>Bridge state</dt><dd><QBadge :tone="app.bridge === 'Connected' ? 'success' : 'warning'">{{ app.bridge }}</QBadge></dd></div></dl></article>
          </section>
        </main>
      </div>
    </div>
  </QPage>
</template>
