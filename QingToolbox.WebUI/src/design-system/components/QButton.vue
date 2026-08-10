<script setup lang="ts">withDefaults(defineProps<{variant?:'primary'|'secondary'|'danger'|'ghost';disabled?:boolean}>(),{variant:'secondary'})</script>
<template><button class="q-button" :class="`is-${variant}`" :disabled="disabled"><slot/></button></template>
