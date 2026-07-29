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
  | 'import'
  | 'download'
  | 'statusSuccess'
  | 'statusInfo'
  | 'statusWarning'
  | 'statusDanger'
  | 'back'
  | 'search'
  | 'close'
  | 'folder'
  | 'remove'

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
    <template v-else-if="name === 'import'">
      <path d="M10 3v9" />
      <path d="m6.5 8.5 3.5 3.5 3.5-3.5" />
      <path d="M4 13.5v2.5h12v-2.5" />
    </template>
    <template v-else-if="name === 'download'">
      <path d="M10 2.8v9" />
      <path d="m6.4 8.5 3.6 3.6 3.6-3.6" />
      <path d="M3.2 13.8v2.4h13.6v-2.4" />
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
    <template v-else-if="name === 'folder'">
      <path d="M2.8 5.5h5l1.5 1.8h7.9v8.2H2.8z" />
      <path d="M2.8 7.3V4.5h4.3l1.4 1.6" />
    </template>
    <template v-else-if="name === 'remove'">
      <path d="M4.5 6.2h11" />
      <path d="M8 3.8h4l.7 2.4H7.3z" />
      <path d="m6.2 6.2.7 10h6.2l.7-10M8.5 9v4.6M11.5 9v4.6" />
    </template>
  </svg>
</template>
