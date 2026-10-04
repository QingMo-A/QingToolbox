import assert from 'node:assert/strict'
import { test } from 'node:test'
import { commandSuggestions, completeCommand } from './searchCommands.ts'

test('slash suggests exactly the three supported modes', () => {
  assert.deepEqual(commandSuggestions('/').map(command => command.prefix), ['/e', '/e:f', '/e:d'])
})
test('partial prefixes filter case-insensitively without suggesting ordinary queries', () => {
  assert.deepEqual(commandSuggestions('/E:').map(command => command.prefix), ['/e:f', '/e:d'])
  assert.equal(commandSuggestions('/e:d').length, 1)
  for (const text of ['', 'minecraft', '/unknown', '/e ', '/e:f *.exe']) assert.equal(commandSuggestions(text).length, 0)
})
test('completion inserts exactly one trailing space', () => {
  for (const { prefix } of commandSuggestions('/')) assert.equal(completeCommand(prefix), `${prefix} `)
})
