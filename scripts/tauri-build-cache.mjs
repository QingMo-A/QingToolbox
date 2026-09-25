// Local development cache only. Release manifests keep their Git provenance.
import { createHash } from 'node:crypto'
import { readFileSync, readdirSync, existsSync, mkdirSync, renameSync, writeFileSync } from 'node:fs'
import { resolve, dirname, relative } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const [action, scope, expected] = process.argv.slice(2)
const modules = {
  canary: ['native-module-canary', 'qing.canary'],
  launcher: ['native-launcher', 'qing.launcher'],
  pdf: ['native-pdf', 'qing.pdf'],
  transfer: ['native-transfer', 'qing.qingtransfer'],
  texttools: ['native-texttools', 'qing.texttools'],
  windowtopmost: ['native-windowtopmost', 'qing.windowtopmost'],
  powerguard: ['native-powerguard', 'qing.powerguard'],
  screenpin: ['native-screenpin', 'qing.screenpin'],
}
if (!['fingerprint', 'check', 'save'].includes(action) || (scope !== 'host' && !modules[scope])) {
  throw new Error('usage: tauri-build-cache.mjs fingerprint|check|save host|<module> [pre-build fingerprint]')
}
const excluded = new Set(['node_modules', 'target', 'dist', '.git', 'coverage'])
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
      if (source && (excluded.has(child.name) || name === 'QingToolbox.Tauri/src-tauri' && child.name === 'resources')) continue
      visit(resolve(path, child.name))
    }
  }
  paths.forEach(path => visit(resolve(root, path)))
  return hash.digest('hex')
}
const common = ['scripts/tauri-build-cache.mjs', '.cargo', 'rust-toolchain.toml']
const inputs = scope === 'host'
  ? ['QingToolbox.Tauri', 'QingToolbox.Tauri/src-tauri/resources/modules', 'QingToolbox.WebUI', ...readdirSync(resolve(root, 'scripts')).filter(n => /^(build-tauri|tauri-packaging)/.test(n)).map(n => `scripts/${n}`), 'LICENSE', 'THIRD_PARTY_NOTICES.md', ...common]
  : [`QingToolbox.Tauri/${modules[scope][0]}`, `scripts/build-tauri-${scope}.ps1`, ...common]
const output = scope === 'host'
  ? 'artifacts/tauri-production/QingToolbox'
  : `QingToolbox.Tauri/src-tauri/resources/modules/${modules[scope][1]}`
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
