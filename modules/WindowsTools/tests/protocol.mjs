// Read-only smoke test of the actual native executable. Never confirms a valid
// privileged action and never registers a real file association.
import { spawn } from 'node:child_process'
import { createInterface } from 'node:readline'
import { resolve, dirname, basename } from 'node:path'
import { mkdtempSync, writeFileSync, readFileSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import assert from 'node:assert/strict'

const binary = process.argv[2] || resolve(import.meta.dirname, '../target/release/qing-windowstools-module.exe')
const data = mkdtempSync(resolve(tmpdir(), 'qing-windowstools-protocol-'))
const documentPath = resolve(data, 'associations.json')
writeFileSync(documentPath, JSON.stringify({ schemaVersion: 1, rows: [{ extension: '.txt', label: 'Test', executable: null }], checkOnStartup: false, prepareOnStartup: false }))
const child = spawn(binary, [], { windowsHide: true, env: { ...process.env, QINGTOOLBOX_MODULE_ID: 'qing.windowstools', QINGTOOLBOX_MODULE_NONCE: 'test-nonce-v1', QINGTOOLBOX_MODULE_DATA_DIR: data }, stdio: ['pipe', 'pipe', 'pipe'] })
const pending = new Map()
const lines = createInterface({ input: child.stdout })
let id = 0
lines.on('line', line => { const frame = JSON.parse(line); const waiter = pending.get(frame.requestId); if (waiter) { pending.delete(frame.requestId); clearTimeout(waiter.timer); waiter.resolve(frame) } })
function request(messageType, payload) {
  const requestId = `test-${++id}`
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => { pending.delete(requestId); reject(new Error(`Timeout: ${messageType}`)) }, 25_000)
    pending.set(requestId, { resolve, timer }); child.stdin.write(JSON.stringify({ protocolVersion: 1, messageType, requestId, payload }) + '\n')
  })
}
const invoke = (method, payload = {}) => request('module.invoke.request', { method, payload })
async function tableReady() {
  for (let i = 0; i < 200; i++) {
    const result = await invoke('getAssociationTable')
    assert.equal(result.error, undefined)
    if (!result.payload.busy) return result.payload
    await new Promise(r => setTimeout(r, 20))
  }
  throw new Error('Association job timed out')
}
try {
  const hello = await request('module.hello.request', { moduleId: 'qing.windowstools', nonce: 'test-nonce-v1' })
  assert.equal(hello.payload.lifecycleVersion, 1)
  assert.equal(hello.payload.moduleId, 'qing.windowstools')
  assert.equal(hello.payload.version, '0.1.1')
  const initialTable = await tableReady()
  assert.equal(initialTable.rows.length, 1)
  assert.equal(initialTable.checkOnStartup, false)
  assert.equal(initialTable.rows[0].status, 'unconfigured')
  assert.ok((await invoke('addAssociationRows', { extensions: ['.exe'] })).error)
  assert.equal((await invoke('addAssociationRows', { extensions: ['.abc', '.ABC'], label: 'Fixture' })).error, undefined)
  let table = await tableReady()
  assert.equal(table.rows.length, 2, 'normalize and deduplicate additions')
  assert.equal((await invoke('setAssociationPreferences', { checkOnStartup: false, prepareOnStartup: true })).error, undefined)
  table = await tableReady()
  assert.equal(table.prepareOnStartup, false, 'preparation requires startup checks')
  assert.equal((await invoke('removeAssociationRows', { extensions: ['.abc'] })).error, undefined)
  assert.equal((await tableReady()).rows.length, 1)
  assert.equal(JSON.parse(readFileSync(documentPath)).rows.length, 1, 'table changes persisted only in temporary data')
  const state = await invoke('getState')
  assert.equal(state.error, undefined)
  assert.equal(typeof state.payload.edition, 'string')
  assert.equal(state.payload.firewall.length, 3)
  assert.equal(state.payload.operation.running, false)
  // Do not print actual local configuration; only structural assertions.
  const unsafe = await invoke('beginAction', { action: 'update.disable' })
  assert.match(unsafe.error.message, /确认/)
  assert.match((await invoke('beginAction', { action: 'firewall.repair' })).error.message, /确认/)
  const injected = await invoke('beginAction', { action: 'update.disable;whoami', confirmed: true })
  assert.match(injected.error.message, /不支持/)
  const badExt = await invoke('getAssociation', { extension: '../exe' })
  assert.ok(badExt.error)
  const assoc = await invoke('getAssociation', { extension: '.txt' })
  assert.equal(assoc.error, undefined)
  assert.equal(assoc.payload.extension, '.txt')
  const badRegistration = await invoke('registerAssociation', { extension: '.txt', executable: 'Z:\\nonexistent-test-file.exe' })
  assert.ok(badRegistration.error)
  for (const active of [true, false, true]) {
    const response = await request('module.lifecycle.request', { active })
    assert.equal(response.payload.active, active)
  }
  await request('module.shutdown.request', {})
  child.stdin.end()
  await new Promise(r => child.exitCode !== null ? r() : child.once('exit', r))
  console.log('PASS: native table jobs/persistence in isolated data, read-only system/association state, consent and injection guards. No system settings changed.')
} finally {
  if (child.exitCode === null) child.kill()
  lines.close()
  for (const p of pending.values()) clearTimeout(p.timer)
  // Only the exact temporary directory allocated above is removed.
  assert.equal(dirname(data), resolve(tmpdir()))
  assert.ok(basename(data).startsWith('qing-windowstools-protocol-'))
  rmSync(data, { recursive: true, force: true })
}
