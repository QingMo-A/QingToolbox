<script setup lang="ts">
withDefaults(defineProps<{ modelValue: string; label: string; placeholder?: string; disabled?: boolean; type?: 'text' | 'password' }>(), { type: 'text', disabled: false })
const emit = defineEmits<{ 'update:modelValue': [value: string] }>()
</script>
<template>
  <label class="q-modal-field"><span>{{ label }}</span><input :type="type" :value="modelValue" :placeholder="placeholder" :disabled="disabled" @input="emit('update:modelValue', ($event.target as HTMLInputElement).value)" /></label>
</template>
<style scoped>.q-modal-field{display:grid;gap:7px;color:var(--q-text);font-size:12px;font-weight:650}.q-modal-field input{height:39px;padding:0 11px;border:1px solid var(--q-border);border-radius:9px;background:var(--q-surface-soft);color:var(--q-text);font:inherit;font-weight:400}.q-modal-field input:focus-visible{outline:2px solid color-mix(in srgb,var(--q-brand) 45%,transparent);outline-offset:1px}.q-modal-field input:disabled{opacity:.55}</style>
