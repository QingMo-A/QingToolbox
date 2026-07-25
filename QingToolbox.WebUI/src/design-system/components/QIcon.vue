<script setup lang="ts">
type QIconName = 'home'|'modules'|'running'|'logs'|'settings'|'diagnostics'|'pin'|'unpin'|'refresh'|'statusSuccess'|'statusInfo'|'statusWarning'|'statusDanger'|'back'|'search'|'close'
defineProps<{ name: QIconName; size?: number }>()
</script>

<template><svg class="q-icon" :width="size??20" :height="size??20" viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false">
  <template v-if="name==='home'"><path d="M3 9.1 10 3l7 6.1"/><path d="M5 8.3V17h10V8.3M8 17v-5h4v5"/></template>
  <template v-else-if="name==='modules'"><rect x="3" y="3" width="5" height="5" rx="1"/><rect x="12" y="3" width="5" height="5" rx="1"/><rect x="3" y="12" width="5" height="5" rx="1"/><rect x="12" y="12" width="5" height="5" rx="1"/></template>
  <template v-else-if="name==='running'"><path d="M7.2 4.3 15 10l-7.8 5.7V4.3Z"/><circle cx="10" cy="10" r="8"/></template>
  <template v-else-if="name==='logs'"><path d="M4 4h12v12H4z"/><path d="M7 7h6M7 10h6M7 13h4"/></template>
  <template v-else-if="name==='settings'"><circle cx="10" cy="10" r="2.5"/><path d="M10 2.8v2M10 15.2v2M2.8 10h2M15.2 10h2M4.9 4.9l1.4 1.4M13.7 13.7l1.4 1.4M15.1 4.9l-1.4 1.4M6.3 13.7l-1.4 1.4"/></template>
  <template v-else-if="name==='diagnostics'"><path d="M3 15.5h14M4 12l3-3 2.5 2.5L14.5 6 17 8.5"/><circle cx="7" cy="9" r=".7" fill="currentColor" stroke="none"/><circle cx="14.5" cy="6" r=".7" fill="currentColor" stroke="none"/></template>
  <template v-else-if="name==='pin'||name==='unpin'"><path v-if="name==='unpin'" d="m4 4 12 12"/><path d="m7 3 6 6-1.8 1.8.5 3.2-1.2 1.2-2.8-2.8L4 16l3.6-3.7-2.8-2.8L6 8.3l3.2.5L11 7"/></template>
  <template v-else-if="name==='refresh'"><path d="M16.5 7A7 7 0 1 0 17 11"/><path d="M13 3h4v4"/></template>
  <template v-else-if="name==='statusSuccess'"><circle cx="10" cy="10" r="7"/><path d="m6.8 10.2 2.1 2.1 4.5-4.6"/></template>
  <template v-else-if="name==='statusInfo'"><circle cx="10" cy="10" r="7"/><path d="M10 9v4M10 6.5h.01"/></template>
  <template v-else-if="name==='statusWarning'"><path d="M9 3.7 2.8 15a1.2 1.2 0 0 0 1.1 1.8h12.2a1.2 1.2 0 0 0 1.1-1.8L11 3.7a1.2 1.2 0 0 0-2 0Z"/><path d="M10 7v4M10 14h.01"/></template>
  <template v-else-if="name==='statusDanger'"><circle cx="10" cy="10" r="7"/><path d="m7.5 7.5 5 5m0-5-5 5"/></template>
  <template v-else-if="name==='back'"><path d="m9 4-6 6 6 6M3 10h14"/></template>
  <template v-else-if="name==='search'"><circle cx="8.5" cy="8.5" r="5.5"/><path d="m13 13 4 4"/></template>
  <template v-else-if="name==='close'"><path d="m5 5 10 10M15 5 5 15"/></template>
</svg></template>
