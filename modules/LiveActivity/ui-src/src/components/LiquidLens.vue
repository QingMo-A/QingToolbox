<script setup lang="ts">
import { computed } from 'vue'
import { liquidMap } from '../material'

/**
 * An SVG filter that refracts what is behind a lens layer like the native
 * liquid glass: lensed outward at the rim, a hair of softening, and a touch
 * of dispersion (red bends further than blue). Apply it as
 * `backdrop-filter: url(#id)` to a layer `margin` px larger than the glass on
 * every side, clipped to the glass — a backdrop filter can only refract what
 * lies inside its own box, and the rim must reach beyond the glass.
 */
const props = defineProps<{ id: string; width: number; height: number; radius: number; scale: number; margin: number }>()
const map = computed(() => liquidMap(props.width, props.height, props.radius, props.scale, props.margin))
const size = computed(() => ({ w: Math.round(props.width) + props.margin * 2, h: Math.round(props.height) + props.margin * 2 }))
const soften = computed(() => Math.max(0.6, 1.2 * props.scale))
</script>

<template>
  <svg class="liquid-lens" width="0" height="0" aria-hidden="true" focusable="false">
    <filter :id="id" x="0" y="0" :width="size.w" :height="size.h" filterUnits="userSpaceOnUse" primitiveUnits="userSpaceOnUse" color-interpolation-filters="sRGB">
      <feImage :href="map.href" x="0" y="0" :width="size.w" :height="size.h" preserveAspectRatio="none" result="lens" />
      <feGaussianBlur in="SourceGraphic" :stdDeviation="soften" result="soft" />
      <feDisplacementMap in="soft" in2="lens" :scale="map.range * 1.12" xChannelSelector="R" yChannelSelector="G" result="bentRed" />
      <feDisplacementMap in="soft" in2="lens" :scale="map.range * 0.94" xChannelSelector="R" yChannelSelector="G" result="bentRest" />
      <feColorMatrix in="bentRed" values="1 0 0 0 0  0 0 0 0 0  0 0 0 0 0  0 0 0 1 0" result="red" />
      <feColorMatrix in="bentRest" values="0 0 0 0 0  0 1 0 0 0  0 0 1 0 0  0 0 0 1 0" result="greenBlue" />
      <feComposite in="red" in2="greenBlue" operator="arithmetic" k2="1" k3="1" />
    </filter>
  </svg>
</template>

<style scoped>
.liquid-lens { position: absolute; width: 0; height: 0; overflow: hidden; pointer-events: none; }
</style>
