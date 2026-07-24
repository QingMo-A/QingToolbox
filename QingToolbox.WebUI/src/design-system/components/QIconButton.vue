<script setup lang="ts">defineProps<{label:string}>()</script><template><button class="q-icon-button" :aria-label="label" :title="label"><slot/></button></template>
