// Controlled Everything integration: never changes the user's desktop/index.
import assert from 'node:assert/strict'
import { spawn } from 'node:child_process'
import { once } from 'node:events'
import { mkdtemp, mkdir, writeFile, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { resolve, join, relative, isAbsolute } from 'node:path'
import { createInterface } from 'node:readline'
import { resolveHostRoot } from './host-ui.mjs'

const directory = resolve(process.argv[2] ?? join(resolveHostRoot(), 'QingToolbox.Tauri/src-tauri/resources/modules/qing.launcher'))
const data = await mkdtemp(join(tmpdir(), 'qing-search-test-'))
const index = join(data, 'index')
await mkdir(index)
await Promise.all(Array.from({ length: 425 }, (_, i) => writeFile(join(index, `item-${String(i).padStart(4, '0')}.txt`), 'search fixture')))
const child = spawn(join(directory, 'bin/qing-launcher-module.exe'), [], { cwd: directory, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'], env: {
  ...process.env, QINGTOOLBOX_MODULE_ID: 'qing.launcher', QINGTOOLBOX_MODULE_NONCE: 'search-test',
  QINGTOOLBOX_MODULE_DIRECTORY: directory, QINGTOOLBOX_MODULE_DATA_DIR: data,
  QING_LAUNCHER_EVERYTHING_SERVICE: '0', QING_LAUNCHER_EVERYTHING_INDEX_ROOTS: index,
} })
const lines = createInterface({ input: child.stdout })
const pending = new Map()
let sequence = 0
let stderr = ''
child.stderr.on('data', bytes => { stderr = (stderr + bytes).slice(-8192) })
lines.on('line', line => { const frame = JSON.parse(line); pending.get(frame.requestId)?.(frame) })
const request = (messageType, payload, timeout = 60000) => new Promise((resolve, reject) => {
  const requestId = String(++sequence)
  const timer = setTimeout(() => { pending.delete(requestId); reject(new Error(`${messageType} timeout; ${stderr}`)) }, timeout)
  pending.set(requestId, frame => { clearTimeout(timer); pending.delete(requestId); resolve(frame) })
  child.stdin.write(JSON.stringify({ protocolVersion: 1, messageType, requestId, payload }) + '\n')
})
const invoke = (method, payload = {}) => request('module.invoke.request', { method, payload })
const search = () => invoke('searchEverything', { mode: 'everything-file', query: 'item-*.txt', requestId: `search-${sequence}` })
try {
  assert.equal((await request('module.hello.request', { moduleId: 'qing.launcher', nonce: 'search-test' })).error, undefined)
  assert.equal((await invoke('getState')).payload.everythingSettings.resultLimit, 200)
  await request('module.lifecycle.request', { active: true })
  let initial
  const readyUntil = Date.now() + 30000
  do {
    initial = await search()
    if (initial.payload?.results?.length === 200) break
    await new Promise(resolve => setTimeout(resolve, 100))
  } while (Date.now() < readyUntil)
  assert.equal(initial.payload.status, 'ready', JSON.stringify(initial))
  assert.equal(initial.payload.results.length, 200)
  assert.equal(initial.payload.nextCursor, null)
  assert.equal(initial.payload.limited, true)
  await invoke('setEverythingSettings', { resultLimit: 150, batchLoading: true })
  const first = await search()
  assert.equal(first.payload.results.length, 150)
  assert(first.payload.nextCursor)
  const second = await invoke('loadMoreEverything', { cursor: first.payload.nextCursor, requestId: 'second' })
  assert.equal(second.error, undefined)
  assert.equal(second.payload.results.length, 150)
  assert(second.payload.nextCursor)
  const third = await invoke('loadMoreEverything', { cursor: second.payload.nextCursor, requestId: 'third' })
  assert.equal(third.payload.results.length, 125)
  assert.equal(third.payload.nextCursor, null)
  const names = [first, second, third].flatMap(frame => frame.payload.results.map(result => result.name))
  assert.equal(new Set(names).size, 425)
  assert.deepEqual(names, [...names].sort())
  assert.equal((await invoke('loadMoreEverything', { cursor: first.payload.nextCursor })).error.code, 'everything_page_failed')
  const restart = await search()
  await invoke('searchEverything', { mode: 'everything-file', query: 'qing-no-match-b1cff389', requestId: 'new' })
  assert.equal((await invoke('loadMoreEverything', { cursor: restart.payload.nextCursor })).error.code, 'everything_page_failed')
  await invoke('setEverythingSettings', { resultLimit: 425, batchLoading: false })
  const all = await search()
  assert.equal(all.payload.results.length, 425)
  assert.equal(all.payload.nextCursor, null)
  assert.equal(all.payload.limited, false)
  await request('module.shutdown.request', {})
  child.stdin.end()
  if (child.exitCode === null) await Promise.race([once(child, 'exit'), new Promise((_, reject) => setTimeout(() => reject(new Error('shutdown timeout')), 5000).unref())])
  assert.equal(child.exitCode, 0)
  console.log('Everything integration passed: default 200; 150/150/125 native viewports; no duplicates; stale/replayed cursor rejection; configurable non-batched 425.')
} finally {
  if (child.exitCode === null) {
    try {
      await request('module.shutdown.request', {}, 5000)
      child.stdin.end()
      await Promise.race([once(child, 'exit'), new Promise(resolve => setTimeout(resolve, 3000).unref())])
    } catch { /* Fall back only for this test's own child. */ }
  }
  lines.close()
  if (child.exitCode === null) { child.kill(); await once(child, 'exit') }
  const withinTemp = relative(resolve(tmpdir()), resolve(data))
  assert(withinTemp && !withinTemp.startsWith('..') && !isAbsolute(withinTemp))
  try { await rm(data, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 }) }
  catch (error) { console.warn(`Temporary test cleanup deferred: ${error.code}`) }
}
