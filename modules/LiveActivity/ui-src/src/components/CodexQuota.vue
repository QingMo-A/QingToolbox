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
      <div v-for="row in rows" :key="row.id" class="quota-window">
        <div class="quota-heading">
          <span>{{ row.label }}</span>
          <strong>{{ row.remaining === null ? '未提供' : `剩余 ${Math.round(row.remaining)}%` }}</strong>
        </div>
        <div v-if="row.remaining !== null" class="quota-track" role="progressbar" :aria-label="`${row.label}剩余`" :aria-valuenow="row.remaining" aria-valuemin="0" aria-valuemax="100">
          <i :style="{ width:`${row.remaining}%` }" :class="{ low:row.remaining <= 10 }" />
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
.codex-quota { border-top:1px solid var(--q-border); margin-top:6px; padding-top:12px; }
.quota-windows { display:grid; gap:14px; }
.quota-heading { display:flex; flex-wrap:wrap; justify-content:space-between; gap:6px 12px; color:var(--q-text-2); font-size:12px; }
.quota-heading strong { color:var(--q-text); font-variant-numeric:tabular-nums; }
.quota-track { margin:8px 0 5px; height:5px; overflow:hidden; border-radius:99px; background:var(--q-surface-soft); }
.quota-track i { display:block; height:100%; border-radius:inherit; background:var(--q-brand); transition:width 240ms ease-out; }
.quota-track i.low { background:var(--q-warning, #e2a03f); }
.quota-reset, .quota-empty { color:var(--q-text-3); font-size:11px; }
.quota-empty { margin:0 0 10px; }
.quota-refresh { margin-top:12px; display:flex; flex-wrap:wrap; justify-content:space-between; gap:5px 12px; color:var(--q-text-3); font-size:10.5px; font-variant-numeric:tabular-nums; }
@media(prefers-reduced-motion:reduce) { .quota-track i { transition:none; } }
</style>
