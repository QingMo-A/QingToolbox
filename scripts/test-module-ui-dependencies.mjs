import { test } from 'node:test'
import assert from 'node:assert/strict'
import { matchesInstalledLock } from './module-ui-dependencies.mjs'

const manifest = { dependencies: { vue: '3.5.42' }, devDependencies: { vite: '7.1.10' } }
const entry = { version: '3.5.42', resolved: 'https://registry.npmjs.org/vue/-/vue-3.5.42.tgz', integrity: 'sha512-test' }
const lock = { lockfileVersion: 3, packages: { '': manifest, 'node_modules/vue': entry } }
const installed = { lockfileVersion: 3, packages: { 'node_modules/vue': entry } }

test('reuse matching locked dependencies; allow omitted platform optional packages', () => {
  assert.equal(matchesInstalledLock(manifest, lock, installed), true)
  assert.equal(matchesInstalledLock(manifest, { ...lock, packages: { ...lock.packages, 'node_modules/optional-linux': { version: '1' } } }, installed), true)
})
test('reject changed manifest, version, source, integrity or lock format', () => {
  assert.equal(matchesInstalledLock({ ...manifest, dependencies: { vue: '3.5.43' } }, lock, installed), false)
  for (const key of ['version', 'resolved', 'integrity']) {
    assert.equal(matchesInstalledLock(manifest, lock, { ...installed, packages: { 'node_modules/vue': { ...entry, [key]: 'changed' } } }), false)
  }
  assert.equal(matchesInstalledLock(manifest, lock, { ...installed, lockfileVersion: 2 }), false)
})
test('reject empty, missing or unrecorded dependency trees', () => {
  assert.equal(matchesInstalledLock(manifest, lock, {}), false)
  assert.equal(matchesInstalledLock(manifest, lock, { ...installed, packages: {} }), false)
  assert.equal(matchesInstalledLock(manifest, lock, { ...installed, packages: { 'node_modules/unknown': entry } }), false)
})
