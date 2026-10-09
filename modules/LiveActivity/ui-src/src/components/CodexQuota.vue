<script setup lang="ts">
import { computed } from 'vue'
import type { CodexAccount } from '../types'
import { quotaLabel, remainingPercent, resetLabel, timeLabel } from '../quota'

const props = defineProps<{ account: CodexAccount | null; connected: boolean }>()
const rows = computed(() => {
  const limits = props.account?.limits
  return [
    { id:'primary', window:limits?.primary, fallback:'主额度' },
    { id:'secondary', window:limits?.secondary, fallback:'次额度' },
  ].flatMap(({id, window, fallback}) => window ? [{
    id, label:quotaLabel(window, fallback), remaining:remainingPercent(window),
    reset:resetLabel(window.resetsAt, props.account?.updatedAtMs ?? null),
  }] : [])
})
</script>

<template>
  <div class="codex-quota">
    <div v-if="rows.length" class="quota-windows">
      <div v-for="row in rows" :key="row.id" class="quota-window" :class="{ low: row.remaining !== null && row.remaining <= 10 }">
        <div class="quota-heading">
          <span>{{ row.label }}</span>
          <strong>{{ row.remaining === null ? '未提供' : `剩余 ${Math.round(row.remaining)}%` }}</strong>
        </div>
        <div v-if="row.remaining !== null" class="quota-track" role="progressbar" :aria-label="`${row.label}剩余`" :aria-valuenow="row.remaining" aria-valuemin="0" aria-valuemax="100">
          <i :style="{ transform:`scaleX(${row.remaining / 100})` }" />
        </div>
        <small class="quota-reset">{{ row.reset }}</small>
      </div>
    </div>
    <p v-else class="quota-empty">{{ account?.error || (connected ? '正在读取额度…' : '额度尚未读取') }}</p>
    <div class="quota-refresh">
      <span :title="timeLabel(account?.updatedAtMs, true)">数据刷新 {{ timeLabel(account?.updatedAtMs) }}</span>
      <span>{{ account?.pollIntervalSeconds ?? 10 }} 秒间隔</span>
    </div>
  </div>
</template>

<style scoped>
.codex-quota { margin-top: 4px; padding-top: 12px; border-top: 1px dashed var(--q-border); }
.quota-windows { display: grid; grid-template-columns: repeat(auto-fit, minmax(150px, 1fr)); gap: 12px 18px; }
.quota-heading { display: flex; flex-wrap: wrap; align-items: baseline; justify-content: space-between; gap: 4px 12px; color: var(--q-text-2); font-size: 12px; }
.quota-heading strong { color: var(--q-text); font-size: 13px; font-variant-numeric: tabular-nums; }
.quota-track { position: relative; height: 6px; margin: 8px 0 6px; overflow: hidden; border-radius: 99px; background: color-mix(in srgb, var(--q-text-3) 16%, transparent); }
/* Scaled, not resized: the bar's fill animates on the compositor. */
.quota-track i { position: absolute; inset: 0; border-radius: inherit; background: linear-gradient(90deg, color-mix(in srgb, var(--q-brand) 70%, #22d3ee), var(--q-brand)); transform-origin: left; transition: transform 420ms cubic-bezier(.2, .8, .2, 1); }
.low .quota-track i { background: linear-gradient(90deg, #f59e0b, var(--q-warning, #e2a03f)); }
.low .quota-heading strong { color: var(--q-warning, #c27c12); }
.quota-reset, .quota-empty { color: var(--q-text-3); font-size: 11px; }
.quota-empty { margin: 0 0 10px; }
.quota-refresh { display: flex; flex-wrap: wrap; justify-content: space-between; gap: 5px 12px; margin-top: 12px; color: var(--q-text-3); font-size: 10.5px; font-variant-numeric: tabular-nums; }
:root[data-appearance-preset='qing-nova'] .quota-track { border: 2px solid #111; border-radius: 0; height: 10px; }
:root[data-appearance-preset='qing-nova'] .quota-track i { border-radius: 0; background: #2f6bff; }
:root[data-appearance-preset='neon-circuit'] .quota-track i { background: var(--deco-foil); }
:root[data-appearance-preset='aurora-flow'] .quota-track i { background: linear-gradient(90deg, #ff2bd6, #00f0ff); box-shadow: 0 0 10px rgb(0 240 255 / .5); }
@media (prefers-reduced-motion: reduce) { .quota-track i { transition: none; } }
</style>
