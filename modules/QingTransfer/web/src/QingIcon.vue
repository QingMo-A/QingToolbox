<script setup lang="ts">
withDefaults(defineProps<{ name: 'refresh' | 'desktop' | 'phone' | 'send' | 'disconnect' | 'folder' | 'close' | 'nearby' }>(), { name: 'nearby' })
</script>

<template>
  <svg class="q-icon" viewBox="0 0 24 24" aria-hidden="true" focusable="false">
    <path v-if="name === 'refresh'" d="M20 11a8 8 0 0 0-14.8-4L3 9m0 0V4m0 5h5M4 13a8 8 0 0 0 14.8 4L21 15m0 0v5m0-5h-5" />
    <path v-else-if="name === 'desktop'" d="M4 5.5h16v10H4zM9 19h6m-3-3.5V19" />
    <path v-else-if="name === 'phone'" d="M8 3.5h8a1.5 1.5 0 0 1 1.5 1.5v14a1.5 1.5 0 0 1-1.5 1.5H8A1.5 1.5 0 0 1 6.5 19V5A1.5 1.5 0 0 1 8 3.5Zm2 13.5h4" />
    <path v-else-if="name === 'send'" d="m4 12 16-7-4 14-4-6-8-1Zm8 1 8-8" />
    <path v-else-if="name === 'disconnect'" d="m8 8-4 4 4 4m8-8 4 4-4 4M4 12h16" />
    <path v-else-if="name === 'folder'" d="M3.5 6.5h6l2 2h9v9h-17z" />
    <path v-else-if="name === 'close'" d="m6 6 12 12M18 6 6 18" />
    <path v-else d="M12 3v4m0 10v4m9-9h-4M7 12H3m15.4-6.4-2.8 2.8M8.4 15.6l-2.8 2.8m11.8 0-2.8-2.8M8.4 8.4 5.6 5.6" />
  </svg>
</template>

<style scoped>
.q-icon { width: 1em; height: 1em; fill: none; stroke: currentColor; stroke-width: 1.8; stroke-linecap: round; stroke-linejoin: round; }
</style>
