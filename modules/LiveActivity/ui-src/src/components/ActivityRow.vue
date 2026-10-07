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
  <li class="activity">
    <i class="mark" :class="activity.state" />
    <div class="body">
      <span class="title">{{ activity.title }}</span>
      <span class="sub">
        {{ label }}<template v-if="status"> · {{ status }}</template>
      </span>
    </div>
    <div v-if="fraction !== null" class="bar">
      <span :style="{ width: `${Math.round(fraction * 100)}%` }" />
    </div>
    <span v-else class="state">{{ label }}</span>
  </li>
</template>

<style scoped>
.activity {
  display: grid;
  grid-template-columns: 10px minmax(0, 1fr) 96px;
  align-items: center;
  gap: 10px;
  padding: 9px 12px;
  border-top: 1px solid var(--q-border);
}
.mark {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  background: var(--q-text-3);
}
.mark.running {
  background: var(--q-brand, #2e80d6);
}
.mark.waiting {
  background: #e2a03f;
}
.mark.success {
  background: var(--q-success, #2f9e6a);
}
.mark.failed {
  background: var(--q-danger, #d24545);
}
.body {
  display: flex;
  min-width: 0;
  flex-direction: column;
  gap: 2px;
}
.title {
  overflow: hidden;
  color: var(--q-text);
  font-size: 12.5px;
  font-weight: 620;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.sub {
  color: var(--q-text-3);
  font-size: 11px;
}
.bar {
  height: 4px;
  overflow: hidden;
  border-radius: 99px;
  background: var(--q-border);
}
.bar span {
  display: block;
  height: 100%;
  border-radius: 99px;
  background: var(--q-brand, #2e80d6);
}
.state {
  justify-self: end;
  color: var(--q-text-3);
  font-size: 11px;
}
</style>
