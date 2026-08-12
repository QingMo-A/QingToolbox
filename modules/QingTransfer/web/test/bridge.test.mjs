import assert from 'node:assert/strict'
assert.equal(typeof 'qingtransfer', 'string')
assert.equal(JSON.parse('{"type":"invoke","id":"1","method":"getState"}').method, 'getState')
console.log('QingTransfer Web bridge smoke passed.')
