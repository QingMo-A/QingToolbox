<script setup lang="ts">
import { computed, ref } from 'vue'
import { useSettingsStore } from '../../app/settingsStore'
import { useAppStore } from '../../app/store'
import brandMark from '../../assets/QingToolbox.Mark.svg'
import QIcon from '../components/QIcon.vue'
import { useLocalization } from '../../localization/localization'

const emit = defineEmits<{ openCommandPalette: [] }>()
const hovered = ref(false)
const pinned = ref(false)
const settings = useSettingsStore()
const app = useAppStore()
const { t } = useLocalization()
const showLogs = computed(
  () => settings.status !== 'ready' || settings.snapshot?.showLogsInSidebar === true,
)
const showDiagnostics = computed(() => app.snapshot?.environmentKind === 'Development')
</script>

<template>
  <div class="q-shell" :class="{ expanded: hovered || pinned, pinned }">
    <aside
      class="q-sidebar"
      @mouseenter="hovered = true"
      @mouseleave="hovered = false"
    >
      <div class="q-brand">
        <img :src="brandMark" alt="" aria-hidden="true" />
        <strong>QingToolbox</strong>
      </div>

      <nav :aria-label="t('navigation.workspace')">
        <RouterLink to="/" :title="t('navigation.home')" :aria-label="t('navigation.home')">
          <b><QIcon name="home" /></b>
          <span>{{ t('navigation.home') }}</span>
        </RouterLink>
        <RouterLink to="/modules" :title="t('navigation.modules')" :aria-label="t('navigation.modules')">
          <b><QIcon name="modules" /></b>
          <span>{{ t('navigation.modules') }}</span>
        </RouterLink>
        <RouterLink to="/running" :title="t('navigation.running')" :aria-label="t('navigation.running')">
          <b><QIcon name="running" /></b>
          <span>{{ t('navigation.running') }}</span>
        </RouterLink>
        <RouterLink v-if="showLogs" to="/logs" :title="t('navigation.logs')" :aria-label="t('navigation.logs')">
          <b><QIcon name="logs" /></b>
          <span>{{ t('navigation.logs') }}</span>
        </RouterLink>
      </nav>

      <button
        class="q-sidebar-quick-open"
        type="button"
        :aria-label="t('navigation.quickOpen')"
        :title="t('navigation.quickOpenShortcut')"
        @click="emit('openCommandPalette')"
      >
        <b><QIcon name="search" /></b>
        <span>{{ t('navigation.quickOpen') }}</span>
      </button>

      <div class="q-sidebar-spacer" />

      <nav class="q-sidebar-secondary">
        <RouterLink v-if="showDiagnostics" to="/diagnostics" :title="t('navigation.diagnostics')" :aria-label="t('navigation.diagnostics')">
          <b><QIcon name="diagnostics" /></b>
          <span>{{ t('navigation.diagnostics') }}</span>
        </RouterLink>
        <button
          :class="{ active: pinned }"
          :aria-label="pinned ? t('navigation.unpinSidebar') : t('navigation.pinSidebar')"
          :title="pinned ? t('navigation.unpinSidebar') : t('navigation.pinSidebar')"
          @click="pinned = !pinned"
        >
          <b><QIcon :name="pinned ? 'unpin' : 'pin'" /></b>
          <span>{{ pinned ? t('navigation.unpinSidebar') : t('navigation.pinSidebar') }}</span>
        </button>
        <RouterLink to="/settings" :title="t('navigation.settings')" :aria-label="t('navigation.settings')">
          <b><QIcon name="settings" /></b>
          <span>{{ t('navigation.settings') }}</span>
        </RouterLink>
      </nav>
    </aside>

    <div class="q-workspace">
      <slot />
    </div>
  </div>
</template>
