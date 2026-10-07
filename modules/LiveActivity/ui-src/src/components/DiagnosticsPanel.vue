<script setup lang="ts">
import { computed } from 'vue'
import type { Diagnostics } from '../types'

const props = defineProps<{ diagnostics: Diagnostics | null }>()

/** Newest first: a log is read from the end. */
const rows = computed(() => {
  const entries = props.diagnostics?.entries ?? []
  return [...entries].reverse().slice(0, 80)
})

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
          <span class="scope">{{ entry.scope }}</span>
          <span class="message">{{ entry.message }}</span>
        </li>
      </ul>
      <p v-else class="muted">暂无日志。</p>
    </template>
  </div>
</template>

<style scoped>
.diagnostics {
  display: flex;
  flex-direction: column;
  gap: 8px;
}
.log {
  max-height: 260px;
  margin: 0;
  padding: 0;
  overflow: auto;
  border: 1px solid var(--q-border);
  border-radius: 12px;
  list-style: none;
  scrollbar-color: #bdcfe6 transparent;
}
.log li {
  display: grid;
  grid-template-columns: 66px 92px minmax(0, 1fr);
  gap: 10px;
  padding: 7px 12px;
  border-top: 1px solid var(--q-border);
  font-size: 11.5px;
  color: var(--q-text-2);
}
.log li:first-child {
  border-top: 0;
}
.log li.warning .message {
  color: #96600c;
}
.log li.error .message {
  color: var(--q-danger, #d24545);
}
.time,
.scope {
  color: var(--q-text-3);
  font-family: ui-monospace, SFMono-Regular, Consolas, monospace;
}
.message {
  overflow-wrap: anywhere;
}
.muted {
  margin: 0;
  color: var(--q-text-3);
  font-size: 12px;
}
</style>
