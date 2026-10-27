// Private module profile, private folder-only Everything instance, no UAC or
// changes to the user's launcher/Desktop/index. Simulates a next-day restart.
import assert from 'node:assert/strict'
import { spawn } from 'node:child_process'
import { once } from 'node:events'
import { createInterface } from 'node:readline'
import { mkdtemp, mkdir, writeFile, stat, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { resolve, join, relative, isAbsolute } from 'node:path'
import { createHash } from 'node:crypto'
import { resolveHostRoot } from './host-ui.mjs'
const directory = resolve(process.argv[2] ?? join(resolveHostRoot(), 'QingToolbox.Tauri/src-tauri/resources/modules/qing.launcher'))
const data = await mkdtemp(join(tmpdir(), 'qing-launcher-startup-'))
const index = join(data, 'index'); await mkdir(index)
const target = join(index, 'fixture.exe'); await writeFile(target, 'synthetic icon target; never executed')
await Promise.all(Array.from({ length: 250 }, (_, i) => writeFile(join(index, `warm-${String(i).padStart(3, '0')}.txt`), 'fixture')))
await writeFile(join(data, 'launcher.json'), JSON.stringify({ sortMode: 'custom', items: [{ id: 'fixture', name: 'Fixture', target, arguments: '', workingDirectory: index, source: 'custom' }], folders: [], customOrder: ['fixture'], cacheEnabled: true }))
const info = await stat(target, { bigint: true })
const key = `icon-detail-v2:${target.replaceAll('/', '\\').toLowerCase()}:${info.size}:${info.mtimeNs}`
const cache = join(data, 'launcher-cache-v1', 'icons'); await mkdir(cache, { recursive: true })
const url = 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg=='
await writeFile(join(cache, createHash('sha256').update(key).digest('hex') + '.json'), JSON.stringify({ created: Math.floor(Date.now() / 1000) - 86400, value: url }))
const latencies = []
async function cycle(round) {
  const child = spawn(join(directory, 'bin/qing-launcher-module.exe'), [], { cwd: directory, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'], env: {
    ...process.env, QINGTOOLBOX_MODULE_ID: 'qing.launcher', QINGTOOLBOX_MODULE_NONCE: 'startup-test', QINGTOOLBOX_MODULE_DIRECTORY: directory,
    QINGTOOLBOX_MODULE_DATA_DIR: data, QING_LAUNCHER_EVERYTHING_SERVICE: '0', QING_LAUNCHER_EVERYTHING_INDEX_ROOTS: index,
  } })
  const pending = new Map(); let sequence = 0; let logs = ''
  child.stderr.on('data', b => { logs = (logs + b).slice(-16384) })
  const lines = createInterface({ input: child.stdout })
  lines.on('line', line => { const v = JSON.parse(line); pending.get(v.requestId)?.(v) })
  function request(messageType, payload, timeout = 30000) {
    return new Promise((done, reject) => {
      const requestId = String(++sequence)
      const timer = setTimeout(() => { pending.delete(requestId); reject(new Error(`${messageType} timed out; ${logs}`)) }, timeout)
      pending.set(requestId, value => { clearTimeout(timer); pending.delete(requestId); done(value) })
      child.stdin.write(JSON.stringify({ protocolVersion: 1, messageType, requestId, payload }) + '\n')
    })
  }
  const invoke = (method, payload = {}) => request('module.invoke.request', { method, payload })
  async function finish(frame) {
    while (frame.payload?.launcherTask) { await new Promise(r => setTimeout(r, 20)); frame = await invoke('pollBackgroundTask', { taskId: frame.payload.launcherTask }) }
    assert.equal(frame.error, undefined, JSON.stringify(frame)); return frame.payload
  }
  try {
    const start = performance.now()
    assert.equal((await request('module.hello.request', { moduleId: 'qing.launcher', nonce: 'startup-test' })).error, undefined)
    assert(performance.now() - start < 1500, 'hello is not held by cache/index/providers')
    assert.equal((await invoke('getState')).payload.items[0].name, 'Fixture')
    assert.equal((await finish(await invoke('getItemIcon', { id: 'fixture' }))).dataUrl, url, '24-hour-old persisted icon reused before enable')
    await request('module.lifecycle.request', { active: true })
    const queued = await invoke('searchEverything', { mode: 'everything-file', query: 'warm-*.txt', requestId: `round-${round}` })
    assert.match(queued.payload.launcherTask, /^launcher-task-/)
    for (let i = 0; i < 12; i++) {
      const start = performance.now()
      assert.equal((await invoke('getState')).error, undefined)
      latencies.push(performance.now() - start)
    }
    assert.equal((await invoke('setSortMode', { mode: 'alphabetical' })).error, undefined, 'navigation while index initializes')
    let results = await finish(queued)
    const until = Date.now() + 30000
    while (results.status !== 'ready' && Date.now() < until) {
      await new Promise(r => setTimeout(r, 100))
      results = await finish(await invoke('searchEverything', { mode: 'everything-file', query: 'warm-*.txt', requestId: `retry-${round}` }))
    }
    assert.equal(results.status, 'ready', JSON.stringify(results))
    assert.equal(results.results.length, 200)
    await request('module.lifecycle.request', { active: false })
    await request('module.lifecycle.request', { active: true })
    assert.equal((await invoke('getState')).error, undefined)
    await request('module.shutdown.request', {})
    child.stdin.end()
    if (child.exitCode === null) await once(child, 'exit')
    assert.equal(child.exitCode, 0)
    assert.match(logs, /icon cache warmed: 1 assets/)
    assert.match(logs, /Everything query cache warmed:/)
    return results.results[0].id
  } finally {
    lines.close()
    if (child.exitCode === null) { child.kill(); await once(child, 'exit') }
  }
}
try {
  const first = await cycle(1), next = await cycle(2)
  assert.notEqual(first, next, 'restart never reuses cached result capability IDs')
  assert(Math.max(...latencies) < 500, 'state requests remain responsive during indexing')
  console.log(`Startup regression passed: pre-enable cache warm; 24-hour-old icons reused across two processes; live index/search, disable/re-enable, fresh IDs; max state latency ${Math.max(...latencies).toFixed(1)} ms.`)
} finally {
  const within = relative(resolve(tmpdir()), resolve(data))
  assert(within && !within.startsWith('..') && !isAbsolute(within))
  await rm(data, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 })
}
