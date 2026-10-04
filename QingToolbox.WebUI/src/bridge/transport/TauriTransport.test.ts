import { describe, expect, it, vi } from 'vitest'
import { TauriTransport, toWebModule } from './TauriTransport'
import type { ModuleSummary, ModuleRuntimeSnapshot, ModuleUpdateView } from './tauriTypes'

const module: ModuleSummary = { id: 'qing.launcher', name: 'Qing Launcher', description: null, version: '1', author: null, uiKind: 'Web', runtimeType: 'process', runtimeIsolation: null, source: 'bundled', iconDataUrl: null, valid: true, issues: [] }
const runtime = (state: ModuleRuntimeSnapshot['state']): ModuleRuntimeSnapshot => ({ moduleId: module.id, state, generation: 1, lastError: null })
const openMock = vi.hoisted(() => vi.fn())
const invokeMock = vi.hoisted(() => vi.fn(async (command: string) => {
  if (command === 'get_host_info') return { version: '0.3.1-alpha', deviceName: 'QING-PC', apiVersion: 1, environmentKind: 'Development', environmentDisplayName: 'QingToolbox [Dev]' }
  if (command === 'list_modules') return { payload: { modules: [] } }
  if (command === 'get_all_module_runtime') return []
  if (command === 'get_module_updates') return []
  if (command === 'get_settings') return { startupModuleIds: [] }
  if (command === 'inspect_module_package') return { id: 'qing.future', name: 'Future', version: '2.0.0', apiVersion: 2, hostApiVersion: 1, compatible: false, sha256: 'a'.repeat(64) }
  if (command === 'import_module') return { id: 'qing.future' }
  return {}
}))
vi.mock('@tauri-apps/api/core', () => ({ invoke: invokeMock }))
vi.mock('@tauri-apps/plugin-dialog', () => ({ open: openMock }))
describe('Tauri module action states', () => {
  it('preserves native structured module failures instead of stringifying them', async () => {
    invokeMock.mockRejectedValueOnce({ code: 'hotkeyUnavailable', message: 'Unable to register shortcut (1409).' })
    const response = await new TauriTransport().request({ protocolVersion: 4, requestId: 'enable', command: 'modules.activate', payload: { moduleId: module.id } })
    expect(response.success).toBe(false)
    expect(response.error).toEqual({ code: 'hotkeyUnavailable', message: 'Unable to register shortcut (1409).' })
  })
  it('routes native checks, per-module preferences, and verified auto-install downloads', async () => {
    for (const [command, native, payload] of [
      ['modules.checkUpdate', 'check_module_update', { moduleId: module.id }],
      ['modules.setUpdateCheck', 'set_module_update_check', { moduleId: module.id, enabled: false }],
      ['modules.downloadUpdate', 'download_module_update', { moduleId: module.id }],
    ] as const) {
      invokeMock.mockClear()
      const result = await new TauriTransport().request({ protocolVersion: 4, requestId: command, command, payload })
      expect(result.success).toBe(true)
      expect(invokeMock.mock.calls[0]).toEqual([native, payload])
      expect(invokeMock).toHaveBeenCalledWith('get_module_updates')
    }
    const invalid = await new TauriTransport().request({ protocolVersion: 4, requestId: 'bad', command: 'modules.setUpdateCheck', payload: { moduleId: module.id, enabled: 'false' } })
    expect(invalid.success).toBe(false)
  })
  it('projects backend update state rather than disabling all native module downloads', () => {
    expect(toWebModule(module)).toMatchObject({ updateStatus: 'NotChecked', canCheckForUpdate: true, downloadStatus: 'NotDownloaded' })
    const update: ModuleUpdateView = { moduleId: module.id, updateStatus: 'UpdateAvailable', targetVersion: '2.0.0', releaseNotes: 'Latest', isUpdateCheckEnabled: false, canCheckForUpdate: true, isUpdateCheckBusy: false, canDownloadUpdate: true, downloadStatus: 'Installing', isDownloadActive: true, downloadBytesReceived: 100, downloadExpectedBytes: 100, canInstallVerifiedUpdate: false }
    const { moduleId: _id, ...fields } = update
    expect(toWebModule({ ...module, source: 'user' }, runtime('running'), false, update)).toMatchObject({ ...fields, id: module.id, canOpen: true })
  })
  it('sends only the four update preference fields to native persisted settings', async () => {
    const transport = new TauriTransport()
    const payload = { checkHostUpdatesOnStartup: false, checkModuleUpdatesOnStartup: true, hostUpdateIntervalMinutes: 0, moduleUpdateIntervalMinutes: 30 }
    invokeMock.mockClear()
    expect((await transport.request({ protocolVersion: 4, requestId: 'prefs', command: 'settings.setUpdatePreferences', payload })).success).toBe(true)
    expect(invokeMock).toHaveBeenCalledWith('update_settings', { update: payload })
    invokeMock.mockClear()
    expect((await transport.request({ protocolVersion: 4, requestId: 'bad', command: 'settings.setUpdatePreferences', payload: { moduleUpdateDisabledIds: ['qing.test'] } })).success).toBe(false)
    expect(invokeMock).not.toHaveBeenCalled()
  })
  it('reports the native development environment instead of presenting it as Production', async () => {
    const response = await new TauriTransport().request({ protocolVersion: 4, requestId: 'snapshot', command: 'app.getSnapshot', payload: {} })
    expect(response.success).toBe(true)
    expect(response.payload).toMatchObject({ environmentKind: 'Development', environmentDisplayName: 'QingToolbox [Dev]', deviceName: 'QING-PC', apiVersion: 1 })
  })
  it('opens only the fixed project repository through the host command', async () => {
    invokeMock.mockClear()
    const response = await new TauriTransport().request({ protocolVersion: 4, requestId: 'repository', command: 'app.openRepository', payload: {} })
    expect(response.success).toBe(true)
    expect(invokeMock).toHaveBeenCalledWith('open_project_repository')
  })
  it('routes load, enable, disable, unload and delete to distinct host actions', async () => {
    for (const [command, native, extra] of [
      ['modules.load', 'start_module', {}],
      ['modules.activate', 'set_module_active', { active: true }],
      ['modules.deactivate', 'set_module_active', { active: false }],
      ['modules.unload', 'stop_module', {}],
      ['modules.remove', 'remove_module', {}],
    ] as const) {
      invokeMock.mockClear()
      const response = await new TauriTransport().request({ protocolVersion: 4, requestId: command, command, payload: { moduleId: module.id } })
      expect(response.success).toBe(true)
      expect(invokeMock.mock.calls[0]).toEqual([native, { moduleId: module.id, ...extra }])
    }
  })
  it('previews an incompatible package, then imports only after its opaque confirmation', async () => {
    openMock.mockResolvedValueOnce('C:/future.qmod')
    invokeMock.mockClear()
    const transport = new TauriTransport()
    const preview = await transport.request({ protocolVersion: 4, requestId: 'pick', command: 'modules.import', payload: {} })
    expect(preview.payload).toMatchObject({ disposition: 'RequiresConfirmation', apiVersion: 2, hostApiVersion: 1 })
    expect(invokeMock).not.toHaveBeenCalledWith('import_module', expect.anything())
    const token = (preview.payload as {token:string}).token
    await transport.request({ protocolVersion: 4, requestId: 'confirm', command: 'modules.confirmIncompatibleImport', payload: { token } })
    expect(invokeMock).toHaveBeenCalledWith('import_module', { sourcePath: 'C:/future.qmod', expectedSha256: 'a'.repeat(64), allowIncompatibleApi: true })
    const replay = await transport.request({ protocolVersion: 4, requestId: 'replay', command: 'modules.confirmIncompatibleImport', payload: { token } })
    expect(replay.success).toBe(false)
  })
  it('unloaded modules only offer load, not open or activate', () => {
    for (const state of [undefined, runtime('notStarted'), runtime('stopped'), runtime('failed')]) {
      expect(toWebModule(module, state)).toMatchObject({ canLoad: true, canOpen: false, canActivate: false })
    }
  })
  it('cannot open or load a starting module', () => {
    expect(toWebModule(module, runtime('starting'))).toMatchObject({ canLoad: false, canOpen: false, canActivate: false, isBusy: true })
  })
  it('can open running Web modules only', () => {
    expect(toWebModule(module, runtime('running'))).toMatchObject({ canLoad: false, canOpen: true, canActivate: false })
    expect(toWebModule({ ...module, valid: false }, runtime('running'))).toMatchObject({ canLoad: false, canOpen: false, isExecutionBlocked: true })
  })
  it('loaded and deactivated modules remain openable without active background work', () => {
    for (const state of ['loaded', 'deactivated'] as const) {
      expect(toWebModule(module, runtime(state))).toMatchObject({
        canLoad: false, canOpen: true, canActivate: true, canDeactivate: false, canUnload: true, isBusy: false,
      })
    }
    expect(toWebModule(module, runtime('running'))).toMatchObject({ canActivate: false, canDeactivate: true, canUnload: true })
  })
})
