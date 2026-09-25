<script setup lang="ts">
import { invoke } from '@tauri-apps/api/core'
import { onBeforeUnmount, onMounted, ref } from 'vue'
import markUrl from '../assets/QingToolbox.Mark.svg'

type PointerOrigin = { x: number; y: number }

const origin = ref<PointerOrigin | null>(null)
const dragging = ref(false)
const busy = ref(false)

async function restoreMain() {
  if (busy.value) return
  busy.value = true
  try {
    await invoke('show_main_from_floating_badge')
  } finally {
    busy.value = false
  }
}

function onPointerDown(event: PointerEvent) {
  if (event.button !== 0) return
  origin.value = { x: event.screenX, y: event.screenY }
  dragging.value = false
  window.addEventListener('pointermove', onPointerMove)
  window.addEventListener('pointerup', onPointerUp, { once: true })
}

function onPointerMove(event: PointerEvent) {
  const start = origin.value
  if (!start || dragging.value) return
  if (Math.hypot(event.screenX - start.x, event.screenY - start.y) < 5) return
  dragging.value = true
  window.removeEventListener('pointermove', onPointerMove)
  void invoke('start_floating_badge_drag')
    .catch(() => undefined)
}

function onPointerUp() {
  window.removeEventListener('pointermove', onPointerMove)
  const shouldRestore = origin.value !== null && !dragging.value
  origin.value = null
  if (shouldRestore) void restoreMain()
}

function onKeydown(event: KeyboardEvent) {
  if (event.key !== 'Enter' && event.key !== ' ') return
  event.preventDefault()
  void restoreMain()
}

onMounted(() => {
  // Resize only after WebView2 has mounted the document. Doing this during
  // Rust setup can retain Windows' decorated minimum width or leave an empty
  // transparent composition surface on some DPI configurations.
  void invoke('prepare_floating_badge_window').catch(() => undefined)
})

onBeforeUnmount(() => {
  window.removeEventListener('pointermove', onPointerMove)
  window.removeEventListener('pointerup', onPointerUp)
})
</script>

<template>
  <button
    class="floating-badge"
    type="button"
    aria-label="打开 QingToolbox"
    title="QingToolbox"
    @pointerdown="onPointerDown"
    @keydown="onKeydown"
    @contextmenu.prevent
  >
    <img :src="markUrl" alt="" draggable="false" />
  </button>
</template>

<style>
html.floating-badge-document,
html.floating-badge-document body,
html.floating-badge-document #app {
  width: 100%;
  height: 100%;
  min-width: 0;
  margin: 0;
  overflow: hidden;
  background: transparent !important;
}

.floating-badge {
  width: 60px;
  height: 60px;
  margin: 4px;
  padding: 0;
  border: 0;
  background: transparent;
  box-shadow: none;
  cursor: grab;
  user-select: none;
  touch-action: none;
  transition: transform 140ms ease, background-color 140ms ease, box-shadow 140ms ease;
}

.floating-badge:hover,
.floating-badge:focus-visible {
  background: transparent;
  box-shadow: none;
  outline: none;
  transform: translateY(-1px);
}

.floating-badge:active {
  cursor: grabbing;
  transform: scale(.96);
}

.floating-badge img {
  display: block;
  width: 100%;
  height: 100%;
  pointer-events: none;
}
</style>
