import assert from 'node:assert/strict'
import { spawn } from 'node:child_process'
import { once } from 'node:events'
import { mkdtemp, readFile, readdir, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join, resolve, relative, isAbsolute } from 'node:path'
import { createInterface } from 'node:readline'

const root = resolve(process.argv[2] ?? 'QingToolbox.Tauri/src-tauri/resources/modules')
for (const entry of await readdir(root, { withFileTypes: true })) {
  if (!entry.isDirectory()) continue
  const directory = join(root, entry.name)
  const manifest = JSON.parse(await readFile(join(directory, 'module.json'), 'utf8'))
  const data = await mkdtemp(join(tmpdir(), 'qing-lifecycle-'))
  const child = spawn(join(directory, manifest.entry), [], { cwd: directory, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'], env: {
    ...process.env, QINGTOOLBOX_MODULE_ID: manifest.id, QINGTOOLBOX_MODULE_NONCE: 'lifecycle-test',
    QINGTOOLBOX_MODULE_DIRECTORY: directory, QINGTOOLBOX_MODULE_DATA_DIR: data,
    QING_LAUNCHER_EVERYTHING_SERVICE: '0', QINGTOOLBOX_TRANSFER_NAME: 'Qing lifecycle test',
  } })
  let sequence = 0
  const pending = new Map()
  const lines = createInterface({ input: child.stdout })
  lines.on('line', line => { const frame = JSON.parse(line); pending.get(frame.requestId)?.(frame) })
  child.stderr.resume()
  const request = (messageType, payload) => new Promise((resolve, reject) => {
    const requestId = String(++sequence)
    const timer = setTimeout(() => { pending.delete(requestId); reject(new Error(`${manifest.id}: ${messageType} timeout`)) }, 15000)
    pending.set(requestId, response => { clearTimeout(timer); pending.delete(requestId); resolve(response) })
    child.stdin.write(JSON.stringify({ protocolVersion: 1, messageType, requestId, payload }) + '\n')
  })
  const invoke = (method, payload = {}) => request('module.invoke.request', { method, payload })
  try {
    const hello = await request('module.hello.request', { moduleId: manifest.id, nonce: 'lifecycle-test' })
    assert.equal(hello.payload.lifecycleVersion, 1)
    const method = manifest.id === 'qing.canary' ? 'ping' : 'getState'
    const initial = await invoke(method)
    assert.equal(initial.error, undefined)
    if (manifest.id === 'qing.qingtransfer') assert.equal(initial.payload.discovery.running, false)
    if (manifest.id === 'qing.powerguard') assert.equal(initial.payload.settings.guardEnabled, false)
    if (manifest.id === 'qing.texttools') await invoke('setInput', { text: 'resident unsaved text' })
    const pid = child.pid
    for (const active of [true, true, false, false, true, false]) {
      const result = await request('module.lifecycle.request', { active })
      assert.equal(result.messageType, 'module.lifecycle.response')
      assert.equal(result.error, undefined)
      assert.equal(result.payload.active, active)
      assert.equal(child.pid, pid)
      assert.equal(child.exitCode, null)
      const state = await invoke(method)
      assert.equal(state.error, undefined)
      if (manifest.id === 'qing.qingtransfer') assert.equal(state.payload.discovery.running, active)
      if (manifest.id === 'qing.texttools') assert.equal(state.payload.input, 'resident unsaved text')
    }
    if (manifest.id === 'qing.qingtransfer') {
      assert.equal((await invoke('refresh')).error.code, 'module_inactive')
      assert.equal((await invoke('getState')).payload.discovery.running, false)
    }
    if (manifest.id === 'qing.launcher') {
      assert.equal((await invoke('searchEverything', { mode: 'everything-all', query: 'test', requestId: 'test' })).error.code, 'module_inactive')
    }
    await request('module.shutdown.request', {})
    child.stdin.end()
    if (child.exitCode === null) await Promise.race([once(child, 'exit'), new Promise((_, reject) => setTimeout(() => reject(new Error('shutdown timeout')), 5000).unref())])
    assert.equal(child.exitCode, 0)
    console.log(`${manifest.id}: load inactive; enable/disable/re-enable retained PID and memory; shutdown passed`)
  } finally {
    lines.close()
    if (child.exitCode === null) { child.kill(); await once(child, 'exit') }
    const withinTemp = relative(resolve(tmpdir()), resolve(data))
    assert(withinTemp && !withinTemp.startsWith('..') && !isAbsolute(withinTemp))
    await rm(data, { recursive: true, force: true })
  }
}
