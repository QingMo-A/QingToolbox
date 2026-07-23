import { createApp } from 'vue'
import { createPinia } from 'pinia'
import App from './app/App.vue'
import { router } from './app/router'
import { WebViewTransport } from './bridge/transport/WebViewTransport'
import { MockTransport } from './bridge/transport/MockTransport'
import { RequestClient } from './bridge/protocol/RequestClient'
import { EventDispatcher } from './bridge/protocol/EventDispatcher'
import { AppClient } from './bridge/clients/AppClient'
import { useAppStore } from './app/store'
import type { AppSnapshot } from './contracts/app'
import './styles/main.css'

const transport = WebViewTransport.isAvailable() ? new WebViewTransport() : new MockTransport()
const dispatcher = new EventDispatcher(); transport.subscribe(event => dispatcher.dispatch(event))
const appClient = new AppClient(new RequestClient(transport))
const app = createApp(App); const pinia = createPinia(); app.use(pinia); app.use(router); app.provide('appClient', appClient)
const store = useAppStore(pinia); store.mode = transport.mode
dispatcher.on('app.snapshot', payload => store.rebuild(payload as AppSnapshot))
dispatcher.on('app.hostEvent', payload => { store.lastEvent = String((payload as { name?: string }).name ?? 'host event') })
app.mount('#app')
appClient.ready().then(snapshot => store.rebuild(snapshot)).catch(error => { store.bridge = 'Unavailable'; store.error = String(error) })
