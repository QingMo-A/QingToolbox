<script setup lang="ts">
import { computed, inject, onBeforeUnmount, onMounted, ref } from 'vue'
import type { HostUpdateClient } from '../../bridge/clients/HostUpdateClient'
import { useAppStore } from '../../app/store'
import { useHostUpdateStore } from '../../app/hostUpdateStore'
import { useLocalization } from '../../localization/localization'
import QButton from './QButton.vue'
import QIcon from './QIcon.vue'

const client=inject<HostUpdateClient>('hostUpdateClient')!
const app=useAppStore();const store=useHostUpdateStore();const {t}=useLocalization();const dismissedVersion=ref('')
let timer:number|undefined;let mounted=false;let operationRequiresPolling=false
const visible=computed(()=>app.snapshot?.environmentKind==='Production'&&store.snapshot?.showBanner===true&&dismissedVersion.value!==store.snapshot.latestVersion)
const progress=computed(()=>{const s=store.snapshot;if(!s||s.expectedBytes<=0)return 0;return Math.min(100,Math.round(s.bytesReceived/s.expectedBytes*100))})
const pageVisible=()=>document.visibilityState!=='hidden'
const snapshotNeedsPolling=()=>store.snapshot?.state==='Checking'||['Downloading','Verifying'].includes(store.snapshot?.downloadState??'')
const stopPolling=()=>{if(timer!==undefined){window.clearTimeout(timer);timer=undefined}}
function syncPolling(){
  if(!mounted||!pageVisible()||(!operationRequiresPolling&&!snapshotNeedsPolling())){stopPolling();return}
  if(timer!==undefined)return
  timer=window.setTimeout(async()=>{timer=undefined;await refreshProgress();syncPolling()},1000)
}
async function run(action:()=>Promise<import('../../contracts/hostUpdate').HostUpdateSnapshot>,pollWhilePending=false){
  if(store.busy)return
  store.busy=true;operationRequiresPolling=pollWhilePending;syncPolling()
  try{store.complete(await action())}catch(e){store.fail(e)}finally{store.busy=false;operationRequiresPolling=false;syncPolling()}
}
const refresh=()=>run(()=>client.getSnapshot())
async function refreshProgress(){try{store.complete(await client.getSnapshot())}catch(e){store.fail(e)}}
function onVisibilityChange(){if(!pageVisible()){stopPolling();return}void refresh().finally(syncPolling)}
onMounted(()=>{mounted=true;document.addEventListener('visibilitychange',onVisibilityChange);if(pageVisible())void refresh().finally(syncPolling)})
onBeforeUnmount(()=>{mounted=false;stopPolling();document.removeEventListener('visibilitychange',onVisibilityChange)})
</script>
<template>
  <section v-if="visible" class="q-host-update-banner" role="status">
    <QIcon name="download" :size="22" />
    <div><strong>{{ t('hostUpdate.banner.title',{version:store.snapshot!.latestVersion}) }}</strong><p>{{ store.snapshot!.summary || t('hostUpdate.banner.description') }}</p><small v-if="store.snapshot!.downloadState==='Downloading'">{{ t('hostUpdate.banner.progress',{progress}) }}</small><small v-else-if="store.error">{{ t('hostUpdate.banner.failed') }}</small></div>
    <div class="q-host-update-actions">
      <QButton @click="dismissedVersion=store.snapshot!.latestVersion">{{ t('hostUpdate.banner.later') }}</QButton>
      <QButton v-if="store.snapshot!.canCancelDownload" :disabled="store.busy" @click="run(()=>client.cancel())">{{ t('hostUpdate.banner.cancel') }}</QButton>
      <QButton v-if="store.snapshot!.canDownload" variant="primary" :disabled="store.busy" @click="run(()=>client.download(),true)">{{ t('hostUpdate.banner.download') }}</QButton>
      <QButton v-if="store.snapshot!.canInstall" variant="primary" :disabled="store.busy" @click="run(()=>client.install())">{{ t('hostUpdate.banner.install') }}</QButton>
      <QButton v-if="store.snapshot!.canCheck&&!store.snapshot!.canDownload&&!store.snapshot!.canInstall" :disabled="store.busy" @click="run(()=>client.check())">{{ t('hostUpdate.banner.check') }}</QButton>
    </div>
  </section>
</template>
<style scoped>
.q-host-update-banner{display:flex;align-items:center;gap:12px;margin:14px 24px 0;padding:12px 14px;border:1px solid color-mix(in srgb,var(--q-brand) 28%,var(--q-border));border-radius:12px;background:color-mix(in srgb,var(--q-brand) 7%,var(--q-surface));color:var(--q-text-1)}
.q-host-update-banner>div:nth-child(2){min-width:0;flex:1}.q-host-update-banner strong{display:block}.q-host-update-banner p{margin:3px 0 0;color:var(--q-text-2);font-size:12px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.q-host-update-banner small{display:block;margin-top:4px;color:var(--q-text-2)}.q-host-update-actions{display:flex;flex-wrap:wrap;justify-content:flex-end;gap:8px}@media(max-width:700px){.q-host-update-banner{align-items:flex-start;flex-wrap:wrap}.q-host-update-actions{width:100%;justify-content:flex-start}.q-host-update-banner p{white-space:normal}}
</style>
