import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

export default defineConfig({
  plugins: [vue()],
  // Module windows are served below qmod://<module-id>/ui/. Relative asset
  // URLs keep the bundle inside that backend-owned route instead of falling
  // back to the host root.
  base: './',
  build: { outDir: 'dist', emptyOutDir: true },
})
