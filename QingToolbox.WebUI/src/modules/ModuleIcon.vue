<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { isModuleIconDataUrl } from '../contracts/modules'

const props = defineProps<{
  iconDataUrl?: string|null
  alt: string
  fallback?: string
}>()

const imageAvailable = ref(isModuleIconDataUrl(props.iconDataUrl))

watch(() => props.iconDataUrl, value => {
  imageAvailable.value = isModuleIconDataUrl(value)
})

const fallbackLabel = computed(() => {
  const value = (props.fallback ?? props.alt).trim()
  return value ? value.slice(0, 1).toUpperCase() : '?'
})

function showFallback() {
  imageAvailable.value = false
}
</script>

<template>
  <span class="module-icon" role="img" :aria-label="alt" :data-icon-state="imageAvailable ? 'image' : 'fallback'">
    <img v-if="imageAvailable" :src="iconDataUrl!" alt="" aria-hidden="true" @error="showFallback">
    <span v-else aria-hidden="true">{{ fallbackLabel }}</span>
  </span>
</template>

<style scoped>
.module-icon > img {
  display: block;
  width: 100%;
  height: 100%;
  object-fit: contain;
}

/* Real module artwork owns its own surface; the blue gradient belongs only to the
   first-letter fallback. Keep the state explicit so every host workspace shares it. */
.module-icon[data-icon-state='image'] {
  background: transparent;
}
</style>
