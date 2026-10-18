<script setup lang="ts">
defineProps<{ name: 'document' | 'merge' | 'split' | 'extract' | 'rotate' | 'add' | 'up' | 'down' | 'grip' }>()
</script>

<template>
  <svg class="pdf-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false">
    <template v-if="name === 'document'"><path d="M14 3H6a1 1 0 0 0-1 1v16a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1V8z"/><path d="M14 3v5h5M8 12h8M8 16h5"/></template>
    <template v-else-if="name === 'merge'"><path d="M5 4v4c0 3 7 3 7 6v6M19 4v4c0 3-7 3-7 6M8 17l4 4 4-4"/></template>
    <template v-else-if="name === 'split'"><path d="M12 3v5c0 3-7 3-7 6v6M12 8c0 3 7 3 7 6v6M2 17l3 3 3-3M16 17l3 3 3-3"/></template>
    <template v-else-if="name === 'extract'"><rect x="4" y="3" width="12" height="16" rx="2"/><path d="M16 7h4v13a1 1 0 0 1-1 1H9M7 8h6M7 12h4"/></template>
    <template v-else-if="name === 'rotate'"><path d="M20 10a8 8 0 1 0-1 7M20 3v7h-7"/></template>
    <template v-else-if="name === 'add'"><path d="M12 5v14M5 12h14"/></template>
    <template v-else-if="name === 'up'"><path d="m6 14 6-6 6 6"/></template>
    <template v-else-if="name === 'down'"><path d="m6 10 6 6 6-6"/></template>
    <template v-else><path d="M9 5h.01M15 5h.01M9 12h.01M15 12h.01M9 19h.01M15 19h.01" stroke-width="3"/></template>
  </svg>
</template>
