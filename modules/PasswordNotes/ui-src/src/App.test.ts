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
  it('searches every category by name or account, never by password', async () => {
    const other = { id: 'site', name: '网站', entries: [{ id: 'site-entry', name: 'GitHub', link: 'https://github.com', remark: '', credentials: [{ id: 'site-account', account: 'octo', note: '' }] }] }
    mocks.call.mockImplementation(async (method: string) => method === 'getState' ? { ...structuredClone(state), categories: [...structuredClone(state).categories, other] } : { copied: true })
    await open()
    await wrapper.find('.search input').setValue('octo')
    expect(wrapper.findAll('.entry-card')).toHaveLength(1)
    expect(wrapper.get('.entry-card').text()).toContain('GitHub')
    expect(wrapper.get('.entry-category').text()).toBe('网站')
    await wrapper.find('.search input').setValue('synthetic-password')
    expect(wrapper.findAll('.entry-card')).toHaveLength(0)
  })
  it('confirms a copy on the button itself without showing the password', async () => {
    await open()
    await wrapper.find('[aria-label="复制密码"]').trigger('click'); await flushPromises()
    expect(wrapper.get('[aria-label="复制密码"]').classes()).toContain('copied')
    expect(wrapper.find('.password-cell code').text()).toBe('***')
  })
  it('shows the auto-hide countdown only while a password is revealed', async () => {
    await open()
    expect(wrapper.find('.reveal-ring').exists()).toBe(false)
    await wrapper.find('[aria-label="显示密码"]').trigger('click'); await flushPromises()
    expect(wrapper.get('.reveal-ring circle').attributes('style')).toContain('30s')
    await wrapper.find('[aria-label="隐藏密码"]').trigger('click'); await flushPromises()
    expect(wrapper.find('.reveal-ring').exists()).toBe(false)
    expect(wrapper.find('.password-cell code').text()).toBe('***')
  })
  it('adding or removing an account does not submit the entry form', async () => {
    await open(); await wrapper.find('[aria-label="编辑 示例游戏"]').trigger('click'); await flushPromises()
    const add = document.querySelector<HTMLButtonElement>('.accounts-heading button')!
    expect(add.type).toBe('button'); add.click(); await flushPromises()
    expect(document.querySelectorAll('.draft-account')).toHaveLength(2)
    expect(mocks.call).not.toHaveBeenCalledWith('saveEntry', expect.anything())
  })

  it('stores repeated phone/email and custom fields only on their own account', async () => {
    mocks.call.mockImplementation(async (method: string) => {
      if (method === 'getEntryForEdit') return { ...structuredClone(entry), credentials: [{ id: 'account', account: 'demo', password: 'synthetic-password', note: '' }] }
      return structuredClone(state)
    })
    await open(); await wrapper.get('[aria-label="编辑 示例游戏"]').trigger('click'); await flushPromises()
    const add = document.querySelector<HTMLButtonElement>('.add-security-field')!
    expect(add.type).toBe('button')
    for (let i = 0; i < 4; i++) { add.click(); await flushPromises() }
    const fields = [...document.querySelectorAll<HTMLElement>('.security-field-edit')]
    const input = (element: Element, value: string) => { const field = element as HTMLInputElement; field.value = value; field.dispatchEvent(new Event('input', { bubbles: true })) }
    input(fields[0].querySelector('input')!, '+1 202 555 0100')
    input(fields[1].querySelector('input')!, '+44 7700 900123')
    for (const [index, kind] of [[2, 'email'], [3, 'custom']] as const) {
      const select = fields[index].querySelector('select')!; select.value = kind; select.dispatchEvent(new Event('change', { bubbles: true }))
    }
    await flushPromises()
    input(fields[2].querySelector('input')!, ' safe@example.com ')
    input(fields[3].querySelectorAll('input')[0], ' 备用邮箱 ')
    input(fields[3].querySelectorAll('input')[1], 'other@example.com')
    document.querySelector<HTMLButtonElement>('.accounts-heading button')!.click(); await flushPromises()
    const second = document.querySelectorAll<HTMLElement>('.draft-account')[1]
    input(second.querySelectorAll('input')[0], 'second'); input(second.querySelectorAll('input')[1], 'second-password')
    expect(mocks.call).not.toHaveBeenCalledWith('saveEntry', expect.anything())
    ;[...document.querySelectorAll<HTMLButtonElement>('.q-modal-actions button')].find(b => b.textContent?.includes('保存条目'))!.click()
    await flushPromises()
    const payload = mocks.call.mock.calls.find(([method]) => method === 'saveEntry')![1]
    expect(payload.credentials[0].securityFields).toEqual([
      { kind: 'phone', label: '', value: '+1 202 555 0100' }, { kind: 'phone', label: '', value: '+44 7700 900123' },
      { kind: 'email', label: '', value: 'safe@example.com' }, { kind: 'custom', label: '备用邮箱', value: 'other@example.com' },
    ])
    expect(payload.credentials[1].securityFields).toEqual([])
  })

  it('ignores blank optional rows and requires a name for a filled custom row', async () => {
    await open(); await wrapper.get('[aria-label="编辑 示例游戏"]').trigger('click'); await flushPromises()
    document.querySelector<HTMLButtonElement>('.add-security-field')!.click(); await flushPromises()
    const select = document.querySelector<HTMLSelectElement>('.security-field-edit select')!; select.value = 'custom'; select.dispatchEvent(new Event('change', { bubbles: true })); await flushPromises()
    const value = document.querySelectorAll<HTMLInputElement>('.security-field-edit input')[1]
    value.value = 'a@example.com'; value.dispatchEvent(new Event('input', { bubbles: true }))
    const save = [...document.querySelectorAll<HTMLButtonElement>('.q-modal-actions button')].find(b => b.textContent?.includes('保存条目'))!
    save.click(); await flushPromises()
    expect(document.querySelector('.edit-error')?.textContent).toContain('设置名称')
    expect(mocks.call).not.toHaveBeenCalledWith('saveEntry', expect.anything())
    value.value = ' '; value.dispatchEvent(new Event('input', { bubbles: true })); save.click(); await flushPromises()
    const payload = mocks.call.mock.calls.find(([method]) => method === 'saveEntry')![1]
    expect(payload.credentials[0].securityFields).toEqual([])
  })

  it('deep-copies existing contacts so editing or canceling cannot mutate the loaded record', async () => {
    const editable = { ...structuredClone(entry), credentials: [{ id: 'account', account: 'demo', password: 'synthetic-password', note: '', securityFields: [{ kind: 'email', label: '', value: 'original@example.com' }] }] }
    mocks.call.mockImplementation(async (method: string) => method === 'getEntryForEdit' ? editable : structuredClone(state))
    await open(); await wrapper.get('[aria-label="编辑 示例游戏"]').trigger('click'); await flushPromises()
    const input = document.querySelector<HTMLInputElement>('.security-field-edit input')!; input.value = 'edited@example.com'; input.dispatchEvent(new Event('input', { bubbles: true }))
    document.querySelector<HTMLButtonElement>('[aria-label="关闭条目编辑"]')!.click(); await flushPromises()
    expect(editable.credentials[0].securityFields[0].value).toBe('original@example.com')
    expect(mocks.call).not.toHaveBeenCalledWith('saveEntry', expect.anything())
  })

  it('removes only the selected contact and enforces the per-account field limit', async () => {
    await open(); await wrapper.get('[aria-label="编辑 示例游戏"]').trigger('click'); await flushPromises()
    const add = document.querySelector<HTMLButtonElement>('.add-security-field')!
    for (let i = 0; i < 16; i++) { add.click(); await flushPromises() }
    expect(add.disabled).toBe(true)
    expect(document.querySelectorAll('.security-field-edit')).toHaveLength(16)
    document.querySelectorAll<HTMLButtonElement>('[aria-label="移除这条安全信息"]')[4].click(); await flushPromises()
    expect(document.querySelectorAll('.security-field-edit')).toHaveLength(15)
    expect(add.disabled).toBe(false)
    expect(mocks.call).not.toHaveBeenCalledWith('saveEntry', expect.anything())
  })

  it('expands contacts, copies the selected index natively, and folds them again on blur', async () => {
    const withContacts = structuredClone(state) as any
    withContacts.categories[0].entries[0].credentials[0].securityFields = [{kind:'phone',label:'',value:'+1 202 555 0100'},{kind:'email',label:'',value:'safe@example.com'}]
    mocks.call.mockImplementation(async (method: string) => method === 'getState' ? withContacts : { copied: true })
    await open()
    expect(wrapper.find('.security-info-list').exists()).toBe(false)
    await wrapper.get('.security-info-toggle').trigger('click')
    expect(wrapper.get('.security-info-list').text()).toContain('safe@example.com')
    await wrapper.get('[aria-label="复制安全邮箱 demo 第2条"]').trigger('click'); await flushPromises()
    expect(mocks.call).toHaveBeenCalledWith('copyCredential', {entryId:'entry',credentialId:'account',field:'securityField',securityFieldIndex:1})
    expect(wrapper.get('[aria-label="复制安全邮箱 demo 第2条"]').classes()).toContain('copied')
    expect(wrapper.get('.password-cell code').text()).toBe('***')
    window.dispatchEvent(new Event('blur')); await flushPromises()
    expect(wrapper.find('.security-info-list').exists()).toBe(false)
  })
})
