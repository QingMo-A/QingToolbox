<script setup lang="ts">
import { computed } from 'vue'
import type { ActivityState, LiveActivity } from '../types'

const props = defineProps<{ activity: LiveActivity }>()

const STATE_LABEL: Record<ActivityState, string> = {
  running: '运行中',
  waiting: '需要你处理',
  paused: '已暂停',
  success: '已完成',
  failed: '失败',
  idle: '空闲',
  cancelled: '已取消',
  unknown: '未知',
}

const label = computed(() => STATE_LABEL[props.activity.state])

const fraction = computed(() => {
  const progress = props.activity.progress
  if (!progress || progress.total === null) return null
  if (!(progress.total > 0)) return null
  return Math.min(Math.max(progress.value / progress.total, 0), 1)
})

/** The provider's own status token, when it supplied one. */
const status = computed(() => props.activity.details.status ?? null)
</script>

<template>
  <li class="activity" :class="activity.state">
    <i class="mark" />
    <div class="body">
      <span class="title">{{ activity.title }}</span>
      <span class="sub">
        {{ label }}<template v-if="status"> · {{ status }}</template>
      </span>
    </div>
    <div v-if="fraction !== null" class="meter">
      <span class="percent">{{ Math.round(fraction * 100) }}%</span>
      <div class="bar"><span :style="{ transform: `scaleX(${fraction})` }" /></div>
    </div>
    <span v-else class="state">{{ label }}</span>
  </li>
</template>

<style scoped>
.activity {
  --tone: var(--q-text-3);
  display: grid;
  grid-template-columns: 10px minmax(0, 1fr) auto;
  align-items: center;
  gap: 11px;
  padding: 10px 13px;
  border-top: 1px solid var(--q-border);
  animation: row-in 280ms cubic-bezier(.2, .8, .2, 1) both;
}
.activity:first-child { border-top: 0; }
.activity.running { --tone: var(--q-brand, #2e80d6); }
.activity.waiting { --tone: #e2a03f; }
.activity.success { --tone: var(--q-success, #2f9e6a); }
.activity.failed { --tone: var(--q-danger, #d24545); }
.mark { position: relative; width: 9px; height: 9px; border-radius: 50%; background: var(--tone); box-shadow: 0 0 0 3px color-mix(in srgb, var(--tone) 18%, transparent); }
.running .mark::after, .waiting .mark::after { content: ""; position: absolute; inset: 0; border-radius: 50%; background: var(--tone); animation: beacon 2s cubic-bezier(0, 0, .2, 1) infinite; }
.body { display: flex; min-width: 0; flex-direction: column; gap: 2px; }
.title { overflow: hidden; color: var(--q-text); font-size: 12.5px; font-weight: 620; text-overflow: ellipsis; white-space: nowrap; }
.sub { color: var(--q-text-3); font-size: 11px; }
.meter { display: flex; flex-direction: column; align-items: flex-end; gap: 4px; width: 96px; }
.percent { color: color-mix(in srgb, var(--tone) 72%, var(--q-text)); font-size: 11px; font-weight: 650; font-variant-numeric: tabular-nums; }
.bar { position: relative; width: 100%; height: 4px; overflow: hidden; border-radius: 99px; background: color-mix(in srgb, var(--q-text-3) 18%, transparent); }
.bar span { position: absolute; inset: 0; border-radius: inherit; background: var(--tone); transform-origin: left; transition: transform 600ms cubic-bezier(.2, .8, .2, 1); }
.running .bar span::after { content: ""; position: absolute; inset: 0; background: linear-gradient(90deg, transparent, rgb(255 255 255 / .55), transparent); animation: sheen 1.8s ease-in-out infinite; transform: translateX(-100%); }
.state { justify-self: end; padding: 2px 8px; border-radius: 99px; color: color-mix(in srgb, var(--tone) 72%, var(--q-text)); background: color-mix(in srgb, var(--tone) 14%, transparent); font-size: 11px; font-weight: 600; white-space: nowrap; }
@keyframes beacon { 70%, to { opacity: 0; transform: scale(2.4); } }
@keyframes sheen { to { transform: translateX(100%); } }
@keyframes row-in { from { opacity: 0; transform: translateY(4px); } }
@media (prefers-reduced-motion: reduce) {
  .activity, .running .mark::after, .waiting .mark::after, .running .bar span::after { animation: none; }
  .bar span { transition: none; }
}
</style>
