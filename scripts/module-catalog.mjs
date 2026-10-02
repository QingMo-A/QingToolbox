import { existsSync, lstatSync, readFileSync, realpathSync } from 'node:fs'
import { resolve, relative, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'

export const officialSource = 'https://raw.githubusercontent.com/QingMo-A/QingToolbox/modules/modules/'
export const profile = 'tauri-process-v1'
const fail = message => { throw new Error(message) }
const object = value => value && typeof value === 'object' && !Array.isArray(value)
const text = (value, max = 320) => typeof value === 'string' && value.trim().length > 0 && value.length <= max && !/[\u0000-\u0008\u000b\u000c\u000e-\u001f]/.test(value)
const keys = (value, allowed, required = allowed) => {
  if (!object(value) || Object.keys(value).some(key => !allowed.includes(key)) || required.some(key => !(key in value))) fail('Unexpected or missing catalog fields.')
}
export function semver(value) {
  if (typeof value !== 'string' || value.length > 96) fail('Invalid semantic version.')
  const match = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$/.exec(value)
  if (!match) fail(`Invalid semantic version: ${value}`)
  const pre = match[4]?.split('.') ?? []
  if (pre.some(part => /^0\d+$/.test(part))) fail('Leading zeros in prerelease version.')
  return { core: match.slice(1, 4).map(BigInt), pre }
}
export function compareVersions(left, right) {
  const a = semver(left), b = semver(right)
  for (let i = 0; i < 3; i++) if (a.core[i] !== b.core[i]) return a.core[i] > b.core[i] ? 1 : -1
  if (!a.pre.length || !b.pre.length) return !a.pre.length ? (b.pre.length ? 1 : 0) : -1
  for (let i = 0; i < Math.max(a.pre.length, b.pre.length); i++) {
    const x = a.pre[i], y = b.pre[i]
    if (x === undefined || y === undefined) return x === undefined ? -1 : 1
    if (x === y) continue
    const nx = /^\d+$/.test(x), ny = /^\d+$/.test(y)
    if (nx && ny) return BigInt(x) > BigInt(y) ? 1 : -1
    if (nx !== ny) return nx ? -1 : 1
    return x > y ? 1 : -1
  }
  return 0
}
function languages(value) {
  keys(value, ['zh-CN', 'en-US'])
  if (!Object.values(value).every(value => text(value, 4000))) fail('Both localized strings are required.')
}
function safePath(value, suffix) {
  if (!text(value, 240) || !/^[A-Za-z0-9._/-]+$/.test(value) || value.split('/').some(part => !part || part === '.' || part === '..') || !value.endsWith(suffix)) fail('Unsafe catalog path.')
  return value
}
function readJson(path) {
  const bytes = readFileSync(path)
  if (bytes.length > 256 * 1024) fail('Catalog JSON exceeds size limit.')
  return JSON.parse(bytes.toString('utf8').replace(/^\uFEFF/, ''))
}
function controlledFile(root, path) {
  const candidate = resolve(root, path)
  let cursor = candidate
  while (cursor !== root) {
    const rel = relative(root, cursor)
    if (rel.startsWith('..') || resolve(root, rel) !== cursor) fail('Catalog asset escapes module root.')
    if (lstatSync(cursor).isSymbolicLink()) fail('Catalog asset is a symbolic link.')
    cursor = dirname(cursor)
  }
  const canonical = realpathSync(candidate)
  if (relative(root, canonical).startsWith('..')) fail('Catalog asset escapes module root.')
  return candidate
}
export function validateUpdate(update, id) {
  keys(update, ['schemaVersion', 'moduleId', 'publisher', 'releases'])
  if (update.schemaVersion !== 2 || update.moduleId !== id || update.publisher !== 'QingMo-A' || !Array.isArray(update.releases) || update.releases.length > 64) fail('Invalid module update identity.')
  let previous
  for (const entry of update.releases) {
    keys(entry, ['version', 'channel', 'moduleProfile', 'apiVersion', 'platform', 'architecture', 'minimumHostVersion', 'maximumHostVersionExclusive', 'publishedAt', 'package', 'releaseNotes'])
    const version = semver(entry.version)
    if (!['preview', 'stable'].includes(entry.channel) || entry.channel === 'stable' && version.pre.length) fail('Invalid release channel.')
    if (entry.moduleProfile !== profile || !Number.isInteger(entry.apiVersion) || entry.apiVersion < 1 || entry.platform !== 'windows' || !['x64', 'arm64'].includes(entry.architecture)) fail('Invalid release compatibility.')
    semver(entry.minimumHostVersion)
    if (entry.maximumHostVersionExclusive !== null && compareVersions(entry.maximumHostVersionExclusive, entry.minimumHostVersion) <= 0) fail('Invalid host version range.')
    if (!/^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$/.test(entry.publishedAt) || !Number.isFinite(Date.parse(entry.publishedAt)) || new Date(entry.publishedAt).toISOString().replace('.000Z', 'Z') !== entry.publishedAt) fail('Invalid UTC publication timestamp.')
    if (previous && compareVersions(previous, entry.version) <= 0) fail('Release versions must be unique and descending.')
    previous = entry.version
    const pkg = entry.package
    keys(pkg, ['fileName', 'url', 'size', 'sha256'])
    if (pkg.fileName !== `${id}-${entry.version}-tauri.qmod` || !/^[A-Za-z0-9._+-]+\.qmod$/.test(pkg.fileName) || !Number.isSafeInteger(pkg.size) || pkg.size < 1 || pkg.size > 256 * 1024 * 1024 || !/^[a-f0-9]{64}$/.test(pkg.sha256)) fail('Invalid package identity or digest.')
    const url = new URL(pkg.url)
    const segments = url.pathname.split('/')
    if (url.protocol !== 'https:' || url.hostname !== 'github.com' || url.port || url.username || url.password || url.search || url.hash || /[%\\]/.test(pkg.url) || segments.length !== 7 || segments.slice(0, 5).join('/') !== '/QingMo-A/QingToolbox/releases/download' || segments[5] !== `modules-${id.slice(5)}-v${entry.version}` || segments[6] !== pkg.fileName) fail('Only fixed official GitHub Release assets are accepted.')
    languages(entry.releaseNotes)
  }
  return update
}
export function validateCatalog(root) {
  root = realpathSync(root)
  const index = readJson(controlledFile(root, 'index.json'))
  keys(index, ['schemaVersion', 'sourceId', 'moduleProfile', 'modules'])
  if (index.schemaVersion !== 2 || index.sourceId !== 'qingtoolbox-official-tauri' || index.moduleProfile !== profile || !object(index.modules) || Object.keys(index.modules).length > 128) fail('Invalid native catalog identity.')
  const ids = Object.keys(index.modules)
  if (ids.join() !== [...ids].sort().join()) fail('Module identifiers must be sorted.')
  let releases = 0, publicModules = 0
  const paths = new Set()
  for (const id of ids) {
    if (!/^qing\.[a-z0-9][a-z0-9.-]{0,100}$/.test(id)) fail('Invalid module identifier.')
    const item = index.modules[id]
    keys(item, ['name', 'description', 'icon', 'moduleManifest', 'updateManifest', 'visibility'])
    languages(item.name); languages(item.description)
    if (!['public', 'development'].includes(item.visibility)) fail('Invalid module visibility.')
    if (item.visibility === 'public') publicModules++
    safePath(item.icon, '/icon.svg'); safePath(item.moduleManifest, '/module.json'); safePath(item.updateManifest, '/update.json')
    const directory = item.moduleManifest.slice(0, -'/module.json'.length)
    if (item.icon !== `${directory}/icon.svg` || item.updateManifest !== `${directory}/update.json` || paths.has(directory)) fail('Each module must own one unique directory.')
    paths.add(directory)
    controlledFile(root, item.icon)
    const manifest = readJson(controlledFile(root, item.moduleManifest))
    if (manifest.id !== id || manifest.runtimeType !== 'Process' || manifest.runtimeIsolation !== 'OutOfProcess' || manifest.apiVersion !== 1 || !/^bin\/[A-Za-z0-9-]+\.exe$/.test(manifest.entry)) fail('Source module identity/API/runtime is invalid.')
    semver(manifest.version)
    const cargo = readFileSync(controlledFile(root, `${directory}/Cargo.toml`), 'utf8')
    const packageSection = cargo.split(/\r?\n\[/)[0]
    if (/^version\s*=\s*"([^"]+)"$/m.exec(packageSection)?.[1] !== manifest.version) fail('Cargo and module versions differ.')
    if (existsSync(resolve(root, directory, 'ui-src/package.json')) && readJson(controlledFile(root, `${directory}/ui-src/package.json`)).version !== manifest.version) fail('UI and module versions differ.')
    const update = validateUpdate(readJson(controlledFile(root, item.updateManifest)), id)
    releases += update.releases.length
  }
  return { modules: ids.length, publicModules, releases }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const root = process.argv[2] ?? resolve(dirname(fileURLToPath(import.meta.url)), '../modules')
  console.log(JSON.stringify(validateCatalog(root)))
}
