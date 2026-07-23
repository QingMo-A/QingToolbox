<script setup lang="ts">
import { inject } from 'vue'
import { useAppStore } from '../app/store'
import type { AppClient } from '../bridge/clients/AppClient'
const app = inject<AppClient>('appClient')!
const store = useAppStore()
async function ping() { const started = performance.now(); try { await app.ping(); store.pingMs = Math.round(performance.now() - started) } catch (e) { store.error = String(e) } }
async function snapshot() { try { store.rebuild(await app.getSnapshot()) } catch (e) { store.error = String(e) } }
function reload() { window.location.reload() }
</script>
<template><main>
  <header><p class="eyebrow">DEVELOPMENT ONLY</p><h1>QingToolbox Development Web Shell</h1><p>A read-only projection of authoritative C# host state.</p></header>
  <section class="status" aria-live="polite"><span :class="{ ok: store.bridge === 'Connected' }">{{ store.bridge }}</span><strong>{{ store.mode }} mode</strong><span v-if="store.error">{{ store.error }}</span></section>
  <section class="grid" v-if="store.snapshot">
    <article><label>Environment</label><strong>{{ store.snapshot.environmentDisplayName }}</strong></article><article><label>Host Version</label><strong>{{ store.snapshot.hostVersion }}</strong></article><article><label>Protocol</label><strong>v{{ store.snapshot.protocolVersion }}</strong></article>
    <article><label>Total Modules</label><strong>{{ store.snapshot.totalModuleCount }}</strong></article><article><label>Valid Modules</label><strong>{{ store.snapshot.validModuleCount }}</strong></article><article><label>Running Modules</label><strong>{{ store.snapshot.runningModuleCount }}</strong></article>
    <article><label>Last Snapshot</label><strong>{{ new Date(store.snapshot.generatedAt).toLocaleTimeString() }}</strong></article><article><label>Last Host Event</label><strong>{{ store.lastEvent }}</strong></article><article><label>Ping</label><strong>{{ store.pingMs === null ? 'Not run' : `${store.pingMs} ms` }}</strong></article>
  </section>
  <nav><button @click="ping">Ping Host</button><button @click="snapshot">Request Snapshot</button><button @click="reload">Reload Web UI</button></nav>
</main></template>
