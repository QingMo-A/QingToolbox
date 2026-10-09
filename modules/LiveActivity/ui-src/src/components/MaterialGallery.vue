<script setup lang="ts">
import { computed } from 'vue'
import type { RgbColor, SurfaceStyle } from '../types'
import { FROST_GRAIN, gelPalette, isLight, isMuted } from '../material'

const props = defineProps<{ selected: SurfaceStyle; backgroundColor: RgbColor; disabled: boolean }>()
defineEmits<{ select: [style: SurfaceStyle] }>()

/**
 * The four materials as swatches over the same busy scene. The tiles carry no
 * text inside the sample, so each button's accessible text is just its name;
 * translucent samples always use 60% so the difference is visible even when
 * the user's own strength is close to opaque, and a grey colour is shown as
 * cherry on the jelly tile, because a grey gel sells nothing.
 */
const styles: { value: SurfaceStyle; name: string; note: string; title: string }[] = [
  { value: 'solid', name: '实色', note: '100%', title: '完全遮住背景' },
  { value: 'translucent', name: '半透明', note: '示例 60%', title: '透出清晰的背景' },
  { value: 'frosted', name: '磨砂玻璃', note: '示例 60%', title: '重度模糊并增强色彩的毛玻璃，带颗粒与高光；此模式下灵动岛不进入系统截屏' },
  { value: 'jelly', name: '果冻', note: '示例 60%', title: '通透饱满的果冻胶体：边缘更浓、顶部高光、形变带回弹' },
]
const tint = computed(() => {
  const clamp = (value: number) => Math.round(Math.max(0, Math.min(255, Number(value) || 0)))
  const { r, g, b } = props.backgroundColor
  const candy = isMuted(props.backgroundColor) ? { r: 220, g: 38, b: 82 } : props.backgroundColor
  const gel = gelPalette(candy)
  return {
    '--tile-tint': `${clamp(r)} ${clamp(g)} ${clamp(b)}`,
    '--tile-ink': isLight(props.backgroundColor) ? '#142030' : '#eff1f5',
    '--tile-gel': gel.gel,
    '--tile-lit': gel.lit,
    '--tile-deep': gel.deep,
    '--tile-glow': gel.glow,
    '--tile-gel-ink': isLight(candy) ? '#142030' : '#ffffff',
    '--tile-grain': FROST_GRAIN,
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
  grid-template-columns: repeat(2, minmax(0, 1fr));
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

.material-swatch { position: relative; display: grid; place-items: center; height: 74px; overflow: hidden; border-radius: 10px; isolation: isolate; }
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
  position: relative;
  display: flex;
  align-items: center;
  gap: 6px;
  width: 78%;
  height: 26px;
  padding: 0 10px;
  overflow: hidden;
  border-radius: 13px;
  box-shadow: inset 0 1px 0 rgb(255 255 255 / .22), inset 0 0 0 1px rgb(255 255 255 / .08), 0 4px 12px rgb(0 0 0 / .25);
}
.pill.m-solid { background: rgb(var(--tile-tint)); }
.pill.m-translucent { background: rgb(var(--tile-tint) / .6); }
/* Frosted: heavy blur with vibrancy, a diagonal specular sweep and grain. */
.pill.m-frosted {
  background:
    linear-gradient(125deg, rgb(255 255 255 / .2), transparent 48%),
    var(--tile-grain),
    rgb(var(--tile-tint) / .55);
  backdrop-filter: blur(9px) saturate(1.65);
  box-shadow: inset 0 1px 0 rgb(255 255 255 / .3), inset 0 0 0 1px rgb(255 255 255 / .12), inset 0 0 6px rgb(255 255 255 / .14), 0 4px 12px rgb(0 0 0 / .25);
}
/* Jelly: a lit centre deepening to a dense rim, a glossy cap, pooled light. */
.pill.m-jelly {
  background:
    radial-gradient(90% 60% at 50% 105%, rgb(var(--tile-glow) / .5), transparent 70%),
    radial-gradient(120% 140% at 50% 35%, rgb(var(--tile-lit) / .62) 30%, rgb(var(--tile-deep) / .96) 100%);
  box-shadow: inset 0 1.5px 0 rgb(255 255 255 / .62), inset 0 0 0 1px rgb(255 255 255 / .16), inset 0 0 9px rgb(var(--tile-deep) / .9), 0 6px 14px -4px rgb(var(--tile-gel) / .7);
}
.pill.m-jelly::before { content: ""; position: absolute; top: 2px; left: 9px; right: 9px; height: 8px; border-radius: 99px; background: linear-gradient(rgb(255 255 255 / .6), rgb(255 255 255 / 0) 90%); }
.pill i { position: relative; display: block; flex: none; height: 4px; border-radius: 2px; background: var(--tile-ink); }
.pill.m-jelly i { background: var(--tile-gel-ink); }
.pill .pill-dot { width: 7px; height: 7px; border-radius: 50%; background: #86b4f0; }
.pill.m-jelly .pill-dot { background: var(--tile-gel-ink); }
.pill .pill-line { flex: 1; opacity: .9; }
.pill .pill-time { width: 18%; opacity: .75; }
.material-tile:hover:not(:disabled) .pill.m-jelly { animation: wobble 620ms cubic-bezier(.3, 1.5, .5, 1); }
@keyframes wobble { 30% { transform: scale(1.06, .9); } 60% { transform: scale(.97, 1.04); } }
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

@media (max-width: 560px) { .material-swatch { height: 60px; } }
@media (max-width: 380px) { .segmented.material-gallery { grid-template-columns: minmax(0, 1fr); } }
@media (prefers-reduced-motion: reduce) {
  .segmented .material-tile, .check { transition: none; }
  .material-tile:hover:not(:disabled) .pill.m-jelly { animation: none; }
}
</style>
