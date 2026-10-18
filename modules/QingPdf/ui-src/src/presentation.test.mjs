import { test } from 'node:test'
import assert from 'node:assert/strict'
import { messageOf, formatSize, splitPreview } from './presentation.ts'

test('preserves native dialog string errors instead of hiding permission failures', () => {
  assert.equal(messageOf('dialog.open not allowed. Permissions associated with this command: dialog:allow-open'), 'dialog.open not allowed. Permissions associated with this command: dialog:allow-open')
  assert.equal(messageOf(new Error('无法打开文件')), '无法打开文件')
  assert.equal(messageOf({ error: { message: '文件损坏' } }), '文件损坏')
  assert.match(messageOf(null), /请重试/)
})

test('uses readable units without promoting empty files to one KB', () => {
  assert.equal(formatSize(0), '0 B')
  assert.equal(formatSize(1024), '1.0 KB')
  assert.equal(formatSize(1536), '1.5 KB')
  assert.equal(formatSize(18.5 * 1024 ** 2), '19 MB')
  assert.equal(formatSize(1024 ** 3), '1.0 GB')
  assert.equal(formatSize(NaN), '—')
})

test('previews evenly distributed pages and bounds large documents', () => {
  assert.deepEqual(splitPreview(8, 3), ['1–3', '4–6', '7–8'])
  assert.deepEqual(splitPreview(3, 3), ['1', '2', '3'])
  assert.deepEqual(splitPreview(3, 4), [])
  assert.deepEqual(splitPreview(3, 2.5), [])
  assert.equal(splitPreview(100000, 100000).length, 12)
})
