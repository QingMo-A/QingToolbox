// Reuse an intact, locked npm tree instead of deleting binaries held by Vite.
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { pathToFileURL } from 'node:url'
import { spawnSync } from 'node:child_process'
import { isDeepStrictEqual } from 'node:util'

export function matchesInstalledLock(manifest, lock, installed) {
  const root = lock.packages?.['']
  if (!root || !installed.packages || lock.lockfileVersion !== installed.lockfileVersion) return false
  for (const kind of ['dependencies', 'devDependencies', 'optionalDependencies']) {
    if (!isDeepStrictEqual(manifest[kind] ?? {}, root[kind] ?? {})) return false
  }
  for (const [path, entry] of Object.entries(installed.packages)) {
    const expected = lock.packages[path]
    if (!expected || ['version', 'resolved', 'integrity', 'link'].some(key => entry[key] !== expected[key])) return false
  }
  return Object.keys(installed.packages).length > 0
}

export function dependenciesCurrent(root) {
  try {
    const read = path => JSON.parse(readFileSync(resolve(root, path), 'utf8'))
    const lock = read('package-lock.json')
    const installed = read('node_modules/.package-lock.json')
    if (!matchesInstalledLock(read('package.json'), lock, installed)) return false
    for (const [path, entry] of Object.entries(installed.packages)) {
      if (read(`${path}/package.json`).version !== entry.version) return false
    }
    // npm checks missing/invalid transitive dependencies, including platform
    // optional dependencies. No install or filesystem mutation occurs here.
    const result = spawnSync('npm ls --all --json', {
      cwd: root, encoding: 'utf8', windowsHide: true, shell: true, timeout: 30000,
    })
    return result.status === 0
  } catch { return false }
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  process.exitCode = process.argv[2] && dependenciesCurrent(resolve(process.argv[2])) ? 0 : 1
}
