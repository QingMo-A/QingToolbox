// Local development cache only. Release manifests keep their Git provenance.
import { createHash } from 'node:crypto'
import { readFileSync, readdirSync, existsSync, mkdirSync, renameSync, writeFileSync } from 'node:fs'
import { resolve, dirname, relative } from 'node:path'
import { fileURLToPath } from 'node:url'
import { moduleSource } from './module-sources.mjs'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const [action, scope, expected] = process.argv.slice(2)
const modules = {
  canary: 'qing.canary',
  launcher: 'qing.launcher',
  pdf: 'qing.pdf',
  texttools: 'qing.texttools',
  windowtopmost: 'qing.windowtopmost',
  powerguard: 'qing.powerguard',
  screenpin: 'qing.screenpin',
}
if (!['fingerprint', 'check', 'save'].includes(action) || (scope !== 'host' && !modules[scope])) {
  throw new Error('usage: tauri-build-cache.mjs fingerprint|check|save host|<module> [pre-build fingerprint]')
}
const excluded = new Set(['node_modules', 'target', 'dist', '.git', 'coverage', '.qing-host-tsconfig.json', 'update.json'])
function fingerprint(paths, source) {
  const hash = createHash('sha256')
  function visit(path) {
    const name = relative(root, path).replaceAll('\\', '/')
    hash.update(name + '\0')
    if (!existsSync(path)) { hash.update('missing\0'); return }
    const entries = readdirSync(dirname(path), { withFileTypes: true })
    const entry = entries.find(entry => resolve(dirname(path), entry.name) === path)
    if (!entry || entry.isSymbolicLink()) throw new Error(`Unsupported cache input: ${path}`)
    if (!entry.isDirectory()) { hash.update(readFileSync(path)); hash.update('\0'); return }
    for (const child of readdirSync(path, { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name, 'en'))) {
      if (source && (excluded.has(child.name) ||
        scope !== 'host' && path === inputs[0] && child.name === 'packages' ||
        name === 'QingToolbox.Tauri/src-tauri' && child.name === 'resources' ||
        scope === 'host' && name === 'QingToolbox.Tauri' && child.name.startsWith('native-'))) continue
      visit(resolve(path, child.name))
    }
  }
  paths.forEach(path => visit(resolve(root, path)))
  return hash.digest('hex')
}
const common = ['scripts/tauri-build-cache.mjs', '.cargo', 'rust-toolchain.toml']
const inputs = scope === 'host'
  ? ['QingToolbox.Tauri', 'QingToolbox.WebUI', ...readdirSync(resolve(root, 'scripts')).filter(n => /^(build-tauri|tauri-packaging)/.test(n)).map(n => `scripts/${n}`), 'LICENSE', 'THIRD_PARTY_NOTICES.md', ...common]
  : [moduleSource(scope, root), `scripts/build-tauri-${scope}.ps1`, 'scripts/module-sources.mjs', 'scripts/module-sources.ps1', 'scripts/module-ui-dependencies.mjs', ...common]
if (scope !== 'host') {
  const sourceRoot = resolve(moduleSource(scope, root), '../..')
  inputs.push(resolve(sourceRoot, 'scripts/host-ui.mjs'), resolve(sourceRoot, 'scripts/host-ui.d.mts'), resolve(sourceRoot, 'scripts/build-module-ui.mjs'))
}
if (['launcher', 'pdf', 'texttools', 'windowtopmost', 'powerguard', 'screenpin'].includes(scope)) {
  // Only the shared module entry's dependencies, not unrelated shell pages
  // and components (which must not force all modules to rebuild).
  const shared = 'QingToolbox.WebUI/src/design-system'
  inputs.push(`${shared}/module`, `${shared}/styles`, `${shared}/tokens/tokens.css`, `${shared}/tokens/appearancePresets.css`,
    ...['QButton', 'QIconButton', 'QIcon', 'QModal', 'QModalInput', 'QModalLabel'].map(name => `${shared}/components/${name}.vue`))
}
const output = scope === 'host'
  ? 'artifacts/tauri-production/QingToolbox'
  : `QingToolbox.Tauri/src-tauri/resources/modules/${modules[scope]}`
const record = resolve(root, 'artifacts/tauri-build-cache', `${scope}.json`)
const sourceHash = fingerprint(inputs, true)
if (action === 'fingerprint') {
  console.log(sourceHash)
} else if (action === 'check') {
  try {
    const cached = JSON.parse(readFileSync(record, 'utf8'))
    if (cached.source !== sourceHash || !existsSync(resolve(root, output)) || cached.output !== fingerprint([output], false)) process.exitCode = 1
  } catch { process.exitCode = 1 }
} else {
  if (expected !== sourceHash) throw new Error('Source changed during build; cache not saved. Retry with a stable source tree.')
  if (!existsSync(resolve(root, output))) throw new Error('Build output is missing')
  mkdirSync(dirname(record), { recursive: true })
  const temp = `${record}.${process.pid}.tmp`
  writeFileSync(temp, JSON.stringify({ source: sourceHash, output: fingerprint([output], false) }))
  renameSync(temp, record)
}
