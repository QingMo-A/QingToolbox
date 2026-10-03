import { test } from 'node:test'
import assert from 'node:assert/strict'
import { resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { compareVersions, validateUpdate, validateCatalog } from './module-catalog.mjs'

const release = () => ({
  version: '0.3.0', channel: 'preview', moduleProfile: 'tauri-process-v1', apiVersion: 1,
  platform: 'windows', architecture: 'x64', minimumHostVersion: '0.3.0-alpha', maximumHostVersionExclusive: null,
  publishedAt: '2026-09-25T14:14:12Z',
  package: { fileName: 'qing.launcher-0.3.0-tauri.qmod', url: 'https://github.com/QingMo-A/QingToolbox/releases/download/modules-launcher-v0.3.0/qing.launcher-0.3.0-tauri.qmod', size: 1415757, sha256: 'a'.repeat(64) },
  releaseNotes: { 'zh-CN': '测试', 'en-US': 'Test' },
})
const manifest = entry => ({ schemaVersion: 2, moduleId: 'qing.launcher', publisher: 'QingMo-A', releases: [entry] })

test('semantic comparison handles numeric versions, previews and build metadata', () => {
  assert.equal(compareVersions('0.10.0', '0.9.9'), 1)
  assert.equal(compareVersions('1.0.0-alpha.10', '1.0.0-alpha.2'), 1)
  assert.equal(compareVersions('1.0.0', '1.0.0-alpha'), 1)
  assert.equal(compareVersions('1.0.0+one', '1.0.0+two'), 0)
  assert.throws(() => compareVersions('1.0.0-alpha.01', '1.0.0'))
})
test('unpublished native modules and verified release records are valid', () => {
  assert.equal(validateUpdate(manifest(release()), 'qing.launcher').releases.length, 1)
  assert.equal(validateUpdate({ ...manifest(release()), releases: [] }, 'qing.launcher').releases.length, 0)
})
test('old WPF profiles, malformed API versions and wrong module identities are rejected', () => {
  for (const mutation of [r => { r.moduleProfile = 'experimental-0.1' }, r => { r.apiVersion = '1' }, r => { r.apiVersion = 0 }]) {
    const r = release(); mutation(r); assert.throws(() => validateUpdate(manifest(r), 'qing.launcher'))
  }
  assert.throws(() => validateUpdate({ ...manifest(release()), schemaVersion: 1 }, 'qing.launcher'))
  assert.throws(() => validateUpdate(manifest(release()), 'qing.pdf'))
})
test('untrusted, mutable and misbound package downloads are rejected', () => {
  for (const url of ['https://evil.example/file.qmod', 'https://github.com/QingMo-A/QingToolbox/releases/latest/download/qing.launcher-0.3.0-tauri.qmod', release().package.url + '?token=x', release().package.url.replace('QingMo-A', 'Someone'), release().package.url.replace('qing.launcher', 'qing.pdf')]) {
    const r = release(); r.package.url = url; assert.throws(() => validateUpdate(manifest(r), 'qing.launcher'))
  }
  for (const mutation of [r => { r.package.size = 0 }, r => { r.package.sha256 = 'bad' }, r => { r.package.fileName = '../module.qmod' }]) {
    const r = release(); mutation(r); assert.throws(() => validateUpdate(manifest(r), 'qing.launcher'))
  }
})

test('branch packages require an official immutable commit and the catalog-owned directory', () => {
  const r = release()
  const commit = 'a'.repeat(40)
  const url = `https://raw.githubusercontent.com/QingMo-A/QingToolbox/${commit}/modules/Launcher/packages/${r.package.fileName}`
  r.package.url = url
  assert.equal(validateUpdate(manifest(r), 'qing.launcher', 'Launcher').releases.length, 1)
  assert.throws(() => validateUpdate(manifest(r), 'qing.launcher', 'QingPdf'))
  for (const changed of [url.replace(commit, 'modules'), url.replace(commit, 'latest'), url.replace(commit, 'abcd'), url.replace('/packages/', '/ui/'), url.replace('QingMo-A', 'Someone'), url + '?x=1', url.replace('/Launcher/', '/%4cauncher/'), url.replace('qing.launcher', 'qing.pdf')]) {
    r.package.url = changed
    assert.throws(() => validateUpdate(manifest(r), 'qing.launcher', 'Launcher'))
  }
})
test('duplicate versions, invalid ranges, dates and incomplete translations are rejected', () => {
  assert.throws(() => validateUpdate({ ...manifest(release()), releases: [release(), release()] }, 'qing.launcher'))
  for (const mutation of [r => { r.maximumHostVersionExclusive = '0.2.0' }, r => { r.publishedAt = '2026-02-31T14:14:12Z' }, r => { delete r.releaseNotes['zh-CN'] }, r => { r.version = '0.3.0-alpha'; r.channel = 'stable' }]) {
    const r = release(); mutation(r); assert.throws(() => validateUpdate(manifest(r), 'qing.launcher'))
  }
})
test('repository catalog contains only native modules, with diagnostics hidden from public browsing', () => {
  const result = validateCatalog(fileURLToPath(new URL('../modules', import.meta.url)))
  assert.equal(result.modules, 7)
  assert.equal(result.publicModules, 6)
})
