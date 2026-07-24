<script setup lang="ts">
import{nextTick,ref,watch}from'vue';import QIconButton from'./QIconButton.vue'
const props=defineProps<{open:boolean;title:string}>();const emit=defineEmits<{close:[]}>();const panel=ref<HTMLElement|null>(null);let previous:HTMLElement|null=null
watch(()=>props.open,async open=>{if(open){previous=document.activeElement as HTMLElement;await nextTick();panel.value?.focus()}else previous?.focus()})
function key(event:KeyboardEvent){if(event.key==='Escape')emit('close')}
</script>
<template><Teleport to="body"><Transition name="drawer"><div v-if="open" class="q-drawer-layer" @keydown="key"><button class="q-drawer-backdrop" aria-label="Close details" @click="emit('close')"/><aside ref="panel" class="q-drawer" tabindex="-1" role="dialog" aria-modal="true" :aria-label="title"><header><h2>{{title}}</h2><QIconButton label="Close details" @click="emit('close')">×</QIconButton></header><slot/></aside></div></Transition></Teleport></template>
