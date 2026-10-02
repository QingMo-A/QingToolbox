import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import { moduleUiAliases } from '../../../scripts/host-ui.mjs'

export default defineConfig({
  base: './',
  plugins: [vue()],
  resolve: { dedupe: ['vue'], alias: moduleUiAliases() },
  build: { outDir: 'dist', emptyOutDir: true },
})
