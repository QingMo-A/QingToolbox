import { test } from 'node:test'
import assert from 'node:assert/strict'
import { mkdtempSync, mkdirSync, writeFileSync, rmSync } from 'node:fs'
import { resolve, dirname } from 'node:path'
import { tmpdir } from 'node:os'
import { resolveModulesRoot, moduleSource } from './module-sources.mjs'

test('explicit module checkout rejects old WPF catalogs and accepts native catalogs', t => {
  const base = resolve(tmpdir())
  const root = mkdtempSync(resolve(base, 'qing-module-source-'))
  t.after(() => { assert.equal(dirname(root), base); rmSync(root, { recursive: true, force: true }) })
  mkdirSync(resolve(root, 'modules'))
  const path = resolve(root, 'modules/index.json')
  writeFileSync(path, JSON.stringify({ schemaVersion: 1, sourceId: 'qingtoolbox-official' }))
  assert.throws(() => resolveModulesRoot(root, { QINGTOOLBOX_MODULES_ROOT: root }), /WPF/)
  writeFileSync(path, JSON.stringify({ schemaVersion: 2, sourceId: 'qingtoolbox-official-tauri', moduleProfile: 'tauri-process-v1' }))
  assert.equal(resolveModulesRoot(root, { QINGTOOLBOX_MODULES_ROOT: root }).toLowerCase(), root.toLowerCase())
})

test('integrated transfer stays host-owned and unknown modules are rejected', () => {
  const host = resolve('host')
  assert.throws(() => moduleSource('transfer', host), /Unknown independent module/)
  assert.throws(() => moduleSource('not-a-module', host), /Unknown/)
})
