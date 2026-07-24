import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import { sourceIdentity } from './tools/asset-identity.mjs'

export default defineConfig(async ({ command }) => {
  const buildId=(await sourceIdentity()).slice(0,32)
  return { base:'./',plugins:[vue()],define:{__QING_ASSET_BUILD_ID__:JSON.stringify(buildId)},build:{outDir:'dist',emptyOutDir:true,sourcemap:false},server:{headers:{'Content-Security-Policy':"default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self' ws:; object-src 'none'; base-uri 'none'"}},test:{environment:'jsdom'} }
})
