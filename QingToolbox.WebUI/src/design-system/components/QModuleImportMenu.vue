<script setup lang="ts">
import { nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import QButton from './QButton.vue'
import QIcon from './QIcon.vue'
import { useLocalization } from '../../localization/localization'
const props = defineProps<{ disabled?: boolean; importing?: boolean }>()
const emit = defineEmits<{ local: []; repository: [] }>()
const { t } = useLocalization()
const expanded = ref(false)
const root = ref<HTMLElement | null>(null)
function close(returnFocus = false) {
  expanded.value = false
  if (returnFocus) root.value?.querySelector<HTMLButtonElement>('.module-import-button')?.focus()
}
async function toggle() {
  expanded.value = !expanded.value
  if (expanded.value) { await nextTick(); root.value?.querySelector<HTMLButtonElement>('[role="menuitem"]')?.focus() }
}
function choose(kind: 'local' | 'repository') { close(); if (kind === 'local') emit('local'); else emit('repository') }
function outside(event: PointerEvent) { if (!root.value?.contains(event.target as Node)) close() }
function keydown(event: KeyboardEvent) {
  if (event.key === 'Escape' && expanded.value) { event.preventDefault(); event.stopPropagation(); close(true); return }
  if (event.key === 'Tab') { close(); return }
  if (['ArrowDown', 'ArrowUp'].includes(event.key)) {
    event.preventDefault()
    if (!expanded.value) { void toggle(); return }
    const buttons = [...root.value!.querySelectorAll<HTMLButtonElement>('[role="menuitem"]')]
    const current = buttons.indexOf(document.activeElement as HTMLButtonElement)
    buttons[(current + (event.key === 'ArrowDown' ? 1 : -1) + buttons.length) % buttons.length]?.focus()
  }
}
onMounted(() => document.addEventListener('pointerdown', outside))
onBeforeUnmount(() => document.removeEventListener('pointerdown', outside))
watch(() => props.disabled || props.importing, disabled => { if (disabled) close() })
</script>
<template>
  <div ref="root" class="q-module-import" @keydown="keydown" @focusout="event => { if (!root?.contains(event.relatedTarget as Node)) close() }">
    <QButton class="module-import-button" variant="primary" :disabled="disabled || importing" :aria-busy="!!importing" aria-haspopup="menu" :aria-expanded="expanded" @click="toggle">
      <span v-if="importing" class="module-operation-spinner" aria-hidden="true" /><QIcon v-else name="import" />
      {{ t(importing ? 'modules.page.importing' : 'modules.page.import') }}
      <svg class="import-chevron" :class="{ expanded }" viewBox="0 0 16 16" fill="none" stroke="currentColor" aria-hidden="true"><path d="m4 6 4 4 4-4" /></svg>
    </QButton>
    <Transition name="import-menu">
      <div v-if="expanded" class="module-import-menu" role="menu" :aria-label="t('modules.page.import')">
        <button role="menuitem" class="module-import-local" @click="choose('local')"><QIcon name="folder" />{{ t('modules.repository.local') }}</button>
        <button role="menuitem" class="module-import-repository" @click="choose('repository')"><QIcon name="download" />{{ t('modules.repository.open') }}</button>
      </div>
    </Transition>
  </div>
</template>
<style scoped>
.q-module-import { position: relative; display: inline-flex; text-align: left; z-index: 5; }
.import-chevron { width: 16px; height: 16px; transition: transform 160ms ease; }
.import-chevron.expanded { transform: rotate(180deg); }
.module-import-menu { position: absolute; right: 0; top: calc(100% + 8px); min-width: 190px; padding: 5px; border: 1px solid var(--q-border); border-radius: 12px; background: var(--q-surface); box-shadow: 0 12px 32px color-mix(in srgb,var(--q-text) 12%,transparent); transform-origin: top right; }
.module-import-menu button { display: flex; width: 100%; align-items: center; gap: 10px; padding: 11px 12px; border: 0; border-radius: 8px; background: transparent; color: var(--q-text); font: inherit; cursor: pointer; text-align: left; }
.module-import-menu button:hover,.module-import-menu button:focus-visible { background: var(--q-hover); color: var(--q-brand); }
.import-menu-enter-active,.import-menu-leave-active { transition: opacity 140ms ease,transform 180ms cubic-bezier(.2,.8,.2,1); }
.import-menu-enter-from,.import-menu-leave-to { opacity: 0; transform: translateY(-5px) scale(.97); }
@media(prefers-reduced-motion:reduce) { .import-chevron,.import-menu-enter-active,.import-menu-leave-active { transition:none; } }
</style>
