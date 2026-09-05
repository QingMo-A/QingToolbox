import { createApp } from 'vue'
import { createPinia } from 'pinia'
import App from './app/App.vue'
import FloatingBadge from './floating-badge/FloatingBadge.vue'
import { router } from './app/router'
import { useAppStore } from './app/store'
import { useModuleStore } from './app/moduleStore'
import { notifyHostTheme, useThemeStore } from './app/themeStore'
import { applyAppearancePreset, readAppearancePreset } from './design-system/tokens/appearancePresets'
import { WebViewTransport } from './bridge/transport/WebViewTransport'
import { TauriTransport } from './bridge/transport/TauriTransport'
import { MockTransport } from './bridge/transport/MockTransport'
import { UnavailableTransport } from './bridge/transport/UnavailableTransport'
import { RequestClient } from './bridge/protocol/RequestClient'
import { EventDispatcher } from './bridge/protocol/EventDispatcher'
import { AppClient } from './bridge/clients/AppClient'
import { ModuleClient } from './bridge/clients/ModuleClient'
import { LogClient } from './bridge/clients/LogClient'
import { SettingsClient } from './bridge/clients/SettingsClient'
import { HostUpdateClient } from './bridge/clients/HostUpdateClient'
import type { AppSnapshot } from './contracts/app'
import './design-system/tokens/tokens.css'
import './styles/main.css'
import './design-system/tokens/appearancePresets.css'

applyAppearancePreset(readAppearancePreset())

const isFloatingBadge = new URL(window.location.href).searchParams.get('surface') === 'floating-badge'

if (isFloatingBadge) {
  document.documentElement.classList.add('floating-badge-document')
  createApp(FloatingBadge).mount('#app')
} else {
  const explicitMock = import.meta.env.VITE_QING_TRANSPORT === 'mock'
  const transport = explicitMock
    ? new MockTransport()
    : TauriTransport.isAvailable()
      ? new TauriTransport()
      : WebViewTransport.isAvailable()
        ? new WebViewTransport()
        : new UnavailableTransport()
  const dispatcher = new EventDispatcher()
  const requests = new RequestClient(transport)
  const appClient = new AppClient(requests)
  const moduleClient = new ModuleClient(requests)
  const logClient = new LogClient(requests)
  const settingsClient = new SettingsClient(requests)
  const hostUpdateClient = new HostUpdateClient(requests)
  const app = createApp(App)
  const pinia = createPinia()

  app.use(pinia)
  app.use(router)
  app.provide('appClient', appClient)
  app.provide('moduleClient', moduleClient)
  app.provide('logClient', logClient)
  app.provide('settingsClient', settingsClient)
  app.provide('hostUpdateClient', hostUpdateClient)

  const store = useAppStore(pinia)
  const moduleStore = useModuleStore(pinia)
  store.mode = transport.mode

  let hostEventRefreshActive = false
  let hostEventRefreshQueued = false
  async function refreshHostStateFromEvent() {
    if (store.bridge !== 'Connected') return
    if (hostEventRefreshActive) {
      hostEventRefreshQueued = true
      return
    }
    hostEventRefreshActive = true
    try {
      do {
        hostEventRefreshQueued = false
        const [appSnapshot, moduleSnapshot] = await Promise.all([
          appClient.getSnapshot(),
          moduleClient.getSnapshot(),
        ])
        store.rebuild(appSnapshot)
        moduleStore.complete(moduleSnapshot)
      } while (hostEventRefreshQueued)
    } catch {
      // A transient refresh failure must not discard the last confirmed state;
      // the next supervisor event or an explicit page refresh retries it.
    } finally {
      hostEventRefreshActive = false
    }
  }

  transport.subscribe(event => dispatcher.dispatch(event))
  dispatcher.on('app.snapshot', payload => store.rebuild(payload as AppSnapshot))
  dispatcher.on('app.hostEvent', payload => {
    const value = payload as { name?: unknown }
    store.lastEvent = typeof value.name === 'string' ? value.name : 'host event'
    if (value.name === 'module.changed') void refreshHostStateFromEvent()
  })

  app.mount('#app')

  const dispose = () => appClient.dispose()
  window.addEventListener('pagehide', dispose, { once: true })
  window.addEventListener('beforeunload', dispose, { once: true })

  async function connect() {
    if (document.readyState !== 'complete') {
      await new Promise<void>(resolve => window.addEventListener('load', () => resolve(), { once: true }))
    }
    const snapshot = await appClient.ready(transport.mode as 'WebView' | 'Mock')
    await appClient.ping()
    store.rebuild(snapshot)
    await appClient.ping()
    await appClient.ping()
    notifyHostTheme(useThemeStore(pinia).mode)
  }

  if (transport.mode === 'Unavailable') {
    store.bridge = 'Unavailable'
    store.error = 'Packaged Web Shell requires the host bridge.'
  } else {
    connect().catch(error => {
      store.bridge = 'Unavailable'
      store.error = String(error)
    })
  }
}
