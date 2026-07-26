<script setup lang="ts">
import { computed } from 'vue'

type QIconName =
  | 'home'
  | 'modules'
  | 'running'
  | 'logs'
  | 'settings'
  | 'diagnostics'
  | 'pin'
  | 'unpin'
  | 'refresh'
  | 'statusSuccess'
  | 'statusInfo'
  | 'statusWarning'
  | 'statusDanger'
  | 'back'
  | 'search'
  | 'close'

const props = defineProps<{ name: QIconName; size?: number }>()

const fluentGlyphs: Partial<Record<QIconName, string>> = {
  home: '\uE80F',
  modules: '\uE71D',
  running: '\uE768',
  logs: '\uE9D9',
  settings: '\uE713',
  diagnostics: '\uE9D2',
  pin: '\uE718',
  unpin: '\uE77A',
}

const fluentGlyph = computed(() => fluentGlyphs[props.name])
</script>

<template>
  <span
    v-if="fluentGlyph"
    class="q-icon q-fluent-icon"
    :style="{ width: `${size ?? 20}px`, height: `${size ?? 20}px`, fontSize: `${size ?? 20}px` }"
    aria-hidden="true"
  >{{ fluentGlyph }}</span>
  <svg
    v-else
    class="q-icon"
    :width="size ?? 20"
    :height="size ?? 20"
    viewBox="0 0 20 20"
    fill="none"
    stroke="currentColor"
    stroke-width="1.7"
    stroke-linecap="round"
    stroke-linejoin="round"
    aria-hidden="true"
    focusable="false"
  >
    <template v-if="name === 'refresh'">
      <path d="M16.5 7A7 7 0 1 0 17 11" />
      <path d="M13 3h4v4" />
    </template>
    <template v-else-if="name === 'statusSuccess'">
      <circle cx="10" cy="10" r="7" />
      <path d="m6.8 10.2 2.1 2.1 4.5-4.6" />
    </template>
    <template v-else-if="name === 'statusInfo'">
      <circle cx="10" cy="10" r="7" />
      <path d="M10 9v4M10 6.5h.01" />
    </template>
    <template v-else-if="name === 'statusWarning'">
      <path d="M9 3.7 2.8 15a1.2 1.2 0 0 0 1.1 1.8h12.2a1.2 1.2 0 0 0 1.1-1.8L11 3.7a1.2 1.2 0 0 0-2 0Z" />
      <path d="M10 7v4M10 14h.01" />
    </template>
    <template v-else-if="name === 'statusDanger'">
      <circle cx="10" cy="10" r="7" />
      <path d="m7.5 7.5 5 5m0-5-5 5" />
    </template>
    <template v-else-if="name === 'back'">
      <path d="m9 4-6 6 6 6M3 10h14" />
    </template>
    <template v-else-if="name === 'search'">
      <circle cx="8.5" cy="8.5" r="5.5" />
      <path d="m13 13 4 4" />
    </template>
    <template v-else-if="name === 'close'">
      <path d="m5 5 10 10M15 5 5 15" />
    </template>
  </svg>
</template>
