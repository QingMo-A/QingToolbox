// Frontend-only preview. Every IPC request is mocked, including UAC and file
// selection; this script cannot modify the real Windows configuration.
import { spawn } from 'node:child_process'
import { mkdirSync } from 'node:fs'
import { resolve } from 'node:path'
import assert from 'node:assert/strict'
const { chromium } = await import(process.env.QING_PLAYWRIGHT_MODULE || 'playwright')
const ui = resolve(import.meta.dirname, '../ui-src')
const out = resolve(process.env.QING_VISUAL_OUTPUT || resolve(ui, '../../../..', 'artifacts/windows-tools-ui'))
mkdirSync(out, { recursive: true })
const server = spawn(process.execPath, [resolve(ui, 'node_modules/vite/bin/vite.js'), '--host', '127.0.0.1', '--port', '5194', '--strictPort'], { cwd: ui, stdio: 'ignore', windowsHide: true })
let browser
try {
  for (let i = 0; i < 80; i++) {
    if (await fetch('http://127.0.0.1:5194').then(r => r.ok).catch(() => false)) break
    await new Promise(r => setTimeout(r, 100))
  }
  browser = await chromium.launch({ channel: 'msedge', headless: true })
  const page = await browser.newPage({ viewport: { width: 1200, height: 840 } })
  const errors = []
  page.on('pageerror', error => errors.push(error.message))
  await page.addInitScript(() => {
    const state = { edition: 'Professional', managed: false, problems: [], firewallService: 'Running', firewallServices: [{ name: 'BFE', status: 'Running', start: 2 }, { name: 'MpsSvc', status: 'Running', start: 2 }], update: { known: true, noAutoUpdate: null, supported: true, hasBackup: false }, firewall: ['Domain', 'Private', 'Public'].map(name => ({ name, enabled: true, local: 1, hasBackup: false, error: null })), operation: { running: false, action: '', error: null, revision: 0 } }
    const formats = [['.txt', '文本'], ['.md', 'Markdown'], ['.pdf', 'PDF 文档'], ['.docx', 'Word 文档'], ['.xlsx', 'Excel 表格'], ['.pptx', 'PowerPoint'], ['.csv', 'CSV 表格'], ['.json', 'JSON'], ['.xml', 'XML'], ['.jpg', 'JPEG 图片'], ['.jpeg', 'JPEG 图片'], ['.png', 'PNG 图片'], ['.webp', 'WebP 图片'], ['.gif', 'GIF 图片'], ['.svg', 'SVG 图片'], ['.mp3', 'MP3 音频'], ['.wav', 'WAV 音频'], ['.mp4', 'MP4 视频'], ['.mkv', 'MKV 视频'], ['.zip', 'ZIP 压缩包'], ['.7z', '7-Zip 压缩包'], ['.rar', 'RAR 压缩包']]
    const table = { rows: formats.map(([extension, label]) => ({ extension, label, executable: ['.txt', '.md', '.pdf'].includes(extension) ? 'C:\\Apps\\SampleReader.exe' : null, currentApplication: extension === '.pdf' ? 'Other Reader' : 'Sample Reader', currentExecutable: extension === '.pdf' ? 'C:\\Other\\Reader.exe' : 'C:\\Apps\\SampleReader.exe', status: extension === '.pdf' ? 'changed' : ['.txt', '.md'].includes(extension) ? 'matched' : 'unconfigured', error: null })), checkOnStartup: true, prepareOnStartup: false, busy: false, revision: 1, checkedAt: 1, message: '', error: null, pendingConfirmation: [] }
    window.__fixture = { state, table, settingsCalls: 0 }
    window.__TAURI_INTERNALS__ = { invoke: async (command, args) => {
      if (command === 'get_module_window_context') return { theme: 'light', appearancePreset: 'qing-default', hostCapabilities: ['fileDialog.openFiles'], hostApiCapabilities: ['fileDialog.openFiles'] }
      if (command === 'module_file_dialog') return { paths: ['C:\\Apps\\SampleReader.exe'] }
      if (command === 'invoke_module_window') {
        if (args.method === 'getState') return structuredClone(state)
        if (args.method === 'openSettings') { window.__fixture.settingsCalls++; return {} }
        if (args.method === 'registerAssociationBatch') {
          table.pendingConfirmation = [...args.payload.extensions]
          for (const row of table.rows) if (table.pendingConfirmation.includes(row.extension)) row.status = 'needsConfirmation'
          table.message = '候选程序已准备，默认应用尚未改变，请在 Windows 中确认。'
        }
        return structuredClone(table)
      }
      return null
    } }
  })
  await page.goto('http://127.0.0.1:5194')
  await page.getByText('未设置关闭策略', { exact: true }).waitFor()
  await page.evaluate(() => Promise.allSettled(document.getAnimations().map(a => a.finished)))
  assert.equal(await page.locator('.identity img').evaluate(img => img.complete && img.naturalWidth > 0), true, 'module icon loads')
  await page.screenshot({ path: resolve(out, 'light.png'), fullPage: true })
  await page.getByRole('button', { name: '关闭自动更新', exact: true }).click()
  await page.getByRole('dialog').waitFor()
  await page.waitForFunction(() => { const panel = document.querySelector('[role="dialog"]'); return panel && getComputedStyle(panel).opacity === '1' && getComputedStyle(panel).transform === 'none' && !document.querySelector('.q-modal-enter-active') })
  await page.evaluate(() => Promise.allSettled(document.getAnimations().map(a => a.finished)))
  await page.screenshot({ path: resolve(out, 'confirmation.png') })
  await page.getByRole('button', { name: '取消', exact: true }).click()
  await page.evaluate(() => Promise.allSettled(document.getAnimations().map(a => a.finished)))
  await page.getByRole('checkbox', { name: '选择 .pdf', exact: true }).check()
  await page.getByRole('checkbox', { name: '选择 .txt', exact: true }).check()
  await page.getByRole('button', { name: '设置选中项', exact: false }).click()
  await page.getByRole('status').filter({ hasText: '默认应用尚未改变' }).waitFor()
  assert.equal(await page.evaluate(() => window.__fixture.settingsCalls), 1, 'batch opens one consent page only')
  await page.getByRole('button', { name: '添加后缀', exact: true }).click()
  await page.getByRole('dialog').waitFor()
  await page.waitForFunction(() => { const panel = document.querySelector('[role="dialog"]'); return panel && getComputedStyle(panel).opacity === '1' && getComputedStyle(panel).transform === 'none' && !document.querySelector('.q-modal-enter-active') })
  await page.evaluate(() => Promise.allSettled(document.getAnimations().map(a => a.finished)))
  await page.screenshot({ path: resolve(out, 'add-formats.png') })
  await page.getByRole('button', { name: '取消', exact: true }).click()
  await page.evaluate(() => { const { state } = window.__fixture; state.firewallService = 'Stopped'; state.firewallServices[1].status = 'Stopped'; state.firewallServices[1].start = 4; state.firewall.forEach(p => { p.enabled = null; p.source = 'saved' }) })
  await page.getByRole('button', { name: '刷新状态', exact: true }).click()
  await page.getByRole('button', { name: '检查并修复服务', exact: true }).waitFor()
  await page.screenshot({ path: resolve(out, 'service-stopped.png'), fullPage: true })
  await page.getByRole('button', { name: '检查并修复服务', exact: true }).click()
  await page.getByRole('dialog').waitFor()
  await page.waitForFunction(() => { const panel = document.querySelector('[role="dialog"]'); return panel && getComputedStyle(panel).opacity === '1' && getComputedStyle(panel).transform === 'none' && !document.querySelector('.q-modal-enter-active') })
  await page.evaluate(() => Promise.allSettled(document.getAnimations().map(a => a.finished)))
  await page.screenshot({ path: resolve(out, 'repair-confirmation.png') })
  await page.getByRole('button', { name: '取消', exact: true }).click()
  await page.evaluate(() => Promise.allSettled(document.getAnimations().map(a => a.finished)))
  await page.evaluate(() => { document.documentElement.dataset.theme = 'dark' })
  await page.screenshot({ path: resolve(out, 'dark.png'), fullPage: true })
  await page.setViewportSize({ width: 390, height: 1000 })
  await page.screenshot({ path: resolve(out, 'compact.png'), fullPage: true })
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true, 'no horizontal overflow')
  assert.deepEqual(errors, [])
  console.log(`PASS: table multi-select, one Windows confirmation, service repair/add dialogs, light/dark and compact layout. Screenshots: ${out}`)
} finally { await browser?.close(); server.kill() }
