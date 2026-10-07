<script setup lang="ts">
import { computed } from 'vue'
import type { CodexAccount, ProviderHealth, ProviderKind, ProviderStatus } from '../types'
import CodexQuota from './CodexQuota.vue'

const props = defineProps<{
  kind: ProviderKind
  implemented: boolean
  status: ProviderStatus | null
  account?: CodexAccount | null
}>()

const NAME: Record<ProviderKind, string> = {
  mock: '模拟数据',
  codex: 'Codex',
  media: '媒体播放',
  transfer: '文件传输',
}

const HEALTH: Record<ProviderHealth, string> = {
  connected: '已连接',
  disconnected: '未连接',
  unavailable: '不可用',
  disabled: '已关闭',
}

/**
 * The one line that explains this provider's state.
 *
 * An unimplemented provider says so plainly rather than showing a fabricated
 * "connected": the module's whole contract is that it reports what it can
 * observe and nothing else.
 */
const detail = computed(() => {
  if (!props.implemented) return '稍后支持'
  if (props.kind === 'codex') {
    const message = props.status?.detail
    if (message?.includes('CLI was not found')) return '未找到 Codex 程序；请安装 Codex 或配置程序路径'
    if (message === 'connecting') return '正在连接 Codex…'
    if (props.status?.health === 'connected') {
      if (props.account?.connectionMode === 'shared') return `共享实例 · 工作 ${props.account.workingThreads} · 等待 ${props.account.waitingThreads}`
      return '仅额度连接 · 桌面任务未接入'
    }
  }
  return props.status?.detail ?? HEALTH[props.status?.health ?? 'disabled']
})

const health = computed<ProviderHealth | 'planned'>(() =>
  props.implemented ? (props.status?.health ?? 'disabled') : 'planned',
)
</script>

<template>
  <div class="provider" :class="health">
    <div class="head">
      <i class="dot" />
      <strong>{{ NAME[kind] }}</strong>
      <span v-if="implemented && status && (kind !== 'codex' || account?.connectionMode === 'shared')" class="count">
        {{ status.activityCount }}
      </span>
    </div>
    <p class="detail">{{ detail }}</p>
    <CodexQuota v-if="kind === 'codex'" :account="account ?? null" :connected="health === 'connected'" />
  </div>
</template>

<style scoped>
.provider {
  display: flex;
  min-width: 0;
  flex-direction: column;
  gap: 5px;
  padding: 11px 13px;
  border: 1px solid var(--q-border);
  border-radius: 12px;
  background: var(--q-card);
}
.head {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 12.5px;
  color: var(--q-text);
}
.dot {
  flex: 0 0 8px;
  width: 8px;
  height: 8px;
  border-radius: 50%;
  background: var(--q-text-3);
}
.connected .dot {
  background: var(--q-success, #2f9e6a);
}
.unavailable .dot {
  background: #e2a03f;
}
.planned .dot {
  background: var(--q-border);
}
.count {
  margin-left: auto;
  padding: 1px 7px;
  border-radius: 99px;
  color: var(--q-text-3);
  background: var(--q-surface-soft);
  font-size: 11px;
}
.detail {
  margin: 0;
  overflow: hidden;
  color: var(--q-text-3);
  font-size: 11px;
  text-overflow: ellipsis;
}
.provider.planned {
  opacity: 0.7;
}
</style>
