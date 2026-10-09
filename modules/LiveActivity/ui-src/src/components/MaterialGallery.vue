<script setup lang="ts">
import { computed } from 'vue'
import type { RgbColor, SurfaceStyle } from '../types'

const props = defineProps<{ selected: SurfaceStyle; backgroundColor: RgbColor; disabled: boolean }>()
defineEmits<{ select: [style: SurfaceStyle] }>()

/**
 * The three materials as swatches over the same busy scene. The tiles carry no
 * text inside the sample, so each button's accessible text is just its name;
 * translucent samples always use 60% so the difference is visible even when
 * the user's own strength is close to opaque.
 */
const styles: { value: SurfaceStyle; name: string; note: string; title: string }[] = [
  { value: 'solid', name: '实色', note: '100%', title: '完全遮住背景' },
  { value: 'translucent', name: '半透明', note: '示例 60%', title: '透出清晰的背景' },
  { value: 'frosted', name: '磨砂玻璃', note: '示例 60%', title: '模糊背景后着色；此模式下灵动岛不进入系统截屏' },
]
const tint = computed(() => {
  const clamp = (value: number) => Math.round(Math.max(0, Math.min(255, Number(value) || 0)))
  const { r, g, b } = props.backgroundColor
  return {
    '--tile-tint': `${clamp(r)} ${clamp(g)} ${clamp(b)}`,
    '--tile-ink': r * 0.2126 + g * 0.7152 + b * 0.0722 > 150 ? '#142030' : '#eff1f5',
  }
})
</script>

<template>
  <div class="segmented material-gallery" role="group" aria-label="面板材质" :style="tint">
    <button
      v-for="item in styles"
      :key="item.value"
      class="material-tile"
      :class="{ selected: selected === item.value }"
      :disabled="disabled"
      type="button"
      :title="item.title"
      :aria-pressed="selected === item.value"
      :data-note="item.note"
      @click="$emit('select', item.value)"
    >
      <span class="material-swatch" aria-hidden="true">
        <i class="scene" />
        <i class="pill" :class="`m-${item.value}`"><i class="pill-dot" /><i class="pill-line" /><i class="pill-time" /></i>
        <svg class="check" viewBox="0 0 16 16"><path d="M4 8.4l2.6 2.6L12 5.4" /></svg>
      </span>
      <strong>{{ item.name }}</strong>
    </button>
  </div>
</template>

<style scoped>
.segmented.material-gallery {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 10px;
  padding: 0;
  border: 0;
  background: none;
}
.segmented .material-tile {
  position: relative;
  display: flex;
  min-width: 0;
  flex-direction: column;
  align-items: stretch;
  gap: 0;
  padding: 6px;
  border: 1px solid var(--q-border);
  border-radius: 14px;
  color: var(--q-text-2);
  background: var(--q-surface-soft);
  text-align: left;
  cursor: pointer;
  transition: border-color 170ms, box-shadow 170ms, transform 170ms cubic-bezier(.2, .8, .2, 1);
}
.segmented .material-tile:hover:not(:disabled) { transform: translateY(-2px); border-color: color-mix(in srgb, var(--q-brand) 55%, var(--q-border)); }
.segmented .material-tile:active:not(:disabled) { transform: scale(.98); }
.segmented .material-tile.selected { color: var(--q-text); border-color: var(--q-brand); background: var(--q-surface-soft); box-shadow: 0 0 0 3px var(--q-brand-soft); font-weight: inherit; }
.segmented .material-tile:disabled { opacity: .6; cursor: default; }
.material-tile strong { padding: 9px 6px 3px; font-size: 12.5px; font-weight: 650; }
.material-tile::after { content: attr(data-note); position: absolute; right: 12px; bottom: 8px; color: var(--q-text-3); font: 600 10.5px/1.4 ui-monospace, "Cascadia Mono", Consolas, monospace; }

.material-swatch { position: relative; display: grid; place-items: center; height: 78px; overflow: hidden; border-radius: 10px; isolation: isolate; }
.scene {
  position: absolute;
  inset: 0;
  z-index: -1;
  background:
    radial-gradient(circle at 22% 38%, #fbbf24 0 15%, transparent 16%),
    radial-gradient(circle at 78% 64%, #f472b6 0 18%, transparent 19%),
    repeating-linear-gradient(115deg, #4f8cff 0 7px, #8b5cf6 7px 14px, #22d3ee 14px 21px),
    #1e293b;
}
.pill {
  display: flex;
  align-items: center;
  gap: 6px;
  width: 78%;
  height: 26px;
  padding: 0 10px;
  border-radius: 13px;
  box-shadow: inset 0 1px 0 rgb(255 255 255 / .22), inset 0 0 0 1px rgb(255 255 255 / .08), 0 4px 12px rgb(0 0 0 / .25);
}
.pill.m-solid { background: rgb(var(--tile-tint)); }
.pill.m-translucent { background: rgb(var(--tile-tint) / .6); }
.pill.m-frosted { background: rgb(var(--tile-tint) / .6); backdrop-filter: blur(6px) saturate(1.4); }
.pill i { display: block; flex: none; height: 4px; border-radius: 2px; background: var(--tile-ink); }
.pill .pill-dot { width: 7px; height: 7px; border-radius: 50%; background: #86b4f0; }
.pill .pill-line { flex: 1; opacity: .9; }
.pill .pill-time { width: 18%; opacity: .75; }
.check {
  position: absolute;
  top: 6px;
  right: 6px;
  width: 18px;
  height: 18px;
  padding: 2px;
  border-radius: 50%;
  fill: none;
  stroke: #fff;
  stroke-width: 2;
  stroke-linecap: round;
  stroke-linejoin: round;
  background: var(--q-brand);
  opacity: 0;
  transform: scale(.6);
  transition: opacity 160ms, transform 200ms cubic-bezier(.2, .9, .3, 1.3);
}
.selected .check { opacity: 1; transform: none; }

:root[data-appearance-preset='qing-nova'] .segmented .material-tile { border: 3px solid #111; border-radius: 0; box-shadow: 4px 4px 0 #111; }
:root[data-appearance-preset='qing-nova'] .segmented .material-tile.selected { background: #ffe14d; }
:root[data-appearance-preset='qing-nova'] .material-swatch { border-radius: 0; }
:root[data-appearance-preset='neon-circuit'] .segmented .material-tile,
:root[data-appearance-preset='neon-circuit'] .material-swatch { border-radius: 3px; }
:root[data-appearance-preset='aurora-flow'] .segmented .material-tile { border-radius: 4px; }
:root[data-appearance-preset='aurora-flow'] .segmented .material-tile.selected { box-shadow: 0 0 0 1px #00f0ff, 0 0 18px rgb(0 240 255 / .3); }

@media (max-width: 560px) { .material-swatch { height: 60px; } .material-tile::after { display: none; } }
@media (max-width: 380px) { .segmented.material-gallery { grid-template-columns: minmax(0, 1fr); } }
@media (prefers-reduced-motion: reduce) { .segmented .material-tile, .check { transition: none; } }
</style>
