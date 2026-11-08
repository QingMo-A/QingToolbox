<script setup lang="ts">
import { computed } from 'vue'
import { colors } from './model'
const props=defineProps<{color:string}>()
const emit=defineEmits<{change:[color:string]}>()
const rgb=computed(()=>[1,3,5].map(i=>parseInt(props.color.slice(i,i+2),16)))
function channel(index:number,event:Event){const v=Number((event.target as HTMLInputElement).value);if(!Number.isFinite(v))return;const n=[...rgb.value];n[index]=Math.max(0,Math.min(255,Math.round(v)));emit('change','#'+n.map(v=>v.toString(16).padStart(2,'0')).join(''))}
</script>
<template><div class="palette"><div class="swatches"><button v-for="c in colors" :key="c" :title="c" :aria-label="`选择颜色 ${c}`" :aria-pressed="color.toLowerCase()===c" :class="{chosen:color.toLowerCase()===c}" :style="{'--swatch':c}" @click="emit('change',c)" /></div><label class="color-picker" title="调色盘"><input type="color" :value="color" aria-label="调色盘" @input="emit('change',($event.target as HTMLInputElement).value)" /><span>调色盘</span></label><div class="rgb"><label v-for="(v,i) in rgb" :key="i">{{['R','G','B'][i]}}<input type="number" min="0" max="255" :value="v" :aria-label="['R','G','B'][i]" @change="channel(i,$event)" /></label></div></div></template>
