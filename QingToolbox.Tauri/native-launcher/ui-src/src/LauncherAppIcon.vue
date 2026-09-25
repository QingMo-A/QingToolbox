<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { cachedItemIcon, loadItemIcon } from './itemIcons'
const props = defineProps<{ iconKey: string | null }>()
const root = ref<HTMLElement | null>(null)
const source = ref<string | null>(null)
const visible = ref(false)
let observer: IntersectionObserver | undefined
let generation = 0
watch([() => props.iconKey, visible], async ([key, show]) => {
  const current = ++generation
  const cached = cachedItemIcon(key)
  source.value = cached ?? null
  if (cached !== undefined) return
  if (!show) return
  const url = await loadItemIcon(key)
  if (current === generation) source.value = url
}, { immediate: true })
onMounted(() => {
  observer = new IntersectionObserver(entries => {
    if (entries.some(entry => entry.isIntersecting)) { visible.value = true; observer?.disconnect() }
  }, { rootMargin: '100px' })
  if (root.value) observer.observe(root.value)
})
onBeforeUnmount(() => { generation++; observer?.disconnect() })
</script>

<template>
  <span ref="root" class="launcher-app-image" aria-hidden="true">
    <img v-if="source" :src="source" alt="" draggable="false" @error="source = null" />
    <svg v-else viewBox="0 0 64 64" fill="none"><rect x="12" y="12" width="40" height="40" rx="12" fill="currentColor" opacity=".08"/><path d="M24 24h6v6h-6zm10 0h6v6h-6zm-10 10h6v6h-6zm10 0h6v6h-6z" stroke="currentColor" stroke-width="1.8" stroke-linejoin="round"/></svg>
  </span>
</template>

<style scoped>
.launcher-app-image { display: grid; place-items: center; width: 100%; height: 100%; min-width: 0; min-height: 0; color: #7199b7; }
.launcher-app-image > img, .launcher-app-image > svg { display: block; width: 100%; height: 100%; object-fit: contain; }
</style>
