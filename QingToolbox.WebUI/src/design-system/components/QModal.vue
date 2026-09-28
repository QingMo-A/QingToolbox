<script setup lang="ts">
import { nextTick, ref, useId, watch } from 'vue'

const props = withDefaults(defineProps<{ open: boolean; title: string; busy?: boolean; closeLabel?: string }>(), { busy: false, closeLabel: 'Close dialog' })
const emit = defineEmits<{ close: [] }>()
const titleId = useId()
const panel = ref<HTMLElement | null>(null)
let previousFocus: HTMLElement | null = null

watch(() => props.open, async open => {
  if (open) {
    previousFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null
    await nextTick()
    panel.value?.focus()
  } else {
    previousFocus?.focus()
    previousFocus = null
  }
}, { immediate: true })

function requestClose() {
  if (!props.busy) emit('close')
}

function onKeydown(event: KeyboardEvent) {
  if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); requestClose(); return }
  if (event.key !== 'Tab' || !panel.value) return
  const focusable = [...panel.value.querySelectorAll<HTMLElement>('button:not([disabled]),input:not([disabled]),textarea:not([disabled]),select:not([disabled]),a[href],[tabindex]:not([tabindex="-1"])')]
    .filter(element => element.getClientRects().length > 0)
  if (focusable.length === 0) { event.preventDefault(); panel.value.focus(); return }
  const first = focusable[0]
  const last = focusable[focusable.length - 1]
  if (event.shiftKey && (document.activeElement === first || document.activeElement === panel.value)) { event.preventDefault(); last.focus() }
  else if (!event.shiftKey && (document.activeElement === last || document.activeElement === panel.value)) { event.preventDefault(); first.focus() }
}
</script>

<template>
  <Teleport to="body">
    <Transition name="q-modal">
      <div v-if="open" class="q-modal-layer" @click.self="requestClose" @keydown="onKeydown">
        <section ref="panel" class="q-modal-card" tabindex="-1" role="dialog" aria-modal="true" :aria-labelledby="titleId" :aria-busy="busy || undefined">
          <header class="q-modal-header"><h2 :id="titleId">{{ title }}</h2><button type="button" class="q-modal-close" :disabled="busy" :aria-label="closeLabel" @click="requestClose">×</button></header>
          <div class="q-modal-content"><slot /></div>
          <footer v-if="$slots.actions" class="q-modal-actions"><slot name="actions" /></footer>
        </section>
      </div>
    </Transition>
  </Teleport>
</template>

<style scoped>
.q-modal-layer{position:fixed;inset:0;z-index:1200;display:grid;place-items:center;padding:22px;background:color-mix(in srgb,var(--q-text) 26%,transparent);backdrop-filter:blur(5px)}
.q-modal-card{width:min(100%,440px);max-height:min(82vh,640px);overflow:auto;border:1px solid var(--q-border);border-radius:18px;background:var(--q-surface);color:var(--q-text);box-shadow:0 24px 70px rgba(12,28,58,.22);outline:none}
.q-modal-header{display:flex;align-items:center;justify-content:space-between;gap:14px;padding:19px 21px 0}.q-modal-header h2{margin:0;font-size:18px;font-weight:700;letter-spacing:-.02em}.q-modal-close{width:30px;height:30px;border:1px solid transparent;border-radius:9px;background:transparent;color:var(--q-text-2);font-size:23px;line-height:1;cursor:pointer}.q-modal-close:hover:not(:disabled){border-color:var(--q-border);background:var(--q-surface-soft);color:var(--q-text)}.q-modal-close:disabled{opacity:.45;cursor:default}
.q-modal-content{display:grid;gap:12px;padding:16px 21px 20px}.q-modal-actions{display:flex;justify-content:flex-end;flex-wrap:wrap;gap:9px;padding:13px 21px 19px;border-top:1px solid var(--q-border)}
.q-modal-enter-active,.q-modal-leave-active{transition:opacity 180ms ease}.q-modal-enter-active .q-modal-card,.q-modal-leave-active .q-modal-card{transition:opacity 180ms ease,transform 220ms cubic-bezier(.2,.8,.2,1)}.q-modal-enter-from,.q-modal-leave-to{opacity:0}.q-modal-enter-from .q-modal-card,.q-modal-leave-to .q-modal-card{opacity:0;transform:translateY(9px) scale(.98)}
@media(prefers-reduced-motion:reduce){.q-modal-enter-active,.q-modal-leave-active,.q-modal-enter-active .q-modal-card,.q-modal-leave-active .q-modal-card{transition:none}}
</style>
