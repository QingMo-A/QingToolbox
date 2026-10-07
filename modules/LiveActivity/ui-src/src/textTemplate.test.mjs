import { test } from 'node:test'
import assert from 'node:assert/strict'
import { renderText } from './textTemplate.ts'
test('plain-text values, zero and per-placeholder fallback match native semantics', () => {
  assert.equal(renderText('余量 {codex.remaining} / {codex.reset}', { 'codex.remaining':'0%', 'codex.reset':'1小时0分' }, '未知'), '余量 0% / 1小时0分')
  assert.equal(renderText('{codex.remaining|未显示} {codex.secondary.remaining}', { 'codex.remaining':null, 'codex.secondary.remaining':null }, '暂无数据'), '未显示 暂无数据')
})
test('unknown names, escaped braces and replacement values stay literal', () => {
  assert.equal(renderText('{{time}} {bad} {codex.remaining|休息🌟}', { 'codex.remaining':null }, ''), '{time} {bad} 休息🌟')
  assert.equal(renderText('{codex.remaining}', { 'codex.remaining':null }, '<b>{date}</b>'), '<b>{date}</b>')
  assert.equal(renderText('{codex.remaining|{bad}} {bad|{codex.remaining}}', { 'codex.remaining':null }, ''), '{bad} {bad|}')
  assert.equal(Array.from(renderText('🌟'.repeat(256), {}, '')).length, 96)
})
