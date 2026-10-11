<script setup lang="ts">
import type { RichLine } from '../richText'
defineProps<{ line: RichLine; wrap?: boolean }>()
</script>

<template>
  <div class="rich-line" :class="{ wrapping: wrap && !line.right.length }">
    <span v-for="(runs, side) in { left: line.left, right: line.right }" :key="side" class="rich-side" :class="[side, { empty: !runs.length }]">
      <span v-for="(run, i) in runs" :key="i" class="rich-run" :class="run.style">
        <template v-if="run.kind === 'text'">{{ run.text }}</template>
        <svg v-else class="inline-icon" viewBox="0 0 14 14" aria-hidden="true">
          <template v-if="run.name === 'clock'"><circle cx="7" cy="7" r="5.7" /><path d="M7 7V4M7 7l2.2 1.1" /></template>
          <circle v-else cx="7" cy="7" r="3.7" fill="currentColor" stroke="none" />
        </svg>
      </span>
    </span>
  </div>
</template>

<style scoped>
.rich-line { display: flex; align-items: center; width: 100%; min-width: 0; font-weight: 400; line-height: 1.45; gap: .65em; }
.rich-side { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: pre; }
.left { flex: 1 1 auto; }
.right { flex: 0 1 auto; max-width: 60%; margin-left: auto; text-align: right; }
.empty { display: none; }
.left.empty + .right { max-width: 100%; }
.wrapping .rich-side { white-space: pre-wrap; overflow-wrap: anywhere; }
.rich-run.bold { font-weight: 600; }
.rich-run.italic { font-style: italic; }
.rich-run.strike { text-decoration: line-through; }
.rich-run.code { font-family: Consolas, monospace; }
.inline-icon { display: inline-block; width: calc(1em + 2px); height: 1em; vertical-align: -.12em; color: var(--island-accent, var(--q-brand)); fill: none; stroke: currentColor; stroke-width: 1.35; stroke-linecap: round; }
</style>
