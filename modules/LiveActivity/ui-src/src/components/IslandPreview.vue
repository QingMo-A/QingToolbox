<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import type { Anchor, RgbColor, SurfaceStyle } from '../types'

/**
 * A pure-geometry rendering of the island on a miniature desktop.
 *
 * This is the settings page's own drawing, not a window. It exists so a user
 * can see where the island will appear and how big it will be without a real
 * task running — which is the only way to evaluate a placement setting.
 *
 * It is deliberately decoupled from the live island state: the two share the
 * same numbers but not the same source, so opening this page cannot disturb a
 * running task and a running task cannot make the preview jump.
 *
 * The capsule mirrors `renderer::compose` in the module: same logical layout,
 * same ink colours, same rim. Keep the two in step.
 */
const props = withDefaults(defineProps<{
  anchor: Anchor
  scale: number
  state: 'compact' | 'peek' | 'expanded'
  surfaceStyle: SurfaceStyle
  opacity: number
  backgroundColor: RgbColor
  clock: string | null
  date?: string | null
  customText: string
  peekText?: string | null
  expandedText?: string | null
  compactWidth?: number
  offsetX?: number
  offsetY?: number
  /** The real island is showing a desktop preview right now. */
  live?: boolean
}>(), { compactWidth: 232, offsetX: 0, offsetY: 0, date: null, peekText: null, expandedText: null, live: false })

const stage = ref<HTMLElement | null>(null)
const stageSize = ref({ width: 640, height: 320 })
/** The wallpaper drifts only while the stage is on screen. */
const onScreen = ref(true)
let resize: ResizeObserver | undefined
let visibility: IntersectionObserver | undefined
onMounted(() => {
  resize = new ResizeObserver(([entry]) => {
    stageSize.value = { width: entry.contentRect.width, height: entry.contentRect.height }
  })
  if (stage.value) resize.observe(stage.value)
  if (stage.value && 'IntersectionObserver' in window) {
    visibility = new IntersectionObserver(([entry]) => { onScreen.value = entry.isIntersecting })
    visibility.observe(stage.value)
  }
})
onBeforeUnmount(() => {
  resize?.disconnect()
  visibility?.disconnect()
})

/** Mirrors `IslandState::logical_size` in the module. */
const SIZES: Record<typeof props.state, { width: number; height: number }> = {
  compact: { width: 232, height: 32 },
  peek: { width: 300, height: 60 },
  expanded: { width: 340, height: 260 },
}
/** The miniature taskbar; the island docks to the work area above it. */
const TASKBAR = 34
const MARGIN = 14

const box = computed(() => {
  const size = SIZES[props.state]
  const scale = Math.min(Math.max(props.scale, 0.75), 1.5)
  const logicalWidth = size.width + props.compactWidth - 232
  const logicalHeight = size.height
  const roomWidth = stageSize.value.width - MARGIN * 2
  const roomHeight = stageSize.value.height - TASKBAR - MARGIN * 2
  const fitted = Math.max(0.1, Math.min(scale, roomWidth / logicalWidth, roomHeight / logicalHeight))
  const width = Math.round(logicalWidth * fitted)
  const height = Math.round(logicalHeight * fitted)
  return {
    width,
    height,
    scale: fitted,
    // `renderer::island_radius`: a pill while compact, a card once grown.
    radius: Math.min(22 * fitted, height / 2),
    logicalWidth: Math.round(logicalWidth * scale),
    logicalHeight: Math.round(logicalHeight * scale),
  }
})

const placement = computed(() => {
  const { width, height } = stageSize.value
  const work = height - TASKBAR
  const roomX = Math.max(0, width - box.value.width - MARGIN * 2)
  const roomY = Math.max(0, work - box.value.height - MARGIN * 2)
  const fraction = props.anchor.endsWith('Left') ? 0 : props.anchor.endsWith('Right') ? 1 : 0.5
  const x = MARGIN + roomX * fraction + props.offsetX * width / 1920
  const y = MARGIN + (props.anchor.startsWith('top') ? 0 : roomY) + props.offsetY * work / 1080
  return {
    x: Math.round(Math.max(0, Math.min(width - box.value.width, x))),
    y: Math.round(Math.max(0, Math.min(work - box.value.height, y))),
  }
})

const islandStyle = computed(() => {
  const clamp = (value: number) => Math.round(Math.max(0, Math.min(255, Number(value) || 0)))
  const { r, g, b } = props.backgroundColor
  // Same luminance split and ink as the native renderer.
  const light = r * 0.2126 + g * 0.7152 + b * 0.0722 > 150
  return {
    width: `${box.value.width}px`,
    height: `${box.value.height}px`,
    borderRadius: `${box.value.radius}px`,
    transform: `translate3d(${placement.value.x}px, ${placement.value.y}px, 0)`,
    backgroundColor: `rgb(${clamp(r)} ${clamp(g)} ${clamp(b)} / ${props.surfaceStyle === 'solid' ? 1 : props.opacity})`,
    '--s': box.value.scale,
    '--island-ink': light ? '#142030' : '#eff1f5',
    '--island-muted': light ? '#425466' : '#aab4bf',
    '--island-accent': light ? '#2265a9' : '#86b4f0',
    '--island-line': light ? 'rgb(20 32 48 / .16)' : 'rgb(239 241 245 / .14)',
    '--island-rim': light ? 'rgb(0 0 0 / .13)' : 'rgb(255 255 255 / .08)',
    '--island-rim-top': light ? 'rgb(0 0 0 / .09)' : 'rgb(255 255 255 / .24)',
    '--island-sheen': light ? 'none' : 'linear-gradient(rgb(255 255 255 / .05), transparent calc(24px * var(--s)))',
  }
})

/** The native expanded card sets its first line as the headline. */
const expandedParts = computed(() => {
  const text = props.expandedText ?? ''
  const at = text.indexOf('\n')
  return at < 0 ? { lead: text, rest: '' } : { lead: text.slice(0, at), rest: text.slice(at + 1) }
})
</script>

<template>
  <div ref="stage" class="stage" :class="[`state-${state}`, { live, paused: !onScreen }]" :data-anchor="anchor">
    <div class="wall" aria-hidden="true"><i class="blob blob-a" /><i class="blob blob-b" /><i class="blob blob-c" /></div>
    <div class="desk" aria-hidden="true">
      <div class="app app-a"><b /><i /><i /><i /><i /></div>
      <div class="app app-b"><b /><i /><i /><i /></div>
    </div>
    <div class="island" :class="[`material-${surfaceStyle}`, `is-${state}`]" :style="islandStyle">
      <div class="island-bar">
        <svg class="orb" viewBox="0 0 14 14" aria-hidden="true"><circle cx="7" cy="7" r="5.7" /><path d="M7 7V4M7 7l2.2 1.1" /></svg>
        <span class="pill-text">{{ customText || date || 'Qing Island' }}</span>
        <span v-if="clock" class="clock">{{ clock }}</span>
      </div>
      <div v-if="state === 'peek' && peekText" class="island-row">
        <span>{{ peekText }}</span>
      </div>
      <template v-if="state === 'expanded'">
        <i class="island-rule" aria-hidden="true" />
        <div v-if="expandedText" class="island-stack">
          <div class="expanded-caption"><strong>{{ expandedParts.lead }}</strong><span v-if="expandedParts.rest">{{ expandedParts.rest }}</span></div>
        </div>
      </template>
    </div>
    <div class="taskbar" aria-hidden="true"><i /><i /><i /><i /><i /></div>
    <span v-if="live" class="live-badge"><i />桌面预览中</span>
    <p class="stage-note" title="逻辑像素；位置为示意，实际窗口按显示器工作区和 DPI 布局">
      {{ box.logicalWidth }} × {{ box.logicalHeight }} px
    </p>
  </div>
</template>

<style scoped>
.stage {
  --wall-a: color-mix(in srgb, var(--q-brand) 78%, #5b8cff);
  --wall-b: color-mix(in srgb, var(--q-accent, #8b5cf6) 70%, #c084fc);
  --wall-c: #f6a35c;
  --wall-base: linear-gradient(155deg, #0e1a33 0%, #1b2b52 46%, #2b1f4d 100%);
  position: relative;
  height: 320px;
  overflow: hidden;
  isolation: isolate;
  border-radius: 16px;
  background: var(--wall-base);
  box-shadow: inset 0 0 0 1px rgb(255 255 255 / .06), 0 18px 40px -24px rgb(8 16 32 / .55);
}
/* Wallpaper: three soft light pools that drift slowly. One composited layer,
   transform only; it stops while the stage is scrolled away. */
.wall { position: absolute; inset: -18%; z-index: -2; animation: drift 38s ease-in-out infinite alternate; will-change: transform; }
.stage.paused .wall { animation-play-state: paused; }
.blob { position: absolute; border-radius: 50%; }
.blob-a { left: 6%; top: 4%; width: 52%; height: 70%; background: radial-gradient(closest-side, var(--wall-a), transparent); }
.blob-b { right: 2%; bottom: 0; width: 56%; height: 74%; background: radial-gradient(closest-side, var(--wall-b), transparent); }
.blob-c { left: 44%; top: 0; width: 30%; height: 44%; background: radial-gradient(closest-side, color-mix(in srgb, var(--wall-c) 70%, transparent), transparent); }
@keyframes drift {
  from { transform: translate3d(-3%, -2%, 0) rotate(-4deg) scale(1.02); }
  to { transform: translate3d(3%, 2%, 0) rotate(4deg) scale(1.08); }
}

/* Two app windows sit behind the island so every material has something
   real to be translucent or frosted over. */
.desk { position: absolute; inset: 0; z-index: -1; }
.app { position: absolute; display: flex; flex-direction: column; gap: 9px; padding: 30px 16px 14px; overflow: hidden; border-radius: 10px; box-shadow: 0 16px 30px -18px rgb(0 0 0 / .6); }
.app b { position: absolute; inset: 0 0 auto; height: 22px; }
.app b::before { content: ""; position: absolute; left: 10px; top: 8px; width: 6px; height: 6px; border-radius: 50%; background: #ff6b6b; box-shadow: 10px 0 #ffc24b, 20px 0 #3ddc84; }
.app i { display: block; height: 8px; border-radius: 4px; }
.app-a { left: 16%; top: 4%; width: 40%; height: 60%; background: rgb(246 248 252 / .92); }
.app-a b { background: rgb(226 232 242 / .95); }
.app-a i { width: 72%; background: linear-gradient(90deg, #4f8cff, #8fb4ff); }
.app-a i:nth-of-type(2) { width: 88%; background: linear-gradient(90deg, #f472b6, #fda4af); }
.app-a i:nth-of-type(3) { width: 54%; background: linear-gradient(90deg, #34d399, #a7f3d0); }
.app-a i:nth-of-type(4) { width: 80%; height: 40px; border-radius: 6px; background: linear-gradient(120deg, #fbbf24, #f97316 60%, #ef4444); }
.app-b { left: 47%; top: 16%; width: 38%; height: 56%; background: rgb(17 22 33 / .94); }
.app-b b { background: rgb(30 37 52 / .96); }
.app-b i { width: 84%; opacity: .85; background: linear-gradient(90deg, #22d3ee 0 22%, transparent 22% 28%, #a78bfa 28% 60%, transparent 60%); }
.app-b i:nth-of-type(2) { width: 62%; background: linear-gradient(90deg, #f472b6 0 30%, transparent 30% 36%, #facc15 36%); }
.app-b i:nth-of-type(3) { width: 74%; background: linear-gradient(90deg, #4ade80 0 44%, transparent 44% 50%, #60a5fa 50%); }

.taskbar { position: absolute; inset: auto 0 0; z-index: 1; display: flex; align-items: center; justify-content: center; gap: 9px; height: 34px; background: rgb(14 18 28 / .62); box-shadow: inset 0 1px 0 rgb(255 255 255 / .08); }
.taskbar i { width: 18px; height: 18px; border-radius: 5px; background: linear-gradient(135deg, #60a5fa, #2563eb); opacity: .92; }
.taskbar i:nth-child(2) { background: linear-gradient(135deg, #fbbf24, #f97316); }
.taskbar i:nth-child(3) { position: relative; background: linear-gradient(135deg, #a78bfa, #7c3aed); }
.taskbar i:nth-child(3)::after { content: ""; position: absolute; left: 5px; right: 5px; bottom: -6px; height: 2px; border-radius: 1px; background: #c4b5fd; }
.taskbar i:nth-child(4) { background: linear-gradient(135deg, #34d399, #059669); }
.taskbar i:nth-child(5) { background: linear-gradient(135deg, #f472b6, #db2777); }

/* ---- the capsule: logical layout of renderer::compose ---- */
.island {
  position: absolute;
  left: 0;
  top: 0;
  z-index: 2;
  overflow: hidden;
  contain: layout paint;
  color: var(--island-ink);
  font-family: "Segoe UI", "Microsoft YaHei UI", sans-serif;
  background-image: var(--island-sheen);
  box-shadow: inset 0 1px 0 var(--island-rim-top), inset 0 0 0 1px var(--island-rim), 0 2px 6px rgb(0 0 0 / .14);
  transition:
    transform 300ms cubic-bezier(.2, .8, .2, 1),
    width 240ms cubic-bezier(.2, .8, .2, 1),
    height 240ms cubic-bezier(.2, .8, .2, 1),
    border-radius 240ms cubic-bezier(.2, .8, .2, 1),
    background-color 200ms;
}
.island.material-frosted { backdrop-filter: blur(14px) saturate(1.45); }
.island-bar {
  display: flex;
  align-items: center;
  gap: calc(5px * var(--s));
  height: calc(32px * var(--s));
  padding: 0 calc(14px * var(--s));
  font-size: calc(12px * var(--s));
  font-weight: 600;
  white-space: nowrap;
}
.orb { flex: none; width: calc(14px * var(--s)); height: calc(14px * var(--s)); fill: none; stroke: var(--island-accent); stroke-width: 1.35; stroke-linecap: round; }
.pill-text { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; }
.clock { flex: none; margin-left: calc(3px * var(--s)); font-variant-numeric: tabular-nums; }
.island-row {
  height: calc(22px * var(--s));
  margin-top: calc(-2px * var(--s));
  padding: 0 calc(16px * var(--s)) 0 calc(33px * var(--s));
  overflow: hidden;
  color: var(--island-muted);
  font-size: calc(11px * var(--s));
  line-height: calc(22px * var(--s));
  white-space: nowrap;
  text-overflow: ellipsis;
  animation: content-in 260ms 70ms cubic-bezier(.2, .8, .2, 1) both;
}
.island-rule {
  position: absolute;
  top: calc(40px * var(--s));
  left: calc(16px * var(--s));
  right: calc(16px * var(--s));
  height: 1px;
  background: linear-gradient(90deg, transparent, var(--island-line) 28px, var(--island-line) calc(100% - 28px), transparent);
  animation: content-in 260ms 60ms both;
}
.island-stack {
  position: absolute;
  inset: calc(41px * var(--s)) calc(22px * var(--s)) calc(14px * var(--s));
  display: flex;
  flex-direction: column;
  justify-content: center;
  overflow: hidden;
  animation: content-in 300ms 90ms cubic-bezier(.2, .8, .2, 1) both;
}
.expanded-caption { overflow-wrap: anywhere; }
.expanded-caption strong { display: block; font-size: calc(20px * var(--s)); font-weight: 600; line-height: 1.32; color: var(--island-ink); }
.expanded-caption span { display: block; margin-top: calc(6px * var(--s)); color: var(--island-muted); font-size: calc(13px * var(--s)); line-height: 1.45; white-space: pre-wrap; }
@keyframes content-in { from { opacity: 0; transform: translateY(4px); } }

.live-badge {
  position: absolute;
  left: 12px;
  bottom: 44px;
  z-index: 3;
  display: inline-flex;
  align-items: center;
  gap: 7px;
  padding: 4px 10px 4px 8px;
  border-radius: 99px;
  color: #fff;
  background: rgb(220 38 38 / .82);
  font-size: 11px;
  font-weight: 650;
  letter-spacing: .02em;
}
.live-badge i { position: relative; width: 7px; height: 7px; border-radius: 50%; background: #fff; }
.live-badge i::after { content: ""; position: absolute; inset: -3px; border: 1.5px solid #fff; border-radius: 50%; animation: ping 1.6s cubic-bezier(0, 0, .2, 1) infinite; }
@keyframes ping { 75%, to { opacity: 0; transform: scale(2); } }
.stage-note {
  position: absolute;
  right: 10px;
  bottom: 42px;
  z-index: 3;
  margin: 0;
  padding: 3px 8px;
  border-radius: 99px;
  color: rgb(255 255 255 / .86);
  background: rgb(10 14 24 / .42);
  font: 600 10.5px/1.4 ui-monospace, "Cascadia Mono", Consolas, monospace;
  letter-spacing: .02em;
}

/* ---- skins: the desktop follows the toolbox; the capsule never does ---- */
:root[data-theme='light'][data-appearance-preset='qing-default'] .stage {
  --wall-base: linear-gradient(155deg, #cfe0ff 0%, #8fb6f2 44%, #6b78d6 100%);
  --wall-c: #ffd08a;
}
:root[data-appearance-preset='aurora-flow'] .stage {
  --wall-a: #00f0ff;
  --wall-b: #ff2bd6;
  --wall-c: #fcee0a;
  --wall-base: linear-gradient(180deg, #07060f 0%, #120a2a 60%, #1d0b30 100%);
  border-radius: 4px;
  box-shadow: inset 0 0 0 1px rgb(0 240 255 / .35), 0 0 30px -10px rgb(0 240 255 / .35);
}
:root[data-appearance-preset='aurora-flow'] .wall::after {
  content: "";
  position: absolute;
  inset: 40% -20% -40%;
  background: linear-gradient(rgb(0 240 255 / .28) 1px, transparent 1px) 0 0 / 100% 26px, linear-gradient(90deg, rgb(255 43 214 / .26) 1px, transparent 1px) 0 0 / 26px 100%;
  transform: perspective(300px) rotateX(58deg);
  transform-origin: top;
}
:root[data-appearance-preset='qing-nova'] .stage {
  --wall-base: linear-gradient(90deg, #ffe14d 0 34%, #111 34% calc(34% + 4px), #f6f1e7 calc(34% + 4px) 70%, #111 70% calc(70% + 4px), #2f6bff calc(70% + 4px));
  border: 3px solid #111;
  border-radius: 0;
  box-shadow: 6px 6px 0 #111;
}
:root[data-appearance-preset='qing-nova'] .wall { display: none; }
:root[data-appearance-preset='qing-nova'] .app { border: 3px solid #111; border-radius: 0; box-shadow: 5px 5px 0 #111; }
:root[data-appearance-preset='qing-nova'] .taskbar { background: #111; }
:root[data-appearance-preset='qing-nova'] .taskbar i { border-radius: 0; }
:root[data-appearance-preset='neon-circuit'] .stage {
  --wall-a: rgb(212 175 55 / .55);
  --wall-b: rgb(120 90 30 / .5);
  --wall-c: #f3d98b;
  --wall-base: var(--deco-art-sunburst) center bottom / cover no-repeat, linear-gradient(180deg, #0c0b10, #17130b);
  border-radius: 3px;
  box-shadow: inset 0 0 0 1px rgb(212 175 55 / .45), 0 18px 40px -26px rgb(212 175 55 / .4);
}
:root[data-appearance-preset='neon-circuit'] .app { border-radius: 2px; outline: 1px solid rgb(212 175 55 / .35); outline-offset: -5px; }
:root[data-appearance-preset='greenline'] .stage {
  --wall-a: rgb(84 140 120 / .5);
  --wall-b: rgb(46 92 110 / .45);
  --wall-c: rgb(190 72 52 / .5);
  --wall-base: var(--ss-art-mountains) center bottom / cover no-repeat, linear-gradient(180deg, #f3ecdc, #e6dcc6);
  border-radius: 6px;
  box-shadow: inset 0 0 0 1px rgb(34 33 30 / .18), 0 18px 40px -28px rgb(34 33 30 / .5);
}
:root[data-appearance-preset='greenline'] .taskbar { background: rgb(34 33 30 / .72); }

@media (max-width: 640px) { .stage { height: 260px; } }
@media (prefers-reduced-motion: reduce) {
  .wall, .live-badge i::after { animation: none; }
  .island, .island-row, .island-rule, .island-stack { transition: none; animation: none; }
}
</style>
