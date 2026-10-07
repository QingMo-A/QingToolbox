import { test } from 'node:test'
import assert from 'node:assert/strict'
import { RefreshGate } from './refreshGate.ts'

test('background reads are single-flight and release the slot after failure', () => {
  const gate = new RefreshGate()
  const epoch = gate.begin(false)
  assert.notEqual(epoch, null)
  assert.equal(gate.begin(false), null)
  assert.equal(gate.accepts(epoch, false), true)
  gate.finish()
  assert.notEqual(gate.begin(false), null)
})
test('dragging/editing blocks reads and blocks already pending results', () => {
  const gate = new RefreshGate()
  assert.equal(gate.begin(true), null)
  const epoch = gate.begin(false)
  assert.equal(gate.accepts(epoch, true), false)
  gate.invalidate()
  assert.equal(gate.accepts(epoch, false), false, 'ending a drag must not revive a pre-drag response')
})
test('a read delayed beyond a settings save cannot revert the new settings', () => {
  const gate = new RefreshGate()
  const oldRead = gate.begin(false)
  gate.invalidate()
  assert.equal(gate.accepts(oldRead, false), false)
  gate.finish()
  const newRead = gate.begin(false)
  assert.equal(gate.accepts(newRead, false), true)
})
test('leaving the page rejects late results and permanently stops polling', () => {
  const gate = new RefreshGate()
  const epoch = gate.begin(false)
  gate.stop()
  gate.finish()
  assert.equal(gate.accepts(epoch, false), false)
  assert.equal(gate.begin(false), null)
})
