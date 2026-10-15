import assert from 'node:assert/strict'
import { test } from 'node:test'
import { RevealGate } from './revealMotion.ts'

test('cold mount and shown event cannot restart an in-progress entrance', () => {
  const gate = new RevealGate()
  assert.equal(gate.accept(0), true)
  assert.equal(gate.accept(20), false)
  assert.equal(gate.accept(400), false)
  assert.equal(gate.accept(1000), true)
})
