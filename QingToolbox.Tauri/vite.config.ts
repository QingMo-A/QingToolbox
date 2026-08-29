import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// Keep the dev server contract stable for `tauri dev` and browser preview.
export default defineConfig({
  plugins: [vue()],
  clearScreen: false,
  server: {
    port: 1420,
    strictPort: true,
    host: 'localhost',
  },
  // Tauri's WebView does not need an asset base prefix.
  base: './',
})
