<script setup lang="ts">
defineProps<{ name: 'lock' | 'key' | 'eye' | 'eyeOff' | 'copy' | 'plus' | 'link' | 'check' | 'book' | 'vault' | 'shield' | 'user' | 'search'; size?: number }>()
</script>
<template>
  <svg :width="size ?? 20" :height="size ?? 20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false">
    <template v-if="name === 'lock'"><rect x="5" y="10" width="14" height="11" rx="3"/><path d="M8 10V7a4 4 0 0 1 8 0v3M12 15v2"/></template>
    <template v-else-if="name === 'key'"><circle cx="8" cy="9" r="5"/><path d="m12 13 8 8m-5-5 3-3m-1 5 3-3M6.5 7.5h.01"/></template>
    <template v-else-if="name === 'eye'"><path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7-10-7-10-7Z"/><circle cx="12" cy="12" r="3"/></template>
    <template v-else-if="name === 'eyeOff'"><path d="m3 3 18 18M10.6 5.1A13 13 0 0 1 12 5c6.5 0 10 7 10 7a19 19 0 0 1-3.1 4.1M6 6a21 21 0 0 0-4 6s3.5 7 10 7a12 12 0 0 0 5-1.2M9.9 9.9a3 3 0 0 0 4.2 4.2"/></template>
    <template v-else-if="name === 'copy'"><rect x="8" y="8" width="12" height="13" rx="3"/><path d="M15 5V4a2 2 0 0 0-2-2H5a2 2 0 0 0-2 2v9a2 2 0 0 0 2 2h.5"/></template>
    <template v-else-if="name === 'plus'"><path d="M12 5v14M5 12h14"/></template>
    <template v-else-if="name === 'link'"><path d="m10 13 4-4m-3-3 2-2a5 5 0 0 1 7 7l-3 3m-4 3-2 3a5 5 0 0 1-7-7l3-3"/></template>
    <template v-else-if="name === 'check'"><path d="m5 12 4.5 4.5L19 7"/></template>
    <template v-else-if="name === 'vault'"><rect x="3" y="3" width="18" height="18" rx="4"/><circle cx="12" cy="12" r="4.2"/><path d="M12 7.8v1.4M12 14.8v1.4M7.8 12h1.4M14.8 12h1.4M3 8h-.01M3 16h-.01"/></template>
    <template v-else-if="name === 'shield'"><path d="M12 3 4.5 6v5.5c0 4.6 3.2 8.2 7.5 9.5 4.3-1.3 7.5-4.9 7.5-9.5V6Z"/><path d="m9 12 2 2 4-4"/></template>
    <template v-else-if="name === 'user'"><circle cx="12" cy="8" r="4"/><path d="M4.5 20.5a7.5 7.5 0 0 1 15 0"/></template>
    <template v-else-if="name === 'search'"><circle cx="11" cy="11" r="6.5"/><path d="m20 20-4.4-4.4"/></template>
    <template v-else><rect x="4" y="3" width="16" height="18" rx="3"/><path d="M9 3v18m4-12h3m-3 4h3"/></template>
  </svg>
</template>
