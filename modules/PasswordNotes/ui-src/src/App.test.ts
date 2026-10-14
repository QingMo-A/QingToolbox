import { mount, flushPromises, type VueWrapper } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import App from './App.vue'

const mocks = vi.hoisted(() => ({ call: vi.fn(), invoke: vi.fn() }))
vi.mock('./bridge', () => ({ call: mocks.call, getContext: vi.fn() }))
vi.mock('@tauri-apps/api/core', () => ({ invoke: mocks.invoke }))
const entry = { id: 'entry', name: '示例游戏', link: 'https://example.com', remark: '主区', credentials: [{ id: 'account', account: 'demo', note: '主号' }] }
const state = { categories: [{ id: 'category', name: '游戏', entries: [entry] }], loadError: null, storage: 'windows-user-dpapi' }
let wrapper: VueWrapper
beforeEach(() => {
  mocks.call.mockReset(); mocks.invoke.mockReset()
  mocks.call.mockImplementation(async (method: string) => {
    if (method === 'getState') return structuredClone(state)
    if (method === 'getPassword') return { password: 'synthetic-password' }
    if (method === 'getEntryForEdit') return { ...structuredClone(entry), credentials: [{ id: 'account', account: 'demo', password: 'synthetic-password', note: '主号' }] }
    return { copied: true }
  })
  mocks.invoke.mockImplementation(async (method: string) => {
    if (method === 'get_module_window_context') return { hostCapabilities: ['dialog.show', 'fileDialog.saveFile', 'notification.show'], hostApiCapabilities: ['dialog.show', 'fileDialog.saveFile', 'notification.show'] }
    if (method === 'module_show_dialog') return { buttonId: 'cancel', values: {} }
    if (method === 'module_file_dialog') return { paths: ['C:\\test\\synthetic.txt'] }
    if (method === 'module_show_notification') return { id: 'notice' }
    return null
  })
})
afterEach(() => { wrapper?.unmount(); document.body.innerHTML = '' })
const open = async () => { wrapper = mount(App, { attachTo: document.body }); await flushPromises() }
describe('password notebook UI privacy and workflow', () => {
  it('starts masked, and password copying never requires exposing its value to the list', async () => {
    await open()
    expect(wrapper.find('.password-cell code').text()).toBe('***')
    expect(mocks.call).not.toHaveBeenCalledWith('getPassword', expect.anything())
    await wrapper.find('[aria-label="复制密码"]').trigger('click'); await flushPromises()
    expect(mocks.call).toHaveBeenCalledWith('copyCredential', { entryId: 'entry', credentialId: 'account', field: 'password' })
    expect(wrapper.text()).not.toContain('synthetic-password')
  })
  it('explicit show/hide works, and blur removes already revealed values', async () => {
    await open()
    await wrapper.find('[aria-label="显示密码"]').trigger('click'); await flushPromises()
    expect(wrapper.find('.password-cell code').text()).toBe('synthetic-password')
    window.dispatchEvent(new Event('blur')); await flushPromises()
    expect(wrapper.find('.password-cell code').text()).toBe('***')
  })
  it('a response arriving after blur cannot re-expose the password', async () => {
    let complete!: (v: {password: string}) => void
    const response = new Promise<{password: string}>(resolve => { complete = resolve })
    mocks.call.mockImplementation(async (method: string) => method === 'getPassword' ? response : structuredClone(state))
    await open(); await wrapper.find('[aria-label="显示密码"]').trigger('click')
    window.dispatchEvent(new Event('blur')); complete({ password: 'late-secret' }); await flushPromises()
    expect(wrapper.text()).not.toContain('late-secret')
    expect(wrapper.find('.password-cell code').text()).toBe('***')
  })
  it('canceling the plaintext warning does not open the picker or export', async () => {
    await open(); await wrapper.findAll('button').find(b => b.text().includes('导出全部 TXT'))!.trigger('click'); await flushPromises()
    expect(mocks.invoke).toHaveBeenCalledWith('module_show_dialog', expect.anything())
    expect(mocks.invoke).not.toHaveBeenCalledWith('module_file_dialog', expect.anything())
    expect(mocks.call).not.toHaveBeenCalledWith('exportTxt', expect.anything())
  })
  it('confirmed category export uses the host picker and only its selected scope', async () => {
    mocks.invoke.mockImplementation(async (method: string) => {
      if (method === 'get_module_window_context') return { hostCapabilities: ['dialog.show','fileDialog.saveFile','notification.show'], hostApiCapabilities: ['dialog.show','fileDialog.saveFile','notification.show'] }
      if (method === 'module_show_dialog') return { buttonId: 'export', values: {} }
      if (method === 'module_file_dialog') return { paths: ['C:\\test\\synthetic.txt'] }
      return { id: 'notice' }
    })
    await open(); await wrapper.findAll('button').find(b => b.text() === '导出分类')!.trigger('click'); await flushPromises()
    expect(mocks.call).toHaveBeenCalledWith('exportTxt', { path: 'C:\\test\\synthetic.txt', categoryId: 'category', includePasswords: true })
  })
  it('adding or removing an account does not submit the entry form', async () => {
    await open(); await wrapper.find('[aria-label="编辑 示例游戏"]').trigger('click'); await flushPromises()
    const add = document.querySelector<HTMLButtonElement>('.accounts-heading button')!
    expect(add.type).toBe('button'); add.click(); await flushPromises()
    expect(document.querySelectorAll('.draft-account')).toHaveLength(2)
    expect(mocks.call).not.toHaveBeenCalledWith('saveEntry', expect.anything())
  })
})
