import { test } from 'node:test'
import assert from 'node:assert/strict'
import { remainingPercent, quotaLabel, resetLabel, timeLabel } from './quota.ts'

test('quota percentages are remaining quota, not token counts; unknown stays unknown', () => {
  assert.equal(remainingPercent({ usedFraction:0.25 }), 75)
  assert.equal(remainingPercent({ usedFraction:0 }), 100)
  assert.equal(remainingPercent({ usedFraction:1 }), 0)
  for (const usedFraction of [null, undefined, NaN, Infinity]) assert.equal(remainingPercent({ usedFraction }), null)
  assert.equal(remainingPercent({ usedFraction:2 }), 0)
  assert.equal(quotaLabel({ windowMinutes:300 }, '主额度'), '5 小时额度')
  assert.equal(quotaLabel({ windowMinutes:10080 }, '次额度'), '每周额度')
  assert.equal(quotaLabel({ windowMinutes:60 }, '主额度'), '1 小时额度')
  assert.equal(quotaLabel({ windowMinutes:null }, '主额度'), '主额度')
})
test('reset seconds are rendered as local time without fabricating missing or expired resets', () => {
  assert.match(resetLabel(1800000000, 1700000000000, 1700000000000), /重置$/)
  assert.equal(resetLabel(1700000000, 1699999999000, 1800000000000), '已到重置时间，等待刷新')
  for (const reset of [null, undefined, -1, NaN, Infinity, Number.MAX_SAFE_INTEGER]) assert.equal(resetLabel(reset, null), '未提供重置时间')
  for (const timestamp of [null, undefined, -1, NaN, Infinity]) assert.equal(timeLabel(timestamp), '—')
  assert.match(timeLabel(1700000000000), /^\d{2}:\d{2}:\d{2}$/)
})
