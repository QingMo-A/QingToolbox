import { readFileSync } from 'node:fs'
import ts from 'typescript'
import { test } from 'node:test'
import assert from 'node:assert/strict'
const { outputText } = ts.transpileModule(readFileSync(new URL('./presentation.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 } })
const { formatSize, messageOf, batchSummary } = await import(`data:text/javascript;base64,${Buffer.from(outputText).toString('base64')}`)
test('preserves actionable native-dialog errors', () => { assert.equal(messageOf('dialog.open not allowed'), 'dialog.open not allowed'); assert.equal(messageOf({ message: '没有权限' }), '没有权限') })
test('uses readable sizes including empty files', () => { assert.equal(formatSize(0), '0 B'); assert.equal(formatSize(1024), '1.00 KB'); assert.equal(formatSize(1024 ** 2), '1.00 MB') })
test('cancelled and failed tasks count as finished but never successful', () => { assert.deepEqual(batchSummary([{status:'succeeded'},{status:'failed'},{status:'cancelled'},{status:'running'}]), {success:1,failed:1,cancelled:1,finished:3}) })
