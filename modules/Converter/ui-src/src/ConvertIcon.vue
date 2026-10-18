<script setup lang="ts">
withDefaults(defineProps<{ name: 'swap' | 'image' | 'audio' | 'document' | 'plus' | 'arrow'; size?: number }>(), { size: 22 })
</script>
<template><svg class="convert-icon" :width="size" :height="size" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
  <template v-if="name === 'swap'"><path d="M4 7h16l-4-4M20 7l-4 4M20 17H4l4 4M4 17l4-4"/></template>
  <template v-else-if="name === 'image'"><rect x="3" y="3" width="18" height="18" rx="3"/><circle cx="8" cy="8" r="1.5"/><path d="m3 17 5-5 4 4 4-6 5 7"/></template>
  <template v-else-if="name === 'audio'"><path d="M9 18V5l11-2v13M9 7l11-2"/><ellipse cx="6" cy="18" rx="3" ry="2.5"/><ellipse cx="17" cy="16" rx="3" ry="2.5"/></template>
  <template v-else-if="name === 'document'"><path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8Z"/><path d="M14 2v6h6M8 13h8M8 17h6"/></template>
  <path v-else-if="name === 'plus'" d="M12 5v14M5 12h14"/>
  <path v-else d="M4 12h16m-6-6 6 6-6 6"/>
</svg></template>
