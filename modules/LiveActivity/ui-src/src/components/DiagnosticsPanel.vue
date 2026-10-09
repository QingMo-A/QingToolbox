<script setup lang="ts">
import { computed } from 'vue'
import type { Diagnostics } from '../types'

const props = defineProps<{ diagnostics: Diagnostics | null }>()

/** Newest first: a log is read from the end. */
const rows = computed(() => {
  const entries = props.diagnostics?.entries ?? []
  return [...entries].reverse().slice(0, 80)
})

const LEVEL = { information: 'INFO', warning: 'WARN', error: 'ERR' } as const

function clock(atMs: number): string {
  const date = new Date(atMs)
  const pad = (value: number) => String(value).padStart(2, '0')
  return `${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}`
}
</script>

<template>
  <div class="diagnostics">
    <p v-if="!diagnostics" class="muted">点击「读取日志」查看模块最近发生的事。</p>
    <template v-else>
      <p class="muted">
        累计记录 {{ diagnostics.totalRecorded }} 条，显示最近 {{ rows.length }} 条。
        日志只记录生命周期与错误，不含提示词、令牌或会话内容。
      </p>
      <ul v-if="rows.length" class="log">
        <li v-for="(entry, index) in rows" :key="index" :class="entry.level">
          <span class="time">{{ clock(entry.atMs) }}</span>
          <span class="level">{{ LEVEL[entry.level] ?? entry.level }}</span>
          <span class="scope">{{ entry.scope }}</span>
          <span class="message">{{ entry.message }}</span>
        </li>
      </ul>
      <p v-else class="muted">暂无日志。</p>
    </template>
  </div>
</template>

<style scoped>
.diagnostics { display: flex; flex-direction: column; gap: 10px; }
.log {
  max-height: 280px;
  margin: 0;
  padding: 6px 0;
  overflow: auto;
  border: 1px solid var(--q-border);
  border-radius: 12px;
  background: color-mix(in srgb, var(--q-surface-soft) 70%, var(--q-bg));
  list-style: none;
  font-family: ui-monospace, "Cascadia Mono", Consolas, monospace;
  scrollbar-color: color-mix(in srgb, var(--q-text-3) 40%, transparent) transparent;
  content-visibility: auto;
}
.log li { display: grid; grid-template-columns: 62px 40px 112px minmax(0, 1fr); gap: 10px; align-items: baseline; padding: 5px 14px; color: var(--q-text-2); font-size: 11.5px; }
.log li:hover { background: color-mix(in srgb, var(--q-brand) 6%, transparent); }
.time, .scope { color: var(--q-text-3); }
.scope { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.level { justify-self: start; padding: 0 5px; border-radius: 4px; color: var(--q-text-3); background: color-mix(in srgb, var(--q-text-3) 14%, transparent); font-size: 9.5px; font-weight: 700; letter-spacing: .04em; }
.warning .level { color: color-mix(in srgb, var(--q-warning, #e2a03f) 62%, var(--q-text)); background: color-mix(in srgb, var(--q-warning, #e2a03f) 18%, transparent); }
.error .level { color: var(--q-danger, #d24545); background: color-mix(in srgb, var(--q-danger, #d24545) 15%, transparent); }
.warning .message { color: color-mix(in srgb, var(--q-warning, #e2a03f) 62%, var(--q-text)); }
.error .message { color: var(--q-danger, #d24545); }
.message { overflow-wrap: anywhere; }
.muted { margin: 0; color: var(--q-text-3); font-size: 12px; line-height: 1.6; }
@media (max-width: 560px) { .log li { grid-template-columns: 58px 36px minmax(0, 1fr); } .scope { display: none; } }
</style>
