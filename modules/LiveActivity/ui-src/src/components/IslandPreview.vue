<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import type { Anchor, RgbColor, SurfaceStyle } from '../types'

/**
 * A pure-geometry rendering of the island.
 *
 * This is the settings page's own drawing, not a window. It exists so a user
 * can see where the island will appear and how big it will be without a real
 * task running — which is the only way to evaluate a placement setting.
 *
 * It is deliberately decoupled from the live island state: the two share the
 * same numbers but not the same source, so opening this page cannot disturb a
 * running task and a running task cannot make the preview jump.
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
  mini?: boolean
  account?: string | null
  accountHeader?: string | null
}>(), { compactWidth: 232, offsetX: 0, offsetY: 0, mini: false, account: null, accountHeader: null, date: null, peekText: null, expandedText: null })
const stage = ref<HTMLElement | null>(null)
const stageSize = ref({ width: 360, height: 320 })
let observer: ResizeObserver | undefined
onMounted(() => {
  observer = new ResizeObserver(([entry]) => {
    stageSize.value = { width: entry.contentRect.width, height: entry.contentRect.height }
  })
  if (stage.value) observer.observe(stage.value)
})
onBeforeUnmount(() => observer?.disconnect())

/** Mirrors `IslandState::logical_size` in the module. */
const SIZES: Record<typeof props.state, { width: number; height: number }> = {
  compact: { width: 232, height: 32 },
  peek: { width: 300, height: 60 },
  expanded: { width: 340, height: 260 },
}

const box = computed(() => {
  const size = SIZES[props.state]
  const scale = Math.min(Math.max(props.scale, 0.75), 1.5)
  const logicalWidth = size.width + props.compactWidth - 232
  const logicalHeight = size.height + (props.accountHeader && !props.mini && props.state !== 'expanded' ? 24 : 0)
  const fitted = Math.max(0.1, Math.min(scale, (stageSize.value.width - 24) / logicalWidth, (stageSize.value.height - 36) / logicalHeight))
  return {
    width: Math.round(logicalWidth * fitted),
    height: Math.round(logicalHeight * fitted),
    scale: fitted,
    logicalWidth: Math.round(logicalWidth * scale),
    logicalHeight: Math.round(logicalHeight * scale),
  }
})

const placementStyle = computed(() => {
  const { width, height } = stageSize.value
  const roomX = Math.max(0, width - box.value.width - 24)
  const roomY = Math.max(0, height - box.value.height - 36)
  const fraction = props.anchor.endsWith('Left') ? 0 : props.anchor.endsWith('Right') ? 1 : 0.5
  const x = props.mini ? (width - box.value.width) / 2 : 12 + roomX * fraction + props.offsetX * width / 1920
  const y = props.mini ? (height - box.value.height) / 2 : 12 + (props.anchor.startsWith('top') ? 0 : roomY) + props.offsetY * height / 1080
  return {
    left: `${Math.max(0, Math.min(width - box.value.width, x))}px`,
    top: `${Math.max(0, Math.min(height - box.value.height - 24, y))}px`,
    '--preview-scale': box.value.scale,
  }
})

const materialStyle = computed(() => {
  const clamp = (value: number) => Math.round(Math.max(0, Math.min(255, Number(value) || 0)))
  const { r, g, b } = props.backgroundColor
  const light = r * 0.2126 + g * 0.7152 + b * 0.0722 > 150
  return {
    backgroundColor: `rgb(${clamp(r)} ${clamp(g)} ${clamp(b)} / ${props.surfaceStyle === 'solid' ? 1 : props.opacity})`,
    '--island-ink': light ? '#142030' : '#eff1f5',
    '--island-muted': light ? '#425466' : '#aab4bf',
    '--island-line': light ? '#14203020' : '#ffffff18',
  }
})
</script>

<template>
  <div ref="stage" class="stage" :class="{ mini }" :data-anchor="anchor">
    <div class="island" :class="`material-${surfaceStyle}`" :style="{ width: `${box.width}px`, height: `${box.height}px`, ...materialStyle, ...placementStyle }">
      <div class="island-bar">
        <i class="dot" />
        <span class="pill-text">{{ customText || (clock ? date || 'Qing Island' : account || accountHeader ? 'Codex' : peekText !== null || expandedText !== null ? 'Qing Island' : '模拟活动 · 运行中') }}</span>
        <span v-if="clock" class="clock">{{ clock }}</span>
      </div>
      <div v-if="accountHeader && !mini" class="island-account-header" :title="accountHeader">{{ accountHeader }}</div>
      <div v-if="state !== 'compact'" class="island-row">
        <span>{{ peekText ?? (customText || (clock ? date || '本地时间' : account || accountHeader ? '点击查看额度' : '模拟导出 · 60%')) }}</span>
      </div>
      <div v-if="state === 'expanded'" class="island-stack">
        <div v-if="expandedText !== null" class="expanded-caption">{{ expandedText }}</div>
        <template v-else-if="clock || customText">
          <div v-if="clock" class="large-clock">{{ clock }}</div>
          <div class="custom-caption">{{ customText }}</div>
        </template>
        <template v-else-if="!account && !accountHeader">
          <div class="item"><i class="dot warn" /><span>模拟导出</span><em>60%</em></div>
          <div class="item"><i class="dot ok" /><span>模拟完成</span><em>完成</em></div>
        </template>
      </div>
      <div v-if="state === 'expanded' && account" class="island-account">{{ account }}</div>
    </div>
    <p v-if="!mini" class="stage-note" title="逻辑像素；位置为示意，实际窗口按显示器工作区和 DPI 布局">
      {{ box.logicalWidth }} × {{ box.logicalHeight }} px
    </p>
  </div>
</template>

<style scoped>
.stage {
  position:relative;
  height:320px;
  overflow:hidden;
  border: 1px solid var(--q-border);
  border-radius: 14px;
  background: repeating-linear-gradient(135deg, transparent 0 12px, color-mix(in srgb, var(--q-brand) 26%, transparent) 12px 24px), radial-gradient(ellipse at 20% 40%, #ad8ce577, transparent 58%), radial-gradient(ellipse at 85% 70%, #58bdaa77, transparent 48%), var(--q-surface-soft);
}
.stage.mini { height:104px; border:0; border-radius:0; }
.island {
  position:absolute;
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: calc(7px * var(--preview-scale)) calc(10px * var(--preview-scale));
  overflow: hidden;
  max-width:100%;
  border: 1px solid var(--island-line);
  border-radius: 16px;
  background: var(--q-card);
  box-shadow: 0 10px 26px #0b1a2e1f;
  transition:
    left 180ms cubic-bezier(.2,.8,.2,1),
    top 180ms cubic-bezier(.2,.8,.2,1),
    background-color 180ms,
    width 140ms ease-out,
    height 140ms ease-out;
}
.island.material-frosted { backdrop-filter:blur(12px); }
.pill-text { flex:1; min-width:0; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
.clock { flex:none; font-variant-numeric:tabular-nums; }
.island-account-header { flex:none; height:calc(18px * var(--preview-scale)); line-height:calc(18px * var(--preview-scale)); font-size:calc(10px * var(--preview-scale)); color:var(--island-muted); overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
.large-clock { margin-top:12px; font-size:calc(32px * var(--preview-scale)); font-weight:700; color:var(--island-ink); font-variant-numeric:tabular-nums; }
.custom-caption { margin-top:12px; font-size:calc(13px * var(--preview-scale)); color:var(--island-muted); overflow-wrap:anywhere; }
.expanded-caption { margin-top:4px; font-size:calc(16px * var(--preview-scale)); line-height:1.5; color:var(--island-ink); white-space:pre-wrap; overflow-wrap:anywhere; }
.island-bar {
  display: flex;
  flex: 0 0 auto;
  gap: 8px;
  align-items: center;
  height: calc(18px * var(--preview-scale));
  font-size: calc(12px * var(--preview-scale));
  font-weight: 620;
  color: var(--island-ink);
}
.dot {
  flex: 0 0 8px;
  width: 8px;
  height: 8px;
  border-radius: 50%;
  background: var(--q-brand, #2e80d6);
}
.dot.warn {
  background: #e2a03f;
}
.dot.ok {
  background: var(--q-success, #2f9e6a);
}
.island-row {
  color: var(--island-muted);
  font-size: calc(11px * var(--preview-scale));
  white-space: nowrap;
  flex:none;
  overflow:hidden;
  text-overflow:ellipsis;
}
.island-stack {
  display: flex;
  flex:1;
  min-height:0;
  overflow:hidden;
  flex-direction: column;
  gap: 5px;
  padding-top: 4px;
  border-top: 1px solid var(--island-line);
}
.island-account { flex:none; white-space:pre-line; font-size:calc(10px * var(--preview-scale)); line-height:1.5; color:var(--island-muted); }
.item {
  display: flex;
  align-items: center;
  gap: 7px;
  font-size: 11px;
  color: var(--island-ink);
}
.item em {
  margin-left: auto;
  color: var(--island-muted);
  font-style: normal;
}
.item.more,
.item.meta {
  color: var(--island-muted);
}
.stage-note {
  position:absolute;
  bottom:8px;
  right:12px;
  margin: 0;
  color: var(--q-text-3);
  font-size: 11px;
}
@media(prefers-reduced-motion:reduce) { .island { transition:none; } }
</style>
