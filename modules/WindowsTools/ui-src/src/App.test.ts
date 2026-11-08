// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import App from './App.vue'

const mock = vi.hoisted(() => ({ call: vi.fn(), selectFiles: vi.fn() }))
vi.mock('./bridge', () => ({ call: mock.call }))
vi.mock('@qingtoolbox/module-ui', async importOriginal => ({ ...await importOriginal<object>(), selectFiles: mock.selectFiles }))
const initial = () => ({ edition: 'Professional', managed: false, problems: [], firewallService: 'Running', firewallServices: [{ name: 'BFE', status: 'Running', start: 2 }, { name: 'MpsSvc', status: 'Running', start: 2 }], update: { known: true, noAutoUpdate: null, supported: true, hasBackup: false }, firewall: [{ name: 'Private', enabled: true as boolean | null, local: 1, hasBackup: false, error: null, source: 'active' }], operation: { running: false, action: '', error: null, revision: 0 } })
const initialTable = () => ({
  rows: ['.pdf', '.txt', '.docx'].map(extension => ({ extension, label: '文档', executable: null as string | null, currentApplication: 'Other Reader', currentExecutable: 'C:\\Other\\Reader.exe', status: 'unconfigured', error: null })),
  checkOnStartup: true, prepareOnStartup: false, busy: false, revision: 1, checkedAt: 1,
  message: '', error: null, pendingConfirmation: [] as string[],
})
const wrappers: ReturnType<typeof mount>[] = []
async function render(state = initial(), table = initialTable()) {
  mock.call.mockImplementation(async (method: string, payload: Record<string, any> = {}) => {
    if (method === 'getState') return structuredClone(state)
    if (method === 'getOperation') return state.operation
    if (method === 'setAssociationTarget') {
      for (const row of table.rows) if (payload.extensions.includes(row.extension)) { row.executable = payload.executable; row.status = 'changed' }
      table.message = '目标已保存，尚未修改默认应用。'
    }
    if (method === 'registerAssociationBatch') {
      table.pendingConfirmation = [...payload.extensions]
      table.message = '已准备恢复，尚未修改默认应用。'
      for (const row of table.rows) if (payload.extensions.includes(row.extension)) row.status = 'needsConfirmation'
    }
    if (method === 'setAssociationPreferences') {
      table.checkOnStartup = payload.checkOnStartup
      table.prepareOnStartup = payload.checkOnStartup && payload.prepareOnStartup
    }
    return structuredClone(table)
  })
  const wrapper = mount(App, { attachTo: document.body }); wrappers.push(wrapper); await flushPromises(); return wrapper
}
function button(wrapper: ReturnType<typeof mount>, label: string) { return wrapper.findAll('button').find(b => b.text() === label)! }
function modalButton(label: string) { return [...document.querySelectorAll<HTMLButtonElement>('[role="dialog"] button')].find(b => b.textContent?.trim() === label)! }
beforeEach(() => { vi.clearAllMocks() })
afterEach(() => { for (const w of wrappers.splice(0)) w.unmount(); document.body.innerHTML = '' })

describe('Windows Tools explicit consent and association table', () => {
  it('only reads on mount and never mutates when a confirmation is cancelled', async () => {
    const w = await render()
    expect(mock.call.mock.calls.every(c => ['getState', 'getAssociationTable'].includes(c[0]))).toBe(true)
    await button(w, '关闭自动更新').trigger('click'); await flushPromises()
    expect(document.body.textContent).toContain('安全修复不会自动安装')
    expect(mock.call).not.toHaveBeenCalledWith('beginAction', expect.anything())
    modalButton('取消').click(); await flushPromises()
    expect(document.body.querySelector('[role="dialog"]')).toBeNull()
  })
  it('requires an explicit confirmation before requesting elevation', async () => {
    const w = await render(); await button(w, '关闭自动更新').trigger('click'); await flushPromises()
    modalButton('确认并授权').click(); await flushPromises()
    expect(mock.call).toHaveBeenCalledWith('beginAction', { action: 'update.disable', confirmed: true })
  })
  it('disables managed system modifications without disabling user association management', async () => {
    const state = initial(); state.managed = true
    const w = await render(state)
    expect(button(w, '关闭自动更新').attributes('disabled')).toBeDefined()
    expect(button(w, '关闭').attributes('disabled')).toBeDefined()
    expect(button(w, '添加后缀').attributes('disabled')).toBeUndefined()
  })
  it('sets multiple saved candidates and opens Windows confirmation only once', async () => {
    const w = await render(); mock.selectFiles.mockResolvedValue(['C:\\Apps\\Reader.exe'])
    await w.get('input[aria-label="选择 .pdf"]').setValue(true)
    await w.get('input[aria-label="选择 .txt"]').setValue(true)
    await button(w, '设置选中项 ↗').trigger('click'); await flushPromises()
    expect(mock.call).toHaveBeenCalledWith('setAssociationTarget', { extensions: ['.pdf', '.txt'], executable: 'C:\\Apps\\Reader.exe' })
    expect(mock.call).toHaveBeenCalledWith('registerAssociationBatch', { extensions: ['.pdf', '.txt'] })
    expect(mock.call.mock.calls.filter(c => c[0] === 'openSettings')).toEqual([['openSettings', { page: 'registered' }]])
    expect(w.text()).toContain('尚未修改默认应用')
    expect(w.text()).toContain('等待 Windows 确认')
  })
  it('does nothing if the executable picker is cancelled', async () => {
    const w = await render(); mock.selectFiles.mockResolvedValue([])
    await w.get('input[aria-label="选择 .pdf"]').setValue(true)
    await button(w, '设置选中项 ↗').trigger('click'); await flushPromises()
    expect(mock.call.mock.calls.every(c => ['getState', 'getAssociationTable'].includes(c[0]))).toBe(true)
  })
  it('preserves a previously selected program when filling other missing targets', async () => {
    const table = initialTable(); table.rows[0]!.executable = 'C:\\PDF\\Reader.exe'
    const w = await render(initial(), table); mock.selectFiles.mockResolvedValue(['C:\\Text\\Editor.exe'])
    await w.get('input[aria-label="选择 .pdf"]').setValue(true)
    await w.get('input[aria-label="选择 .txt"]').setValue(true)
    await button(w, '设置选中项 ↗').trigger('click'); await flushPromises()
    expect(mock.call).toHaveBeenCalledWith('setAssociationTarget', { extensions: ['.txt'], executable: 'C:\\Text\\Editor.exe' })
    expect(table.rows[0]!.executable).toBe('C:\\PDF\\Reader.exe')
    expect(mock.call).toHaveBeenCalledWith('registerAssociationBatch', { extensions: ['.pdf', '.txt'] })
  })
  it('select-all only selects visible filtered rows', async () => {
    const w = await render()
    await w.get('input[aria-label="搜索后缀或程序"]').setValue('.pdf')
    await w.get('input[aria-label="全选当前显示项"]').setValue(true)
    await w.get('input[aria-label="搜索后缀或程序"]').setValue('')
    expect((w.get('input[aria-label="选择 .pdf"]').element as HTMLInputElement).checked).toBe(true)
    expect((w.get('input[aria-label="选择 .txt"]').element as HTMLInputElement).checked).toBe(false)
    expect(w.text()).toContain('已选择 1 项')
  })
  it('does not auto-open system Settings for startup drift reminders', async () => {
    const table = initialTable(); table.pendingConfirmation = ['.pdf']; table.rows[0]!.status = 'needsConfirmation'
    const w = await render(initial(), table)
    expect(w.text()).toContain('前往 Windows 确认')
    expect(mock.call).not.toHaveBeenCalledWith('openSettings', expect.anything())
  })
  it('offers service repair with clear impact and a separate confirmation', async () => {
    const state = initial(); state.firewallService = 'Stopped'; state.firewallServices[1]!.status = 'Stopped'; state.firewallServices[1]!.start = 4
    state.firewall[0]!.enabled = null; state.firewall[0]!.source = 'saved'
    const w = await render(state)
    expect(w.text()).toContain('已保存配置：开启')
    expect(w.text()).toContain('不代表当前已生效')
    await button(w, '检查并修复服务').trigger('click'); await flushPromises()
    expect(document.body.textContent).toContain('可能影响部分网络连接')
    expect(mock.call).not.toHaveBeenCalledWith('beginAction', expect.anything())
    modalButton('确认并授权').click(); await flushPromises()
    expect(mock.call).toHaveBeenCalledWith('beginAction', { action: 'firewall.repair', confirmed: true })
  })
  it('turning startup checks off also disables automatic preparation', async () => {
    const table = initialTable(); table.prepareOnStartup = true
    const w = await render(initial(), table)
    await w.get('.association-options input').setValue(false); await flushPromises()
    expect(mock.call).toHaveBeenCalledWith('setAssociationPreferences', { checkOnStartup: false, prepareOnStartup: true })
    const options = w.findAll('.association-options input')
    expect(options[1]!.attributes('disabled')).toBeDefined()
    expect((options[1]!.element as HTMLInputElement).checked).toBe(false)
  })
})
