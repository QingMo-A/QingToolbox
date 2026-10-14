import { defineConfig } from 'vitest/config'
import vue from '@vitejs/plugin-vue'
import { moduleUiAliases } from '../../../scripts/host-ui.mjs'
export default defineConfig({ plugins: [vue()], resolve: { dedupe: ['vue', '@tauri-apps/api'], alias: moduleUiAliases() }, test: { environment: 'jsdom', include: ['src/**/*.test.ts'], maxWorkers: 1 } })
