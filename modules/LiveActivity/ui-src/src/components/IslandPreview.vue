<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import type { Anchor, RgbColor, SurfaceStyle } from '../types'
import { FLUID, JELLY, frostedTint, isLight, jellyDensity, jellyPalette, liquidTint } from '../material'
import LiquidLens from './LiquidLens.vue'

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
 * same ink colours, same materials and motion. Keep the two in step.
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
  /** Frosted blur radius in logical pixels. */
  frostBlur?: number
}>(), { compactWidth: 232, offsetX: 0, offsetY: 0, date: null, peekText: null, expandedText: null, live: false, frostBlur: 6 })

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
/** The page lens is exactly the glass (see liquidMap for why). */
const LENS_MARGIN = 0

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

/*
 * Jelly: the axis that changes most leads and overshoots, the other squashes
 * against it (overlay::motion::jelly). Decided per change of shape.
 */
const jellyLead = ref<'width' | 'height'>('height')
watch(() => [box.value.width, box.value.height] as const, (next, previous) => {
  if (!previous) return
  const relative = (from: number, to: number) => Math.abs(to - from) / Math.max(from, to, 1)
  jellyLead.value = relative(previous[0], next[0]) >= relative(previous[1], next[1]) ? 'width' : 'height'
})

/*
 * Liquid glass takes its light from the pointer while it hovers the capsule
 * and bulges a little towards it, as the native window does.
 */
const pointer = ref<{ x: number; y: number } | null>(null)
function pointerMoved(event: PointerEvent): void {
  if (props.surfaceStyle !== 'liquid') return
  const rect = (event.currentTarget as HTMLElement).getBoundingClientRect()
  pointer.value = { x: event.clientX - rect.left, y: event.clientY - rect.top }
}
function pointerLeft(): void { pointer.value = null }
const lensId = `liquid-lens-${Math.random().toString(36).slice(2, 9)}`

const liquidLight = computed(() => {
  const { width, height } = box.value
  let [lx, ly] = [-0.55, -0.835]
  if (pointer.value) {
    const dx = pointer.value.x - width / 2
    const dy = pointer.value.y - height / 2
    const length = Math.hypot(dx, dy) || 1
    lx = 0.35 * lx + 0.65 * dx / length
    ly = 0.35 * ly + 0.65 * dy / length
    const norm = Math.hypot(lx, ly) || 1
    lx /= norm
    ly /= norm
  }
  // The rim gradient runs away from the light; CSS angles start at "up".
  const angle = Math.atan2(-lx, ly) * 180 / Math.PI
  const bulge = pointer.value
    ? { x: (pointer.value.x - width / 2) / (width / 2), y: (pointer.value.y - height / 2) / (height / 2) }
    : { x: 0, y: 0 }
  return { angle, bulge }
})

const islandTransform = computed(() => {
  const { x, y } = placement.value
  if (props.surfaceStyle !== 'liquid' || !pointer.value) return `translate3d(${x}px, ${y}px, 0)`
  // A gentle swell towards the pointer, not a uniform zoom.
  const { bulge } = liquidLight.value
  return `translate3d(${x + bulge.x * 1.2}px, ${y + bulge.y * 1.2}px, 0) scale(${1 + 0.012 * Math.abs(bulge.x)}, ${1 + 0.03 * Math.abs(bulge.y)})`
})

const islandStyle = computed(() => {
  const clamp = (value: number) => Math.round(Math.max(0, Math.min(255, Number(value) || 0)))
  const { r, g, b } = props.backgroundColor
  const style = props.surfaceStyle
  const liquid = style === 'liquid'
  const jelly = style === 'jelly'
  const glassTint = liquidTint(props.opacity)
  // Same luminance split and ink as the native renderer. Native liquid glass
  // reads the real desktop; this stage is dusk-dark, so clear glass takes light
  // ink unless a light tint is strong enough to carry dark ink.
  const light = liquid ? isLight(props.backgroundColor) && glassTint >= 0.15 : isLight(props.backgroundColor)
  const alpha = style === 'solid' ? 1 : liquid ? glassTint : style === 'frosted' ? frostedTint(props.opacity) : props.opacity
  const material: Record<string, string | number> = {}
  if (liquid) {
    Object.assign(material, {
      '--light-angle': `${liquidLight.value.angle.toFixed(1)}deg`,
      '--motion-ms': `${FLUID.ms}ms`,
      '--motion-w': FLUID.easing,
      '--motion-h': FLUID.easing,
    })
  }
  if (jelly) {
    const palette = jellyPalette(props.backgroundColor)
    Object.assign(material, {
      '--gel': palette.gel,
      '--gel-lit': palette.lit,
      '--gel-deep': palette.deep,
      '--gel-density': jellyDensity(props.opacity),
      '--motion-ms': `${JELLY.ms}ms`,
      '--motion-w': jellyLead.value === 'width' ? JELLY.primary : JELLY.secondary,
      '--motion-h': jellyLead.value === 'height' ? JELLY.primary : JELLY.secondary,
    })
  }
  if (liquid || jelly) {
    Object.assign(material, {
      '--island-muted': light ? '#26384a' : '#dadee4',
      '--island-accent': light ? '#142030' : '#eff1f5',
    })
  }
  return {
    width: `${box.value.width}px`,
    height: `${box.value.height}px`,
    borderRadius: `${box.value.radius}px`,
    transform: islandTransform.value,
    backgroundColor: jelly ? 'transparent' : `rgb(${clamp(r)} ${clamp(g)} ${clamp(b)} / ${alpha})`,
    '--s': box.value.scale,
    '--island-ink': light ? '#142030' : '#eff1f5',
    '--island-muted': light ? '#425466' : '#aab4bf',
    '--island-accent': light ? '#2265a9' : '#86b4f0',
    '--island-line': light ? 'rgb(20 32 48 / .16)' : 'rgb(239 241 245 / .14)',
    '--island-rim': light ? 'rgb(0 0 0 / .13)' : 'rgb(255 255 255 / .08)',
    '--island-rim-top': light ? 'rgb(0 0 0 / .09)' : 'rgb(255 255 255 / .24)',
    '--island-sheen': light ? 'none' : 'linear-gradient(rgb(255 255 255 / .05), transparent calc(24px * var(--s)))',
    '--radius': `${box.value.radius}px`,
    // renderer::legible folds the desktop's brightness to the ink's side.
    '--liquid-tone': light ? 'contrast(.86) brightness(1.12)' : 'contrast(.88) brightness(.88)',
    '--frost-blur': props.frostBlur,
    ...material,
  }
})

/*
 * The liquid lens: a layer exactly the glass's shape that follows the
 * capsule and carries the backdrop filter. Its own border-radius is the only
 * clip a browser applies to a filtered backdrop.
 */
const lensClipStyle = computed(() => ({
  width: `${box.value.width}px`,
  height: `${box.value.height}px`,
  borderRadius: `${box.value.radius}px`,
  transform: islandTransform.value,
  backdropFilter: `url(#${lensId}) saturate(1.15) ${islandStyle.value['--liquid-tone']}`,
  '--fluid-ms': `${FLUID.ms}ms`,
  '--fluid-ease': FLUID.easing,
}))

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
    <template v-if="surfaceStyle === 'liquid'">
      <LiquidLens :id="lensId" :width="box.width" :height="box.height" :radius="box.radius" :scale="box.scale" :margin="LENS_MARGIN" />
      <i class="lens-clip" :style="lensClipStyle" aria-hidden="true" />
    </template>
    <div class="island" :class="[`material-${surfaceStyle}`, `is-${state}`, { touched: pointer }]" :style="islandStyle" @pointermove="pointerMoved" @pointerleave="pointerLeft">
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
.island > * { position: relative; z-index: 1; }
.island > .island-rule, .island > .island-stack { position: absolute; }

/* Frosted (renderer::process): flat, hazy, soft. A ~24px blur with a little
   vibrancy and lift, the tint layer and one thin, even edge. No sheen, no
   gradient, no glow. */
.island.material-frosted {
  background-image: none;
  backdrop-filter: blur(calc(var(--frost-blur, 6) * 1px * var(--s))) saturate(1.2) brightness(1.04);
  box-shadow: inset 0 0 0 1px var(--island-rim-top), 0 2px 8px rgb(0 0 0 / .14);
}

/* Liquid glass (renderer::compose, Liquid): clear, curved glass. The lens
   layer behind refracts the stage outward at the rim (LiquidLens); here the
   capsule adds a whisper of tint, the specular rim facing the light (the
   pointer while it hovers), a soft band inside the lit edge, shade on the far
   side, and the fluid motion. */
.lens-clip {
  position: absolute;
  left: 0;
  top: 0;
  z-index: 2;
  pointer-events: none;
  transition:
    transform 260ms cubic-bezier(.2, .8, .2, 1),
    width var(--fluid-ms, 420ms) var(--fluid-ease, ease),
    height var(--fluid-ms, 420ms) var(--fluid-ease, ease),
    border-radius var(--fluid-ms, 420ms) var(--fluid-ease, ease);
}

.island.material-liquid {
  background-image: none;
  box-shadow:
    inset calc(2px * var(--s)) calc(2px * var(--s)) calc(6px * var(--s)) calc(-2px * var(--s)) rgb(255 255 255 / .3),
    inset calc(-3px * var(--s)) calc(-3px * var(--s)) calc(8px * var(--s)) calc(-3px * var(--s)) rgb(0 0 0 / .22),
    0 calc(6px * var(--s)) calc(18px * var(--s)) calc(-6px * var(--s)) rgb(0 0 0 / .32);
  text-shadow: 0 1px 1px rgb(0 0 0 / .28);
  transition:
    transform 260ms cubic-bezier(.2, .8, .2, 1),
    width var(--motion-ms) var(--motion-w),
    height var(--motion-ms) var(--motion-h),
    border-radius var(--motion-ms) var(--motion-h),
    background-color 200ms;
}
.island.material-liquid::after {
  content: "";
  position: absolute;
  inset: 0;
  z-index: 2;
  padding: 1.4px;
  border-radius: inherit;
  background: linear-gradient(var(--light-angle, 146deg), rgb(255 255 255 / .95), rgb(255 255 255 / .3) 24%, rgb(255 255 255 / .06) 55%, rgb(255 255 255 / .42) 100%);
  -webkit-mask: linear-gradient(#000 0 0) content-box, linear-gradient(#000 0 0);
  -webkit-mask-composite: xor;
  mask: linear-gradient(#000 0 0) content-box exclude, linear-gradient(#000 0 0);
  pointer-events: none;
}

/* Jelly (renderer::compose, Jelly): a soft coloured body, not glass. Its own
   colour leads, deepening into a thick, soft rim; light scattered inside
   glows below the centre; a wide soft highlight sits on the upper body; and
   it moves with mass — overshoot, settle, squash and stretch. */
.island.material-jelly {
  background-image:
    radial-gradient(42% 45% at 50% 62%, rgb(var(--gel-lit) / .55), transparent 100%),
    linear-gradient(rgb(var(--gel) / var(--gel-density)), rgb(var(--gel) / var(--gel-density)));
  box-shadow:
    inset 0 0 calc(12px * var(--s)) calc(2px * var(--s)) rgb(var(--gel-deep) / .9),
    inset 0 calc(2px * var(--s)) calc(3px * var(--s)) rgb(255 255 255 / .35),
    0 calc(8px * var(--s)) calc(20px * var(--s)) calc(-8px * var(--s)) rgb(var(--gel-deep) / .7);
  text-shadow: 0 1px 1px rgb(0 0 0 / .28);
  transition:
    transform 300ms cubic-bezier(.2, .8, .2, 1),
    width var(--motion-ms) var(--motion-w),
    height var(--motion-ms) var(--motion-h),
    border-radius var(--motion-ms) var(--motion-h);
}
.island.material-jelly::before {
  content: "";
  position: absolute;
  left: 8%;
  right: 8%;
  top: 4%;
  height: 40%;
  z-index: 0;
  border-radius: 50%;
  background: radial-gradient(closest-side, rgb(255 255 255 / .42), rgb(255 255 255 / .12) 70%, transparent);
  pointer-events: none;
}
.stage:hover .island.material-jelly { animation: jelly-squish 640ms; }
@keyframes jelly-squish {
  0% { scale: 1 1; }
  18% { scale: 1.07 .9; }
  40% { scale: .97 1.05; }
  62% { scale: 1.02 .98; }
  100% { scale: 1 1; }
}

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
  .island, .island.material-liquid, .island.material-jelly, .lens-clip, .island-row, .island-rule, .island-stack { transition: none; animation: none; }
  .stage:hover .island.material-jelly { animation: none; }
}
</style>
