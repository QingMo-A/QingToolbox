<script setup lang="ts">
import { computed, ref } from 'vue'
import { invoke } from '@tauri-apps/api/core'
import mark from '../../assets/QingToolbox.Mark.svg'
import { useLocalization } from '../../localization/localization'

const { currentLocale } = useLocalization()
const error = ref('')
const labels = computed(() => currentLocale.value === 'en-US'
  ? { floatingBadge: 'Floating window', minimize: 'Minimize', toggleMaximize: 'Maximize / Restore', close: 'Close' }
  : { floatingBadge: '切换到悬浮窗', minimize: '最小化', toggleMaximize: '最大化 / 还原', close: '关闭' })
async function act(action: 'floatingBadge' | 'minimize' | 'toggleMaximize' | 'close' | 'drag') {
  error.value = ''
  try { await invoke('control_main_window', { action }) }
  catch { error.value = currentLocale.value === 'en-US' ? 'Window action failed' : '窗口操作失败，请重试' }
}
function drag(event: MouseEvent) {
  if (event.button === 0 && event.detail < 2) void act('drag')
}
</script>

<template>
  <header class="q-titlebar" @contextmenu.prevent>
    <div class="q-titlebar-drag" @mousedown="drag" @dblclick="act('toggleMaximize')">
      <img :src="mark" alt="" draggable="false" />
      <span>QingToolbox</span>
      <small v-if="error" role="status">{{ error }}</small>
    </div>
    <div class="q-titlebar-actions">
      <button v-for="action in (['floatingBadge', 'minimize', 'toggleMaximize', 'close'] as const)"
        :key="action" type="button" :class="{ 'is-close': action === 'close' }"
        :aria-label="labels[action]" :title="labels[action]" @click="act(action)">
        <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" aria-hidden="true">
          <g v-if="action === 'floatingBadge'"><rect x="4" y="4" width="16" height="16" rx="5"/><rect x="9" y="9" width="6" height="6" rx="2"/></g>
          <path v-else-if="action === 'minimize'" d="M5 12h14"/>
          <rect v-else-if="action === 'toggleMaximize'" x="6" y="6" width="12" height="12" rx="1"/>
          <path v-else d="m6 6 12 12M18 6 6 18"/>
        </svg>
      </button>
    </div>
  </header>
</template>

<style scoped>
.q-titlebar { display: flex; height: 40px; background: var(--q-surface-soft); color: var(--q-text); border-bottom: 1px solid var(--q-border); user-select: none; }
.q-titlebar-drag { display: flex; flex: 1; min-width: 0; align-items: center; gap: 9px; padding-left: 14px; font-size: 12px; }
.q-titlebar-drag img { width: 22px; height: 22px; pointer-events: none; }
.q-titlebar-drag small { color: var(--q-danger); }
.q-titlebar-actions { display: flex; }
.q-titlebar-actions button { display: grid; place-items: center; width: 46px; border: 0; background: transparent; color: inherit; cursor: default; }
.q-titlebar-actions button:hover { background: var(--q-hover); }
.q-titlebar-actions button.is-close:hover { color: white; background: #c42b1c; }
.q-titlebar-actions button:focus-visible { outline-offset: -3px; }
.q-titlebar-actions svg { width: 17px; height: 17px; }
</style>
