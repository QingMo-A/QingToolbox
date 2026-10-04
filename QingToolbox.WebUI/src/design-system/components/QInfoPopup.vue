<script setup lang="ts">
import QButton from './QButton.vue'
defineProps<{ deviceName: string; appName: string; title: string; label: string; buttonLabel: string; buttonDisabled?: boolean }>()
defineEmits<{ action: [] }>()
</script>

<template>
  <section class="q-info-popup" role="status" aria-live="polite">
    <div class="q-info-popup-accent" aria-hidden="true" />
    <div class="q-info-popup-content">
      <div class="q-info-popup-source">
        <strong class="q-info-popup-device">{{ deviceName }}</strong>
        <span class="q-info-popup-app">{{ appName }}</span>
      </div>
      <h2 class="q-info-popup-title">{{ title }}</h2>
      <p class="q-info-popup-label">{{ label }}</p>
      <div class="q-info-popup-actions"><QButton :disabled="buttonDisabled" @click="$emit('action')">{{ buttonLabel }}</QButton></div>
    </div>
  </section>
</template>

<style scoped>
.q-info-popup { display: flex; width: 100%; height: 100%; overflow: hidden; border: 1px solid var(--q-border); border-radius: 18px; background: var(--q-surface); color: var(--q-text); box-shadow: inset 0 1px 0 color-mix(in srgb, var(--q-border) 30%, transparent); }
.q-info-popup-accent { width: 4px; flex: none; background: linear-gradient(180deg, var(--q-brand), var(--q-brand-soft)); }
.q-info-popup-content { display: flex; flex-direction: column; min-width: 0; flex: 1; padding: 12px 14px 10px 15px; }
.q-info-popup-source { display: flex; align-items: center; gap: 8px; min-width: 0; }
.q-info-popup-device { overflow: hidden; color: var(--q-brand); font-size: 16px; font-weight: 750; line-height: 1.35; text-overflow: ellipsis; white-space: nowrap; }
.q-info-popup-app { overflow: hidden; max-width: 125px; padding: 2px 7px; border-radius: 6px; background: var(--q-brand-soft); color: var(--q-brand); font-size: 11px; font-weight: 650; text-overflow: ellipsis; white-space: nowrap; }
.q-info-popup-title { overflow: hidden; margin: 8px 0 0; font-size: 15px; font-weight: 700; line-height: 1.35; text-overflow: ellipsis; white-space: nowrap; }
.q-info-popup-label { display: -webkit-box; overflow: hidden; margin: 3px 0 0; color: var(--q-text-2); font-size: 13px; line-height: 1.4; white-space: pre-wrap; overflow-wrap: anywhere; -webkit-box-orient: vertical; -webkit-line-clamp: 2; }
.q-info-popup-actions { display: flex; justify-content: flex-end; margin-top: auto; }
.q-info-popup-actions :deep(.q-button) { min-height: 30px; padding: 0 11px; font-size: 12px; }
</style>
