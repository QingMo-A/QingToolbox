import { describe, expect, it, vi } from 'vitest'
import { TauriTransport, toWebModule } from './TauriTransport'
import type { ModuleSummary, ModuleRuntimeSnapshot } from './tauriTypes'

const module: ModuleSummary = { id: 'qing.launcher', name: 'Qing Launcher', description: null, version: '1', author: null, uiKind: 'Web', runtimeType: 'process', runtimeIsolation: null, source: 'bundled', iconDataUrl: null, valid: true, issues: [] }
const runtime = (state: ModuleRuntimeSnapshot['state']): ModuleRuntimeSnapshot => ({ moduleId: module.id, state, generation: 1, lastError: null })
const invokeMock = vi.hoisted(() => vi.fn(async (command: string) => {
  if (command === 'list_modules') return { payload: { modules: [] } }
  if (command === 'get_all_module_runtime') return []
  if (command === 'get_settings') return { startupModuleIds: [] }
  return {}
}))
vi.mock('@tauri-apps/api/core', () => ({ invoke: invokeMock }))
describe('Tauri module action states', () => {
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
