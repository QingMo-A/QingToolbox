import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import { sourceIdentity } from './tools/asset-identity.mjs'

export default defineConfig(async ({ command }) => {
  const buildId=(await sourceIdentity()).slice(0,32)
  return {
    base: './',
    plugins: [vue(), {
      name: 'qing-development-csp',
      // Development needs Vite's style injection and WebSocket; production
      // retains the stricter policy in index.html and the Tauri host config.
      apply: 'serve' as const,
      transformIndexHtml: (html: string) => html.replace(/<meta http-equiv="Content-Security-Policy"[^>]*>/i, ''),
    }],
    define: { __QING_ASSET_BUILD_ID__: JSON.stringify(buildId) },
    build: { outDir: 'dist', emptyOutDir: true, sourcemap: false },
    server: { headers: { 'Content-Security-Policy': "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' qfont: http://qfont.localhost https://qfont.localhost; connect-src 'self' ws: ipc: http://ipc.localhost; object-src 'none'; base-uri 'none'" } },
    test: { environment: 'jsdom' },
  }
})
