// Shared UI remains host-owned. Resolve it for Vite and TypeScript without
// vendoring another component library into every independently built module.
import { readFileSync, writeFileSync, unlinkSync } from 'node:fs'
import { resolve } from 'node:path'
import { spawnSync } from 'node:child_process'
import { resolveHostRoot } from './host-ui.mjs'

const ui = process.cwd()
const original = JSON.parse(readFileSync(resolve(ui, 'tsconfig.json'), 'utf8'))
const config = resolve(ui, '.qing-host-tsconfig.json')
const sdk = resolve(resolveHostRoot(), 'QingToolbox.WebUI/src/design-system/module/index.ts')
const temporary = {
  extends: './tsconfig.json',
  compilerOptions: {
    paths: { ...original.compilerOptions?.paths, '@qingtoolbox/module-ui': [sdk] },
  },
}
function run(script, args) {
  const result = spawnSync(process.execPath, [resolve(ui, 'node_modules', script), ...args], { stdio: 'inherit' })
  if (result.error) throw result.error
  if (result.status !== 0) throw new Error(`${script} exited with ${result.status}`)
}
try {
  writeFileSync(config, JSON.stringify(temporary))
  run('vue-tsc/bin/vue-tsc.js', ['--noEmit', '-p', config])
  if (!process.argv.includes('--typecheck')) run('vite/bin/vite.js', ['build'])
} finally {
  try { unlinkSync(config) } catch (error) { if (error.code !== 'ENOENT') throw error }
}
