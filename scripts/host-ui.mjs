import { existsSync, realpathSync } from 'node:fs'
import { resolve, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'
import { execFileSync } from 'node:child_process'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
export function resolveHostRoot() {
  let selected = process.env.QINGTOOLBOX_HOST_ROOT
  if (!selected) {
    const listing = execFileSync('git', ['-C', root, 'worktree', 'list', '--porcelain'], { encoding: 'utf8' })
    const host = listing.split(/\r?\n\r?\n/).find(block => block.split(/\r?\n/).includes('branch refs/heads/toolbox'))
    selected = host?.split(/\r?\n/).find(line => line.startsWith('worktree '))?.slice(9)
  }
  if (!selected) throw new Error('Set QINGTOOLBOX_HOST_ROOT to the compatible toolbox checkout containing the shared module UI.')
  const host = realpathSync(resolve(root, selected))
  if (!existsSync(resolve(host, 'QingToolbox.WebUI/src/design-system/module/index.ts')))
    throw new Error(`Shared module UI is missing in ${host}; use the migrated toolbox host, not the old WPF SDK.`)
  return host
}

export function moduleUiAliases() {
  const shared = resolve(resolveHostRoot(), 'QingToolbox.WebUI/src/design-system/module')
  return [
    { find: '@qingtoolbox/module-ui/module.css', replacement: resolve(shared, 'module.css') },
    { find: '@qingtoolbox/module-ui', replacement: resolve(shared, 'index.ts') },
  ]
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) console.log(resolveHostRoot())
