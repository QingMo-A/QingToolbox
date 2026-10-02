<script setup lang="ts">
defineProps<{ name: string }>()
const paths: Record<string, string> = {
  grid: 'M4 4h6v6H4zM14 4h6v6h-6zM4 14h6v6H4zM14 14h6v6h-6z',
  search: 'M16 16l4.5 4.5M17 10.5a6.5 6.5 0 1 1-13 0 6.5 6.5 0 0 1 13 0',
  settings: 'M9 4l.7-2h4.6l.7 2 2 .9 2.2-.4 2.3 4-1.5 1.7v2.3l1.5 1.7-2.3 4-2.2-.4-2 .9-.7 2H9l-.7-2-2-.9-2.2.4-2.3-4 1.5-1.7v-2.3L1.8 8.5l2.3-4 2.2.4L9 4zM15.2 11.8a3.2 3.2 0 1 1-6.4 0 3.2 3.2 0 0 1 6.4 0',
  refresh: 'M20 7v5h-5M4 17v-5h5M19 11a7 7 0 0 0-12-5L4 9m16 6-3 3A7 7 0 0 1 5 13',
  folder: 'M3 7h6l2 2h10v10a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V7z',
  folderAdd: 'M3 7h6l2 2h10v10a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V7zM12 12v5m-2.5-2.5h5',
  close: 'M6 6l12 12M18 6 6 18',
  file: 'M6 3h8l4 4v14H6zM14 3v5h4',
  upload: 'M12 16V3m-5 5 5-5 5 5M4 15v5a1 1 0 0 0 1 1h14a1 1 0 0 0 1-1v-5',
  clock: 'M21 12a9 9 0 1 1-18 0 9 9 0 0 1 18 0M12 7v5l3 2',
}
</script>
<template><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.65" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path :d="paths[name] || paths.grid" /></svg></template>
