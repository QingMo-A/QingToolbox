<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { QButton } from '@qingtoolbox/module-ui'
import type { Settings, SettingsPatch, TimerView } from '../types'
const props = defineProps<{
  settings: Settings
  timers: { stopwatch: TimerView; countdown: TimerView }
  busy: boolean
  canRun: boolean
  /** Which texts (常驻/悬停/展开) already reference each timer. */
  usage: { stopwatch: string[]; countdown: string[] }
  /** The text the insert buttons write into. */
  modeLabel: string
}>()
const emit = defineEmits<{
  command: [kind: 'stopwatch' | 'countdown', action: 'start' | 'pause' | 'reset']
  patch: [patch: SettingsPatch]
  insert: [key: string]
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
const presets = [1, 5, 10, 25]

/**
 * Dial geometry. The countdown arc is what is left of the configured length;
 * the stopwatch shows a comet that turns once a minute. Its angle is never
 * wrapped, so the eased CSS rotation between polls always moves forward.
 */
const dials = computed(() => ({
  stopwatch: { arc: 100, angle: props.timers.stopwatch.seconds * 6 },
  countdown: {
    arc: props.timers.countdown.started || props.timers.countdown.finished
      ? Math.max(0, Math.min(100, props.timers.countdown.seconds / Math.max(1, props.settings.countdownSeconds) * 100))
      : 100,
    angle: 0,
  },
}))
function status(view: TimerView): string {
  return view.finished ? '倒计时结束' : view.running ? '进行中' : view.started ? '已暂停' : '未开始'
}
</script>

<template>
  <section class="card card-timers">
    <div class="card-head">
      <span class="card-icon" aria-hidden="true"><svg><use href="#i-timer" /></svg></span>
      <div class="card-title"><h2>计时与倒计时</h2><p>计时数据只通过占位符显示，放在哪段文字、配什么字由你决定</p></div>
    </div>
    <div class="timer-grid">
      <div v-for="kind in (['stopwatch', 'countdown'] as const)" :key="kind" class="timer-tile" :class="{ finished: timers[kind].finished, running: timers[kind].running }">
        <div class="timer-heading">
          <strong>{{ kind === 'stopwatch' ? '计时器' : '倒计时' }}</strong>
          <span class="timer-usage" :class="{ used: usage[kind].length }" :title="usage[kind].length ? '这些文本里有它的占位符' : '还没有文本引用它；它不会自动出现在胶囊里'">
            {{ usage[kind].length ? `显示于 ${usage[kind].join('、')}` : '未在文本中使用' }}
          </span>
        </div>
        <div class="timer-dial" :class="kind">
          <svg class="dial" viewBox="0 0 120 120" aria-hidden="true">
            <circle class="dial-ticks" cx="60" cy="60" r="56" pathLength="60" />
            <circle class="dial-track" cx="60" cy="60" r="49" />
            <circle class="dial-arc" cx="60" cy="60" r="49" pathLength="100" :style="{ strokeDasharray: `${dials[kind].arc} 100`, opacity: dials[kind].arc > 0 ? 1 : 0 }" />
            <g v-if="kind === 'stopwatch'" class="dial-hand" :style="{ transform: `rotate(${dials.stopwatch.angle}deg)` }"><circle cx="60" cy="11" r="4.5" /></g>
          </svg>
          <div class="dial-readout">
            <output class="timer-value">{{ timers[kind].text }}</output>
            <div class="timer-status">{{ status(timers[kind]) }}</div>
          </div>
        </div>
        <div class="timer-insert" role="group" :aria-label="`插入${kind === 'stopwatch' ? '计时器' : '倒计时'}占位符`">
          <button type="button" :disabled="busy" :title="`插入到${modeLabel}文本：{${kind}}`" @click="emit('insert', kind)">+ {{ kind === 'stopwatch' ? '计时器' : '倒计时' }}</button>
          <button type="button" :disabled="busy" :title="`插入到${modeLabel}文本：{${kind}.state}`" @click="emit('insert', `${kind}.state`)">+ 状态</button>
        </div>
        <p v-if="kind === 'stopwatch'" class="timer-note">光点每分钟绕行一圈；暂停保留已计时间，重置才清零。</p>
        <div v-if="kind === 'countdown'" class="duration" title="修改时长会重置倒计时">
          <div class="duration-inputs">
            <label><input v-model="minutes" type="number" min="0" max="10080" step="1" :disabled="busy || timers.countdown.running" @change="durationChanged" aria-label="倒计时分钟" /><span>分</span></label>
            <label><input v-model="seconds" type="number" min="0" max="59" step="1" :disabled="busy || timers.countdown.running" @change="durationChanged" aria-label="倒计时秒数" /><span>秒</span></label>
          </div>
          <div class="duration-presets" role="group" aria-label="常用时长">
            <button v-for="value in presets" :key="value" type="button" :class="{ active: settings.countdownSeconds === value * 60 }" :disabled="busy || timers.countdown.running" @click="emit('patch', { countdownSeconds: value * 60 })">{{ value }}′</button>
          </div>
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
.timer-grid { display: grid; flex: 1; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 12px; }
.timer-tile {
  position: relative;
  display: flex;
  min-width: 0;
  flex-direction: column;
  gap: 12px;
  padding: 14px;
  overflow: hidden;
  border: 1px solid var(--q-border);
  border-radius: 16px;
  background: var(--q-surface-soft);
  transition: border-color 200ms, background-color 200ms, box-shadow 200ms;
}
.timer-tile.running { border-color: color-mix(in srgb, var(--q-brand) 45%, var(--q-border)); }
.timer-tile.finished { border-color: var(--q-brand); background: color-mix(in srgb, var(--q-brand) 9%, var(--q-surface-soft)); box-shadow: 0 0 0 3px var(--q-brand-soft); }
.timer-heading { display: flex; align-items: center; justify-content: space-between; gap: 6px; color: var(--q-text); font-size: 12.5px; }
.timer-usage { overflow: hidden; padding: 1px 8px; border-radius: 99px; color: var(--q-text-3); background: color-mix(in srgb, var(--q-text-3) 12%, transparent); font-size: 10.5px; font-weight: 600; text-overflow: ellipsis; white-space: nowrap; }
.timer-usage.used { color: var(--q-brand); background: var(--q-brand-soft); }
.timer-insert { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 6px; }
.timer-insert button {
  padding: 5px 0;
  border: 1px dashed color-mix(in srgb, var(--q-brand) 45%, var(--q-border));
  border-radius: 8px;
  color: var(--q-brand);
  background: transparent;
  font: 600 11.5px/1.3 inherit;
  cursor: pointer;
  transition: background-color 150ms, border-color 150ms, transform 150ms;
}
.timer-insert button:hover:not(:disabled) { border-style: solid; background: var(--q-brand-soft); }
.timer-insert button:active:not(:disabled) { transform: scale(.97); }
.timer-insert button:disabled { opacity: .5; cursor: default; }

.timer-dial { position: relative; display: grid; place-items: center; width: min(100%, 150px); aspect-ratio: 1; margin: 0 auto; }
.dial { position: absolute; inset: 0; width: 100%; height: 100%; overflow: visible; }
.dial-ticks { fill: none; stroke: color-mix(in srgb, var(--q-text-3) 45%, transparent); stroke-width: 3; stroke-dasharray: .12 .88; }
.dial-track { fill: none; stroke: color-mix(in srgb, var(--q-text-3) 18%, transparent); stroke-width: 7; }
.dial-arc {
  fill: none;
  stroke: var(--q-brand);
  stroke-width: 7;
  stroke-linecap: round;
  transform: rotate(-90deg);
  transform-origin: 60px 60px;
  transition: stroke-dasharray 1.5s linear, stroke 200ms;
}
.stopwatch .dial-arc { stroke: color-mix(in srgb, var(--q-brand) 26%, transparent); }
.dial-hand { transform-origin: 60px 60px; fill: var(--q-brand); }
.running .dial-hand { transition: transform 1.5s linear; }
.running .dial-hand circle { filter: drop-shadow(0 0 4px var(--q-brand)); }
.finished .dial-arc { stroke: var(--q-success, #2f9e6a); }
.dial-readout { position: relative; display: flex; flex-direction: column; align-items: center; gap: 2px; }
.timer-value { color: var(--q-text); font-size: 21px; font-weight: 700; font-variant-numeric: tabular-nums; letter-spacing: -.02em; line-height: 1.25; }
.timer-status { color: var(--q-text-3); font-size: 11px; }
.running .timer-status { color: var(--q-brand); font-weight: 600; }
.finished .timer-status { color: var(--q-success, #2f9e6a); font-weight: 650; }
.finished .timer-value { animation: finish-pulse 1.4s ease-in-out 3; }
@keyframes finish-pulse { 50% { transform: scale(1.06); } }

.timer-note { margin: 0; color: var(--q-text-3); font-size: 11px; line-height: 1.6; text-align: center; }
.duration { display: flex; flex-direction: column; gap: 8px; }
.duration-inputs { display: flex; gap: 8px; }
.duration-inputs label { position: relative; display: block; flex: 1; min-width: 0; }
/* The toolbox's shared input skin draws the field; the unit sits inside it. */
.duration-inputs input { width: 100%; min-width: 0; min-height: 34px; padding: 5px 26px 5px 10px; font: inherit; font-size: 12.5px; font-variant-numeric: tabular-nums; }
.duration-inputs input:disabled { opacity: .5; }
.duration-inputs span { position: absolute; top: 50%; right: 10px; color: var(--q-text-3); font-size: 11px; transform: translateY(-50%); pointer-events: none; }
.duration-presets { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 5px; }
.duration-presets button {
  padding: 4px 0;
  border: 1px solid var(--q-border);
  border-radius: 8px;
  color: var(--q-text-2);
  background: transparent;
  font: 600 11px/1.4 inherit;
  font-variant-numeric: tabular-nums;
  cursor: pointer;
}
.duration-presets button:hover:not(:disabled) { color: var(--q-text); border-color: var(--q-brand); }
.duration-presets button.active { color: var(--q-brand); border-color: var(--q-brand); background: var(--q-brand-soft); }
.duration-presets button:disabled { opacity: .5; cursor: default; }
.timer-actions { display: flex; flex-wrap: wrap; gap: 7px; margin-top: auto; }
.timer-actions :deep(.q-button) { flex: 1; min-width: 0; padding: 0 10px; white-space: nowrap; }

:root[data-appearance-preset='qing-nova'] .timer-tile { border: 3px solid #111; border-radius: 0; background: #fff; box-shadow: 4px 4px 0 #111; }
:root[data-appearance-preset='qing-nova'] .dial-arc { stroke-linecap: butt; }
:root[data-appearance-preset='aurora-flow'] .running .dial-arc,
:root[data-appearance-preset='aurora-flow'] .dial-hand circle { filter: drop-shadow(0 0 5px #00f0ff); }
:root[data-appearance-preset='neon-circuit'] .timer-tile { border-radius: 3px; }
:root[data-appearance-preset='neon-circuit'] .timer-value { font-family: var(--deco-figures); font-weight: 400; letter-spacing: .02em; }

@media (max-width: 540px) { .timer-grid { grid-template-columns: 1fr; } }
@media (prefers-reduced-motion: reduce) {
  .timer-tile, .dial-arc, .running .dial-hand { transition: none; }
  .finished .timer-value { animation: none; }
}
</style>
