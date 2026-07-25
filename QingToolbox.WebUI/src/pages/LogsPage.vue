<script setup lang="ts">
import { computed,inject,watch } from 'vue'
import type { LogClient } from '../bridge/clients/LogClient'
import { useAppStore } from '../app/store'
import { useLogStore } from '../app/logStore'
import QPage from '../design-system/components/QPage.vue'
import QButton from '../design-system/components/QButton.vue'
import QIcon from '../design-system/components/QIcon.vue'
import QSkeleton from '../design-system/components/QSkeleton.vue'
const client=inject<LogClient>('logClient')!;const app=useAppStore();const logs=useLogStore()
const refreshed=computed(()=>logs.generatedAt?new Date(logs.generatedAt).toLocaleTimeString():null)
const time=(value:string)=>new Date(value).toLocaleTimeString([],{hour:'2-digit',minute:'2-digit',second:'2-digit'})
async function refresh(){logs.begin();try{logs.complete(await client.getSnapshot())}catch(error){logs.fail(error)}}
watch(()=>app.bridge,bridge=>{if(bridge==='Connected'&&logs.status==='idle')void refresh()},{immediate:true})
</script>
<template><QPage class="logs-page"><header class="wpf-page-header"><div><h1>Session logs</h1><p>Read-only events recorded during this application session.</p></div><QButton :disabled="app.bridge!=='Connected'||logs.status==='loading'" @click="refresh"><QIcon name="refresh"/> Refresh</QButton></header><p v-if="logs.status==='ready'" class="logs-summary">{{logs.entries.length}} entries<span v-if="refreshed"> · Refreshed {{refreshed}}</span></p><section v-if="app.bridge!=='Connected'&&logs.status==='idle'" class="q-empty logs-state"><div class="q-empty-icon"><QIcon name="logs" :size="28"/></div><h3>Connecting to the host</h3><p>Session logs will appear after the secure bridge is ready.</p></section><section v-else-if="logs.status==='loading'" class="logs-skeleton" aria-label="Loading session logs"><QSkeleton v-for="item in 6" :key="item"/></section><section v-else-if="logs.status==='error'" class="q-empty logs-state"><div class="q-empty-icon"><QIcon name="statusDanger" :size="28"/></div><h3>Session logs are unavailable</h3><p>{{logs.errorMessage}}</p><QButton @click="refresh"><QIcon name="refresh"/> Retry</QButton></section><section v-else-if="logs.status==='ready'&&logs.entries.length===0" class="q-empty logs-state"><div class="q-empty-icon"><QIcon name="logs" :size="28"/></div><h3>No session entries yet</h3><p>New in-memory events will be shown after a manual refresh.</p></section><section v-else-if="logs.entries.length" class="logs-table" aria-label="Session log entries"><div class="logs-head"><span>Time</span><span>Level</span><span>Category</span><span>Message</span></div><article v-for="(entry,index) in logs.entries" :key="`${entry.timestamp}-${index}`" class="logs-row"><time :datetime="entry.timestamp">{{time(entry.timestamp)}}</time><span class="log-level" :class="`is-${entry.level.toLowerCase()}`">{{entry.level}}</span><strong>{{entry.category}}</strong><p>{{entry.message}}</p></article></section></QPage></template>
