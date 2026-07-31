<script setup lang="ts">
import { inject } from 'vue'
import type { HostUpdateClient } from '../../bridge/clients/HostUpdateClient'
import { useHostUpdateStore } from '../../app/hostUpdateStore'
import { useLocalization } from '../../localization/localization'
import QButton from './QButton.vue'

const client=inject<HostUpdateClient>('hostUpdateClient')!;const store=useHostUpdateStore();const {t}=useLocalization()
async function run(action:()=>Promise<import('../../contracts/hostUpdate').HostUpdateSnapshot>){if(store.busy)return;store.busy=true;try{store.complete(await action())}catch(e){store.fail(e)}finally{store.busy=false}}
</script>
<template>
  <article class="settings-card host-update-panel">
    <div><h3>{{ t('hostUpdate.panel.title') }}</h3><p>{{ t('hostUpdate.panel.description') }}</p></div>
    <dl v-if="store.snapshot" class="settings-values"><div><dt>{{ t('hostUpdate.panel.current') }}</dt><dd>{{ store.snapshot.currentVersion }}</dd></div><div><dt>{{ t('hostUpdate.panel.latest') }}</dt><dd>{{ store.snapshot.latestVersion }}</dd></div><div><dt>{{ t('hostUpdate.panel.status') }}</dt><dd>{{ store.snapshot.state }}</dd></div><div><dt>{{ t('hostUpdate.panel.lastChecked') }}</dt><dd>{{ store.snapshot.lastChecked || '—' }}</dd></div></dl>
    <p v-if="store.snapshot?.summary" class="settings-host-message">{{ store.snapshot.summary }}</p><p v-if="store.error" class="settings-inline-error">{{ t('hostUpdate.banner.failed') }}</p>
    <div class="host-update-panel-actions"><QButton :disabled="store.busy||!store.snapshot?.canCheck" @click="run(()=>client.check())">{{ t('hostUpdate.banner.check') }}</QButton><QButton v-if="store.snapshot?.canCancelDownload" :disabled="store.busy" @click="run(()=>client.cancel())">{{ t('hostUpdate.banner.cancel') }}</QButton><QButton v-if="store.snapshot?.canDownload" variant="primary" :disabled="store.busy" @click="run(()=>client.download())">{{ t('hostUpdate.banner.download') }}</QButton><QButton v-if="store.snapshot?.canInstall" variant="primary" :disabled="store.busy" @click="run(()=>client.install())">{{ t('hostUpdate.banner.install') }}</QButton></div>
  </article>
</template>
<style scoped>.host-update-panel-actions{display:flex;flex-wrap:wrap;gap:8px;margin-top:12px}</style>
