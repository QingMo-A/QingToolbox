import test from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { renderRich } from './richText.ts'

for (const vector of JSON.parse(readFileSync(new URL('../../contracts/rich-text-v1.json', import.meta.url), 'utf8'))) {
  test(`rich text: ${vector.name}`, () => assert.deepEqual(renderRich(vector.input, vector.values, '暂无数据'), vector.output))
}
test('bounded Unicode, literal HTML and incomplete markup', () => {
  assert.equal(Array.from(renderRich('😀'.repeat(2000), {}, '').lines[0].left[0].text).length, 256)
  assert.equal(renderRich('<script>bad</script>', {}, '').lines[0].left[0].text, '<script>bad</script>')
  assert.equal(renderRich('*unclosed', {}, '').lines[0].left[0].text, '*unclosed')
  const document = renderRich(`A{space}${'字'.repeat(400)}{space}R`, {}, '')
  assert.equal(document.lines[0].right[0].text, 'R')
  assert.equal(document.lines[0].left.length, 2)
  assert.equal(Array.from(document.lines[0].left.map(run => run.text).join('')).length, 255)
})
