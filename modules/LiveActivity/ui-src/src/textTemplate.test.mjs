import { test } from 'node:test'
import assert from 'node:assert/strict'
import { renderText, usesPlaceholder } from './textTemplate.ts'
test('plain-text values, zero and per-placeholder fallback match native semantics', () => {
  assert.equal(renderText('余量 {codex.remaining} / {codex.reset}', { 'codex.remaining':'0%', 'codex.reset':'1小时0分' }, '未知'), '余量 0% / 1小时0分')
  assert.equal(renderText('{codex.remaining|未显示} {codex.secondary.remaining}', { 'codex.remaining':null, 'codex.secondary.remaining':null }, '暂无数据'), '未显示 暂无数据')
})
test('state templates preserve lines and escaped tokens are not timer/time references', () => {
  assert.equal(renderText('{date}\n额度 {codex.remaining|未知}', {date:'2026-10-07', 'codex.remaining':null}, ''), '2026-10-07\n额度 未知')
  assert.equal(usesPlaceholder('{ time |未知}', 'time'), true)
  assert.equal(usesPlaceholder('{date}\n{stopwatch}', 'stopwatch'), true)
  assert.equal(usesPlaceholder('{{time}} {timeXYZ}', 'time'), false)
  assert.equal(usesPlaceholder('{codex.remaining|{time}}', 'time'), false)
})
test('unknown names, escaped braces and replacement values stay literal', () => {
  assert.equal(renderText('{{time}} {bad} {codex.remaining|休息🌟}', { 'codex.remaining':null }, ''), '{time} {bad} 休息🌟')
  assert.equal(renderText('{codex.remaining}', { 'codex.remaining':null }, '<b>{date}</b>'), '<b>{date}</b>')
  assert.equal(renderText('{codex.remaining|{bad}} {bad|{codex.remaining}}', { 'codex.remaining':null }, ''), '{bad} {bad|}')
  assert.equal(Array.from(renderText('🌟'.repeat(256), {}, '')).length, 96)
})
test('state words and task keys are their own placeholders, not prefixes of others', () => {
  assert.equal(usesPlaceholder('{countdown.state}', 'countdown'), false)
  assert.equal(usesPlaceholder('{countdown.state}', 'countdown.state'), true)
  assert.equal(usesPlaceholder('{tasks.count|0} {task}', 'task'), true)
  assert.equal(renderText('{task|空闲} · {tasks.count}', { task: null, 'tasks.count': '0' }, '?'), '空闲 · 0')
})
