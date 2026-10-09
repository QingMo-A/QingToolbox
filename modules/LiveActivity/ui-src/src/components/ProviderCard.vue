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
      <span class="glyph" aria-hidden="true">
        <svg v-if="kind === 'codex'" viewBox="0 0 24 24"><path d="M8.5 8 4.5 12l4 4M15.5 8l4 4-4 4M13.5 6l-3 12" /></svg>
        <svg v-else viewBox="0 0 24 24"><path d="M9.5 3.5h5M10.5 3.5v6L5 18.5a1.5 1.5 0 0 0 1.3 2.2h11.4a1.5 1.5 0 0 0 1.3-2.2L13.5 9.5v-6M7.5 15h9" /></svg>
      </span>
      <strong>{{ NAME[kind] }}</strong>
      <span class="health"><i class="dot" />{{ implemented ? HEALTH[status?.health ?? 'disabled'] : '规划中' }}</span>
      <span v-if="implemented && status && (kind !== 'codex' || account?.connectionMode === 'shared')" class="count" title="活动数">
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
  gap: 8px;
  padding: 14px 15px;
  border: 1px solid var(--q-border);
  border-radius: 14px;
  background: var(--q-surface-soft);
}
.head { display: flex; align-items: center; gap: 9px; color: var(--q-text); font-size: 13px; }
.glyph { display: grid; flex: none; place-items: center; width: 28px; height: 28px; border-radius: 9px; color: var(--q-brand); background: var(--q-brand-soft); }
.glyph svg { width: 16px; height: 16px; fill: none; stroke: currentColor; stroke-width: 1.8; stroke-linecap: round; stroke-linejoin: round; }
.health { display: inline-flex; align-items: center; gap: 6px; margin-left: auto; padding: 2px 9px 2px 7px; border-radius: 99px; color: var(--q-text-3); background: var(--q-card); font-size: 11px; font-weight: 600; }
.dot { position: relative; flex: none; width: 7px; height: 7px; border-radius: 50%; background: var(--q-text-3); }
.connected .health { color: var(--q-success, #2f9e6a); }
.connected .dot { background: var(--q-success, #2f9e6a); }
.connected .dot::after { content: ""; position: absolute; inset: 0; border-radius: 50%; background: inherit; animation: beacon 2.4s cubic-bezier(0, 0, .2, 1) infinite; }
.unavailable .health { color: var(--q-warning, #c27c12); }
.unavailable .dot { background: var(--q-warning, #e2a03f); }
.planned .dot { background: var(--q-border); }
.count { min-width: 22px; padding: 1px 7px; border-radius: 99px; color: var(--q-text-2); background: var(--q-card); font-size: 11px; font-variant-numeric: tabular-nums; text-align: center; }
.detail { margin: 0; overflow: hidden; color: var(--q-text-3); font-size: 11.5px; text-overflow: ellipsis; }
.provider.planned { opacity: .7; }
@keyframes beacon { 70%, to { opacity: 0; transform: scale(2.6); } }
:root[data-appearance-preset='qing-nova'] .provider { border: 3px solid #111; border-radius: 0; background: #fff; box-shadow: 4px 4px 0 #111; }
:root[data-appearance-preset='qing-nova'] .glyph { border-radius: 0; }
:root[data-appearance-preset='neon-circuit'] .provider,
:root[data-appearance-preset='neon-circuit'] .glyph { border-radius: 3px; }
@media (prefers-reduced-motion: reduce) { .connected .dot::after { animation: none; } }
</style>
