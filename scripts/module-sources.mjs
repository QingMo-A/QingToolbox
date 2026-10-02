import { existsSync, readFileSync, realpathSync } from 'node:fs'
import { resolve, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'
import { execFileSync } from 'node:child_process'

export const moduleDirectories = Object.freeze({
  canary: 'Canary', launcher: 'Launcher', pdf: 'QingPdf',
  texttools: 'TextTools', windowtopmost: 'WindowTopmost',
  powerguard: 'PowerGuard', screenpin: 'ScreenPin',
})
const hostRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..')

export function resolveModulesRoot(root = hostRoot, environment = process.env) {
  let selected = environment.QINGTOOLBOX_MODULES_ROOT
  if (!selected) {
    let listing
    try { listing = execFileSync('git', ['-C', root, 'worktree', 'list', '--porcelain'], { encoding: 'utf8' }) }
    catch { throw new Error('Set QINGTOOLBOX_MODULES_ROOT to a checkout of the modules branch.') }
    const item = listing.split(/\r?\n\r?\n/).find(block => block.split(/\r?\n/).includes('branch refs/heads/modules'))
    selected = item?.split(/\r?\n/).find(line => line.startsWith('worktree '))?.slice(9)
  }
  if (!selected) throw new Error('The modules worktree is missing; set QINGTOOLBOX_MODULES_ROOT.')
  const result = realpathSync(resolve(root, selected))
  const indexPath = resolve(result, 'modules/index.json')
  if (!existsSync(indexPath)) throw new Error(`Official module index missing: ${indexPath}`)
  const index = JSON.parse(readFileSync(indexPath, 'utf8').replace(/^\uFEFF/, ''))
  if (index.schemaVersion !== 2 || index.sourceId !== 'qingtoolbox-official-tauri' || index.moduleProfile !== 'tauri-process-v1')
    throw new Error('The modules checkout is not the Rust/Tauri module source. Do not use the WPF catalog.')
  return result
}

export function moduleSource(scope, root = hostRoot) {
  if (scope === 'transfer') return resolve(root, 'QingToolbox.Tauri/native-transfer')
  const directory = moduleDirectories[scope]
  if (!directory) throw new Error(`Unknown independent module: ${scope}`)
  return resolve(resolveModulesRoot(root), 'modules', directory)
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  console.log(process.argv[2] ? moduleSource(process.argv[2]) : resolveModulesRoot())
}
