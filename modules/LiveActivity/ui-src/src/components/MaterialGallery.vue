<script setup lang="ts">
import type { RgbColor, SurfaceStyle } from '../types'
import IslandPreview from './IslandPreview.vue'
defineProps<{ selected: SurfaceStyle; backgroundColor: RgbColor; customText: string; disabled: boolean }>()
defineEmits<{ select: [style: SurfaceStyle] }>()
const styles: { value: SurfaceStyle; name: string }[] = [
  { value: 'solid', name: '实色' }, { value: 'translucent', name: '半透明' }, { value: 'frosted', name: '磨砂玻璃' },
]
</script>

<template>
  <div class="material-gallery">
    <button v-for="item in styles" :key="item.value" class="material-tile" :class="{ selected: selected === item.value }" :disabled="disabled" type="button" :aria-label="`使用${item.name}`" :aria-pressed="selected === item.value" @click="$emit('select', item.value)">
      <IslandPreview mini anchor="topCenter" :scale="0.88" state="peek" :surface-style="item.value" :opacity="0.6" :background-color="backgroundColor" clock="12:34" :custom-text="customText" />
      <span class="material-title"><strong>{{ item.name }}</strong><span>{{ item.value === 'solid' ? '100%' : '60%' }}</span></span>
    </button>
  </div>
</template>

<style scoped>
.material-gallery { display:grid; grid-template-columns:repeat(3,minmax(0,1fr)); gap:12px; }
.material-tile { min-width:0; overflow:hidden; padding:0; border:1px solid var(--q-border); border-radius:14px; background:var(--q-surface-soft); text-align:left; cursor:pointer; transition:border-color 170ms, transform 170ms ease-out, box-shadow 170ms; }
.material-tile:hover:not(:disabled) { transform:translateY(-2px); border-color:var(--q-brand); box-shadow:0 7px 18px color-mix(in srgb,var(--q-brand) 10%,transparent); }
.material-tile:active:not(:disabled) { transform:scale(.985); }
.material-tile.selected { border-color:var(--q-brand); box-shadow:0 0 0 2px var(--q-brand-soft); }
.material-tile:disabled { opacity:.6; cursor:default; }
.material-title { display:flex; align-items:center; justify-content:space-between; padding:12px 14px; color:var(--q-text); font-size:12px; }
.material-title > span { color:var(--q-text-3); font-size:11px; font-variant-numeric:tabular-nums; }
@media(max-width:650px) { .material-gallery { grid-template-columns:minmax(0,1fr); } }
@media(prefers-reduced-motion:reduce) { .material-tile { transition:none; } }
</style>
