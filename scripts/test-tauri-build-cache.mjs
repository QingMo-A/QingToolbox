import { test } from 'node:test'
import assert from 'node:assert/strict'
import { mkdtempSync, mkdirSync, copyFileSync, writeFileSync, rmSync } from 'node:fs'
import { resolve, dirname, join } from 'node:path'
import { tmpdir } from 'node:os'
import { fileURLToPath } from 'node:url'
import { spawnSync } from 'node:child_process'

test('content cache reuses unchanged work and rejects changed/missing outputs', t => {
  const base = resolve(tmpdir())
  const root = mkdtempSync(join(base, 'qing-cache-test-'))
  t.after(() => {
    assert.equal(dirname(resolve(root)), base)
    assert.ok(root.startsWith(join(base, 'qing-cache-test-')))
    rmSync(root, { recursive: true, force: true })
  })
  const write = (path, content) => {
    const file = resolve(root, path)
    mkdirSync(dirname(file), { recursive: true })
    writeFileSync(file, content)
  }
  write('scripts/tauri-build-cache.mjs', '')
  copyFileSync(fileURLToPath(new URL('./tauri-build-cache.mjs', import.meta.url)), join(root, 'scripts/tauri-build-cache.mjs'))
  const src = 'QingToolbox.Tauri/native-launcher/src/main.rs'
  const out = 'QingToolbox.Tauri/src-tauri/resources/modules/qing.launcher/bin/module.exe'
  write(src, 'one')
  write('QingToolbox.Tauri/native-launcher/ui-src/package.json', '{}')
  write(out, 'binary')
  const run = (...args) => spawnSync(process.execPath, [join(root, 'scripts/tauri-build-cache.mjs'), ...args], { encoding: 'utf8' })
  const hash = () => {
    const value = run('fingerprint', 'launcher')
    assert.equal(value.status, 0, value.stderr)
    return value.stdout.trim()
  }
  const before = hash()
  assert.equal(run('check', 'launcher').status, 1)
  assert.equal(run('save', 'launcher', before).status, 0)
  assert.equal(run('check', 'launcher').status, 0)
  write('QingToolbox.Tauri/native-launcher/target/ignored', 'generated')
  write('QingToolbox.Tauri/native-launcher/ui-src/node_modules/ignored', 'dependency cache')
  assert.equal(run('check', 'launcher').status, 0)
  write(src, 'two')
  assert.equal(run('check', 'launcher').status, 1)
  assert.notEqual(run('save', 'launcher', before).status, 0)
  write(src, 'one')
  assert.equal(run('check', 'launcher').status, 0)
  write(out, 'corrupt')
  assert.equal(run('check', 'launcher').status, 1)
  write(out, 'binary')
  assert.equal(run('check', 'launcher').status, 0)
  rmSync(resolve(root, out))
  assert.equal(run('check', 'launcher').status, 1)
})
