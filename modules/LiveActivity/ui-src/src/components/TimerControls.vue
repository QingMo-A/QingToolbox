<script setup lang="ts">
import { ref, watch } from 'vue'
import { QButton } from '@qingtoolbox/module-ui'
import type { Settings, SettingsPatch, TimerView } from '../types'
const props = defineProps<{ settings: Settings; timers: { stopwatch: TimerView; countdown: TimerView }; busy: boolean; canRun: boolean }>()
const emit = defineEmits<{
  command: [kind: 'stopwatch' | 'countdown', action: 'start' | 'pause' | 'reset']
  patch: [patch: SettingsPatch]
}>()
const minutes = ref(5)
const seconds = ref(0)
watch(() => props.settings.countdownSeconds, value => {
  minutes.value = Math.floor(value / 60); seconds.value = value % 60
}, { immediate:true })
function durationChanged() {
  const value = Math.floor(Math.max(0, Number(minutes.value) || 0)) * 60 + Math.floor(Math.max(0, Math.min(59, Number(seconds.value) || 0)))
  emit('patch', { countdownSeconds:Math.max(1, Math.min(604800, value)) })
}
</script>

<template>
  <section class="card card-timers">
    <h2>计时与倒计时</h2>
    <div class="timer-grid">
      <div v-for="kind in (['stopwatch', 'countdown'] as const)" :key="kind" class="timer-tile" :class="{ finished: timers[kind].finished }">
        <div class="timer-heading">
          <strong>{{ kind === 'stopwatch' ? '计时器' : '倒计时' }}</strong>
          <label class="timer-visibility">
            <input type="checkbox" :checked="kind === 'stopwatch' ? settings.showStopwatch : settings.showCountdown" :disabled="busy" @change="emit('patch', kind === 'stopwatch' ? { showStopwatch: ($event.target as HTMLInputElement).checked } : { showCountdown: ($event.target as HTMLInputElement).checked })" />
            显示
          </label>
        </div>
        <output class="timer-value">{{ timers[kind].text }}</output>
        <div class="timer-status">{{ timers[kind].finished ? '倒计时结束' : timers[kind].running ? '进行中' : timers[kind].started ? '已暂停' : '未开始' }}</div>
        <div v-if="kind === 'countdown'" class="duration-inputs" title="修改时长会重置倒计时">
          <label><input v-model="minutes" type="number" min="0" max="10080" step="1" :disabled="busy || timers.countdown.running" @change="durationChanged" aria-label="倒计时分钟" /> 分</label>
          <label><input v-model="seconds" type="number" min="0" max="59" step="1" :disabled="busy || timers.countdown.running" @change="durationChanged" aria-label="倒计时秒数" /> 秒</label>
        </div>
        <div class="timer-actions">
          <QButton size="small" :variant="timers[kind].running ? 'secondary' : 'primary'" :disabled="busy || !canRun" @click="emit('command', kind, timers[kind].running ? 'pause' : 'start')">{{ timers[kind].running ? '暂停' : timers[kind].finished ? '重新开始' : timers[kind].started ? '继续' : '开始' }}</QButton>
          <QButton size="small" :disabled="busy || !timers[kind].started" @click="emit('command', kind, 'reset')">重置</QButton>
        </div>
      </div>
    </div>
  </section>
</template>

<style scoped>
.timer-grid { display:grid; grid-template-columns:repeat(2,minmax(0,1fr)); gap:12px; }
.timer-tile { min-width:0; display:flex; flex-direction:column; padding:14px; gap:8px; border:1px solid var(--q-border); border-radius:14px; background:var(--q-surface-soft); transition:border-color 180ms, background 180ms; }
.timer-tile.finished { border-color:var(--q-brand); background:color-mix(in srgb, var(--q-brand) 9%, var(--q-surface-soft)); }
.timer-heading { display:flex; align-items:center; justify-content:space-between; gap:6px; }
.timer-visibility { display:flex; align-items:center; gap:5px; font-size:11px; color:var(--q-text-3); }
.timer-value { font-size:clamp(18px,2vw,27px); font-weight:700; font-variant-numeric:tabular-nums; line-height:1.5; }
.timer-status { color:var(--q-text-3); font-size:11px; }
.duration-inputs { display:flex; flex-wrap:wrap; gap:8px; font-size:12px; }
.duration-inputs label { display:flex; align-items:center; gap:5px; }
.duration-inputs input { width:64px; padding:6px; color:var(--q-text); background:var(--q-card); border:1px solid var(--q-border); border-radius:8px; }
.duration-inputs input:disabled { opacity:.5; }
.timer-actions { display:flex; flex-wrap:wrap; gap:7px; margin-top:auto; padding-top:6px; }
@media(max-width:540px) { .timer-grid { grid-template-columns:1fr; } }
@media(prefers-reduced-motion:reduce) { .timer-tile { transition:none; } }
</style>
