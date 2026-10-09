<script setup lang="ts">
import { computed } from 'vue'
import type { RgbColor, SurfaceStyle } from '../types'
import { isLight, isMuted, jellyPalette } from '../material'
import LiquidLens from './LiquidLens.vue'

const props = defineProps<{ selected: SurfaceStyle; backgroundColor: RgbColor; disabled: boolean }>()
defineEmits<{ select: [style: SurfaceStyle] }>()

/**
 * The five materials as swatches over the same busy scene. The tiles carry no
 * text inside the sample, so each button's accessible text is just its name.
 * Frosted scatters, liquid glass refracts, jelly is a soft coloured body:
 * three different things, shown side by side.
 */
const styles: { value: SurfaceStyle; name: string; note: string; title: string }[] = [
  { value: 'solid', name: '实色', note: '100%', title: '完全遮住背景' },
  { value: 'translucent', name: '半透明', note: '示例 60%', title: '透出清晰的背景' },
  { value: 'frosted', name: '磨砂玻璃', note: '平·雾·柔', title: '平整的毛玻璃板：背景被模糊散射，能感知但看不清；细而均匀的边缘，无高光、无形变。此模式下灵动岛不进入系统截屏' },
  { value: 'liquid', name: '液态玻璃', note: '透·折·厚·流', title: '曲面透明玻璃：边缘折射背后的内容，左上亮、右下暗，高光跟随指针，悬停时向指针方向微微鼓起，形变如液体流动。此模式下灵动岛不进入系统截屏' },
  { value: 'jelly', name: '果冻', note: '软·弹', title: '半透明的软胶实体：内部颜色明显、边缘圆润肥厚、高光宽而柔和；形变带惯性回弹与挤压拉伸' },
]
/** The sample pill's fixed size, so its lens map is computed once. */
const PILL = { width: 132, height: 28, radius: 14 }
const LENS_MARGIN = 0
const lensId = `tile-lens-${Math.random().toString(36).slice(2, 9)}`
const tint = computed(() => {
  const clamp = (value: number) => Math.round(Math.max(0, Math.min(255, Number(value) || 0)))
  const { r, g, b } = props.backgroundColor
  // A grey gel sells nothing: the jelly sample falls back to a candy colour.
  const candy = isMuted(props.backgroundColor) ? { r: 40, g: 120, b: 240 } : props.backgroundColor
  const gel = jellyPalette(candy)
  return {
    '--tile-tint': `${clamp(r)} ${clamp(g)} ${clamp(b)}`,
    '--tile-ink': isLight(props.backgroundColor) ? '#142030' : '#eff1f5',
    '--tile-gel': gel.gel,
    '--tile-gel-lit': gel.lit,
    '--tile-gel-deep': gel.deep,
    '--tile-gel-ink': isLight(candy) ? '#142030' : '#ffffff',
    '--tile-lens': `url(#${lensId})`,
    '--lens-margin': `${LENS_MARGIN}px`,
  }
})
</script>

<template>
  <div class="segmented material-gallery" role="group" aria-label="面板材质" :style="tint">
    <LiquidLens :id="lensId" :width="PILL.width" :height="PILL.height" :radius="PILL.radius" :scale="1" :margin="LENS_MARGIN" />
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
        <i v-if="item.value === 'liquid'" class="lens-clip" />
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
  grid-template-columns: repeat(auto-fill, minmax(148px, 1fr));
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
.material-tile::after { content: attr(data-note); position: absolute; right: 12px; bottom: 8px; color: var(--q-text-3); font-size: 10.5px; font-weight: 600; letter-spacing: .04em; }

.material-swatch { position: relative; display: grid; place-items: center; height: 72px; overflow: hidden; border-radius: 10px; isolation: isolate; }
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
  width: 132px;
  max-width: calc(100% - 12px);
  height: 28px;
  padding: 0 10px;
  overflow: hidden;
  border-radius: 14px;
  box-shadow: inset 0 1px 0 rgb(255 255 255 / .22), inset 0 0 0 1px rgb(255 255 255 / .08), 0 4px 12px rgb(0 0 0 / .25);
}
.pill.m-solid { background: rgb(var(--tile-tint)); }
.pill.m-translucent { background: rgb(var(--tile-tint) / .6); }
/* Frosted: flat, hazy, soft — blur, a little lift, tint, one thin edge. */
.pill.m-frosted {
  background: rgb(var(--tile-tint) / .3);
  backdrop-filter: blur(5px) saturate(1.2) brightness(1.04);
  box-shadow: inset 0 0 0 1px rgb(255 255 255 / .16), 0 4px 12px rgb(0 0 0 / .2);
}
/* Liquid: the lens layer refracts the scene; the pill adds light and depth. */
/* The lens is exactly the pill: its own radius clips the filtered backdrop. */
.lens-clip {
  position: absolute;
  left: 50%;
  top: 50%;
  width: 132px;
  height: 28px;
  border-radius: 14px;
  transform: translate(-50%, -50%);
  backdrop-filter: var(--tile-lens) saturate(1.15) contrast(.85) brightness(.85);
}
.pill.m-liquid {
  background: transparent;
  box-shadow: inset 2px 2px 5px -2px rgb(255 255 255 / .4), inset -2px -2px 6px -3px rgb(0 0 0 / .3), 0 5px 14px -4px rgb(0 0 0 / .4);
}
.pill.m-liquid::after {
  content: "";
  position: absolute;
  inset: 0;
  padding: 1.3px;
  border-radius: inherit;
  background: linear-gradient(146deg, rgb(255 255 255 / .95), rgb(255 255 255 / .28) 26%, rgb(255 255 255 / .06) 55%, rgb(255 255 255 / .42));
  -webkit-mask: linear-gradient(#000 0 0) content-box, linear-gradient(#000 0 0);
  -webkit-mask-composite: xor;
  mask: linear-gradient(#000 0 0) content-box exclude, linear-gradient(#000 0 0);
}
/* Jelly: a soft coloured body with a wide soft highlight and inner glow. */
.pill.m-jelly {
  background:
    radial-gradient(45% 50% at 50% 70%, rgb(var(--tile-gel-lit) / .55), transparent),
    rgb(var(--tile-gel) / .8);
  box-shadow: inset 0 0 9px 1px rgb(var(--tile-gel-deep) / .9), inset 0 2px 3px rgb(255 255 255 / .35), 0 6px 14px -4px rgb(var(--tile-gel-deep) / .8);
}
.pill.m-jelly::before { content: ""; position: absolute; left: 8%; right: 8%; top: 2px; height: 46%; border-radius: 50%; background: radial-gradient(closest-side, rgb(255 255 255 / .5), rgb(255 255 255 / .12) 70%, transparent); }
.pill i { position: relative; display: block; flex: none; height: 4px; border-radius: 2px; background: var(--tile-ink); }
.pill.m-liquid i { background: #f4f6fa; box-shadow: 0 1px 1px rgb(0 0 0 / .3); }
.pill.m-jelly i { background: var(--tile-gel-ink); }
.pill .pill-dot { width: 7px; height: 7px; border-radius: 50%; background: #86b4f0; }
.pill.m-liquid .pill-dot, .pill.m-jelly .pill-dot { background: currentColor; color: #f4f6fa; }
.pill .pill-line { flex: 1; opacity: .9; }
.pill .pill-time { width: 18%; opacity: .75; }
/* Each material moves as itself: jelly squashes and rebounds; liquid swells. */
.material-tile:hover:not(:disabled) .pill.m-jelly { animation: jelly-squish 640ms; }
.material-tile:hover:not(:disabled) .pill.m-liquid { scale: 1.02 1.06; transition: scale 420ms cubic-bezier(.3, 1.2, .4, 1); }
.pill.m-liquid { transition: scale 420ms cubic-bezier(.3, 1.2, .4, 1); }
@keyframes jelly-squish { 18% { scale: 1.07 .88; } 40% { scale: .96 1.06; } 62% { scale: 1.02 .98; } }
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

@media (max-width: 560px) { .material-swatch { height: 60px; } .segmented.material-gallery { grid-template-columns: repeat(2, minmax(0, 1fr)); } }
@media (prefers-reduced-motion: reduce) {
  .segmented .material-tile, .check, .pill.m-liquid { transition: none; }
  .material-tile:hover:not(:disabled) .pill.m-jelly { animation: none; }
}
</style>
