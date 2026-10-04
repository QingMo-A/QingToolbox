import { invoke } from '@tauri-apps/api/core'
import { listen, type UnlistenFn } from '@tauri-apps/api/event'
import { open } from '@tauri-apps/plugin-dialog'
import { isRecord, type BridgeEvent, type BridgeRequest, type BridgeResponse } from '../../contracts/app'
import type { Transport } from './Transport'
import type { FontOption, HostUpdateSnapshot as TauriHostUpdateSnapshot, ModuleListPayload, ModuleRuntimeSnapshot, ModuleSummary, SessionLogSnapshot, SettingsSnapshot as TauriSettingsSnapshot, StartupRegistrationSnapshot } from './tauriTypes'

/** Adapts the migrated Rust/Tauri commands to the existing full Vue shell. */
export class TauriTransport implements Transport {
  readonly mode = 'WebView' as const
  private disposed = false
  private sessionToken: string | null = null
  private activationNonce: string | null = null
  private pendingModuleSelection: { token: string; sourcePath: string; sha256: string; kind: 'import' | 'replace'; moduleId: string } | null = null
  private readonly eventCleanups = new Set<() => void>()

  static isAvailable(): boolean {
    return typeof window !== 'undefined' && Boolean((window as Window & { __TAURI_INTERNALS__?: unknown }).__TAURI_INTERNALS__)
  }

  async request(message: BridgeRequest): Promise<BridgeResponse> {
    if (this.disposed) throw new Error('Bridge transport is disposed.')
    try { return this.ok(message, await this.dispatch(message)) }
    catch (error) { return this.fail(message, error) }
  }

  subscribe(listener: (event: BridgeEvent) => void): () => void {
    if (this.disposed) return () => undefined
    let cleanedUp = false
    let unlisten: UnlistenFn | null = null
    let cleanup: () => void
    cleanup = () => {
      if (cleanedUp) return
      cleanedUp = true
      unlisten?.()
      this.eventCleanups.delete(cleanup)
    }
    this.eventCleanups.add(cleanup)
    void listen<{ reason?: unknown; moduleId?: unknown; state?: unknown }>('qmod:module-state-changed', event => {
      if (cleanedUp || this.disposed) return
      const reason = typeof event.payload?.reason === 'string' ? event.payload.reason : 'module-state-changed'
      const moduleId = typeof event.payload?.moduleId === 'string' ? event.payload.moduleId : null
      const state = typeof event.payload?.state === 'string' ? event.payload.state : null
      listener({ protocolVersion: 4, event: 'app.hostEvent', payload: { name: 'module.changed', reason, moduleId, state } })
    }).then(value => {
      unlisten = value
      if (cleanedUp || this.disposed) value()
    }).catch(() => {
      this.eventCleanups.delete(cleanup)
    })
    return cleanup
  }
  dispose(): void {
    this.disposed = true
    this.sessionToken = null
    this.activationNonce = null
    this.pendingModuleSelection = null
    for (const cleanup of [...this.eventCleanups]) cleanup()
  }

  private async dispatch(message: BridgeRequest): Promise<unknown> {
    switch (message.command) {
      case 'web.ready': {
        const [host, listed, runtime] = await Promise.all([
          invoke<TauriHostInfo>('get_host_info'),
          invoke<{ payload: ModuleListPayload }>('list_modules'),
          invoke<ModuleRuntimeSnapshot[]>('get_all_module_runtime'),
        ])
        this.activationNonce = crypto.randomUUID() + crypto.randomUUID()
        return { activationNonce: this.activationNonce, snapshot: appSnapshot(host, listed.payload.modules, runtime) }
      }
      case 'app.ping': {
        if (message.payload.activationNonce === this.activationNonce) {
          this.sessionToken = crypto.randomUUID() + crypto.randomUUID(); this.activationNonce = null
          return { pong: true, activated: true, hostTime: new Date().toISOString(), sessionToken: this.sessionToken }
        }
        if (message.payload.sessionToken === this.sessionToken && this.sessionToken) return { pong: true, activated: true, hostTime: new Date().toISOString(), sessionToken: null }
        throw new Error('BridgeNotActivated: Tauri session is not activated.')
      }
      case 'app.getSnapshot': {
        const [host, listed, runtime] = await Promise.all([
          invoke<TauriHostInfo>('get_host_info'),
          invoke<{ payload: ModuleListPayload }>('list_modules'),
          invoke<ModuleRuntimeSnapshot[]>('get_all_module_runtime'),
        ])
        return appSnapshot(host, listed.payload.modules, runtime)
      }
      case 'app.openRepository': await invoke('open_project_repository'); return { opened: true }
      case 'modules.getSnapshot': return this.moduleSnapshot()
      case 'modules.import': return this.importModule()
      case 'modules.updateFromPicker': return this.updateModuleFromPicker(requiredString(message.payload.moduleId))
      case 'modules.confirmIncompatibleImport': return this.confirmIncompatibleImport(requiredString(message.payload.token))
      case 'modules.cancelPendingImport': this.cancelPendingImport(requiredString(message.payload.token)); return { cancelled: true }
      case 'modules.load': await invoke('start_module', { moduleId: requiredString(message.payload.moduleId) }); return this.moduleSnapshot()
      case 'modules.open': await invoke('open_module', { moduleId: requiredString(message.payload.moduleId) }); return this.moduleSnapshot()
      case 'modules.activate': await invoke('set_module_active', { moduleId: requiredString(message.payload.moduleId), active: true }); return this.moduleSnapshot()
      case 'modules.deactivate': await invoke('set_module_active', { moduleId: requiredString(message.payload.moduleId), active: false }); return this.moduleSnapshot()
      case 'modules.unload': await invoke('stop_module', { moduleId: requiredString(message.payload.moduleId) }); return this.moduleSnapshot()
      case 'modules.openDirectory':
        await invoke('open_module_directory', { moduleId: requiredString(message.payload.moduleId) })
        return { disposition: 'Succeeded', snapshot: await this.moduleSnapshot() }
      case 'modules.remove':
        await invoke('remove_module', { moduleId: requiredString(message.payload.moduleId) })
        return { disposition: 'Succeeded', snapshot: await this.moduleSnapshot() }
      case 'modules.setStartupAuthorization':
        await invoke('set_module_startup_authorization', { moduleId: requiredString(message.payload.moduleId), enabled: Boolean(message.payload.enabled) })
        return this.moduleSnapshot()
      case 'modules.checkUpdate':
        await invoke('check_module_update', { moduleId: requiredString(message.payload.moduleId) }); return this.moduleSnapshot()
      case 'modules.downloadUpdate':
        await invoke('download_module_update', { moduleId: requiredString(message.payload.moduleId) }); return this.moduleSnapshot()
      case 'modules.setUpdateCheck':
        if (typeof message.payload.enabled !== 'boolean') throw new Error('InvalidPayload: a boolean is required.')
        await invoke('set_module_update_check', { moduleId: requiredString(message.payload.moduleId), enabled: message.payload.enabled }); return this.moduleSnapshot()
      case 'modules.installVerifiedUpdate':
        throw new Error('ModuleUpdateUnavailable: native module downloads already include verified installation.')
      case 'settings.getSnapshot': return this.settingsSnapshot()
      case 'settings.setUpdatePreferences': {
        const allowed = ['checkHostUpdatesOnStartup', 'checkModuleUpdatesOnStartup', 'hostUpdateIntervalMinutes', 'moduleUpdateIntervalMinutes']
        if (Object.keys(message.payload).some(key => !allowed.includes(key))) throw new Error('InvalidPayload: unexpected fields.')
        return this.updateSettings(message.payload)
      }
      case 'settings.setLanguage': return this.updateSettings({ language: requiredString(message.payload.languageCode) })
      case 'settings.setAppearancePreset': return this.updateSettings({ appearancePresetId: requiredString(message.payload.appearancePresetId) })
      case 'settings.setThemeMode': return this.updateSettings({ themeMode: requiredString(message.payload.themeMode) })
      case 'settings.setMainWindowCloseBehavior': return this.updateSettings({ closeBehavior: closeBehaviorToRust(requiredString(message.payload.mainWindowCloseBehavior)) })
      case 'settings.setWindowSize': return this.updateSettings({ windowWidth: requiredDimension(message.payload.width, 760, 7680), windowHeight: requiredDimension(message.payload.height, 520, 4320) })
      case 'settings.getCurrentWindowSize':
        if (Object.keys(message.payload).length !== 0) throw new Error('InvalidPayload: unexpected fields.')
        return invoke<{ width: number; height: number }>('get_main_window_size')
      case 'settings.setStartupFullscreen':
        if (typeof message.payload.enabled !== 'boolean') throw new Error('InvalidPayload: a boolean is required.')
        return this.updateSettings({ startupFullscreen: message.payload.enabled })
      case 'settings.applyWindowSize':
        if (Object.keys(message.payload).length !== 0) throw new Error('InvalidPayload: unexpected fields.')
        await invoke('apply_saved_window_size'); return { applied: true }
      case 'settings.setStartupPresentationMode': return this.updateSettings({ startupPresentation: startupToRust(requiredString(message.payload.startupPresentationMode)) })
      case 'settings.setShowLogsInSidebar': return this.updateSettings({ showLogsInSidebar: Boolean(message.payload.showLogsInSidebar) })
      case 'settings.setLaunchAtLogin': return this.updateSettings({ launchAtLogin: Boolean(message.payload.enabled) })
      case 'settings.repairStartupRegistration': await invoke('repair_startup_registration'); return this.settingsSnapshot()
      case 'settings.refreshFonts': return this.settingsSnapshot()
      case 'settings.setFont': return this.withStartupStatus(invoke<TauriSettingsSnapshot>('set_font', { fontId: requiredString(message.payload.fontId) }))
      case 'settings.importFont': return this.importFont()
      case 'logs.getSnapshot': return invoke<SessionLogSnapshot>('get_session_logs')
      case 'hostUpdate.getSnapshot':
        return invoke<TauriHostUpdateSnapshot>('get_host_update_snapshot')
      case 'hostUpdate.install': return invoke<TauriHostUpdateSnapshot>('install_host_update')
      case 'hostUpdate.download': return invoke<TauriHostUpdateSnapshot>('download_host_update')
      case 'hostUpdate.cancel': return invoke<TauriHostUpdateSnapshot>('cancel_host_update')
      case 'hostUpdate.check': return invoke<TauriHostUpdateSnapshot>('check_host_update')
      default: throw new Error(`UnsupportedCommand: ${message.command}`)
    }
  }

  private async moduleSnapshot() {
    const [listed, runtime, settings, updates] = await Promise.all([invoke<{ payload: ModuleListPayload }>('list_modules'), invoke<ModuleRuntimeSnapshot[]>('get_all_module_runtime'), invoke<TauriSettingsSnapshot>('get_settings'), invoke<import('./tauriTypes').ModuleUpdateView[]>('get_module_updates')])
    const runtimeById = new Map(runtime.map(item => [item.moduleId, item]))
    const startupIds = new Set(settings.startupModuleIds)
    if (!Array.isArray(updates)) throw new Error('InvalidModuleUpdates: invalid native update snapshot.')
    const updateById = new Map(updates.map(update => [update.moduleId, update]))
    return { generatedAt: new Date().toISOString(), modules: listed.payload.modules.map(module => toWebModule(module, runtimeById.get(module.id), startupIds.has(module.id), updateById.get(module.id))) }
  }

  private async importModule() {
    this.pendingModuleSelection = null
    const selected = await open({ title: '导入 QingToolbox 模块', multiple: false, directory: false, filters: [{ name: 'QingToolbox module', extensions: ['qmod'] }] })
    if (!selected || Array.isArray(selected)) return { disposition: 'Cancelled', importedModuleId: null, snapshot: await this.moduleSnapshot() }
    const preview = await invoke<ModulePackagePreview>('inspect_module_package', { sourcePath: selected })
    if (!preview.compatible) return this.pendingPrompt('import', selected, preview)
    const imported = await invoke<{ id: string }>('import_module', { sourcePath: selected, expectedSha256: preview.sha256, allowIncompatibleApi: false })
    return { disposition: 'Imported', importedModuleId: imported.id, snapshot: await this.moduleSnapshot() }
  }

  private async updateModuleFromPicker(moduleId: string) {
    this.pendingModuleSelection = null
    const selected = await open({ title: '选择模块更新包', multiple: false, directory: false, filters: [{ name: 'QingToolbox module', extensions: ['qmod'] }] })
    if (!selected || Array.isArray(selected)) return { disposition: 'Cancelled', importedModuleId: null, snapshot: await this.moduleSnapshot() }
    const preview = await invoke<ModulePackagePreview>('inspect_module_package', { sourcePath: selected })
    if (preview.id !== moduleId) throw new Error('ModuleIdentityMismatch: selected package does not match the installed module.')
    if (!preview.compatible) return this.pendingPrompt('replace', selected, preview)
    const updated = await invoke<{ id: string }>('update_module', { moduleId, sourcePath: selected, expectedSha256: preview.sha256, allowIncompatibleApi: false })
    return { disposition: 'Imported', importedModuleId: updated.id, snapshot: await this.moduleSnapshot() }
  }

  private pendingPrompt(kind: 'import' | 'replace', sourcePath: string, preview: ModulePackagePreview) {
    const token = crypto.randomUUID()
    this.pendingModuleSelection = { token, sourcePath, sha256: preview.sha256, kind, moduleId: preview.id }
    return { disposition: 'RequiresConfirmation', token, moduleId: preview.id, moduleName: preview.name, apiVersion: preview.apiVersion, hostApiVersion: preview.hostApiVersion, operation: kind }
  }

  private async confirmIncompatibleImport(token: string) {
    const selection = this.pendingModuleSelection
    if (!selection || selection.token !== token) throw new Error('ImportConfirmationExpired: choose the package again.')
    this.pendingModuleSelection = null
    const parameters = { sourcePath: selection.sourcePath, expectedSha256: selection.sha256, allowIncompatibleApi: true }
    const imported = selection.kind === 'replace'
      ? await invoke<{ id: string }>('update_module', { moduleId: selection.moduleId, ...parameters })
      : await invoke<{ id: string }>('import_module', parameters)
    return { disposition: 'Imported', importedModuleId: imported.id, snapshot: await this.moduleSnapshot() }
  }

  private cancelPendingImport(token: string) {
    if (this.pendingModuleSelection?.token === token) this.pendingModuleSelection = null
  }

  private async importFont() {
    const selected = await open({ title: '导入字体', multiple: false, directory: false, filters: [{ name: 'Font files', extensions: ['ttf', 'otf', 'ttc'] }] })
    if (!selected || Array.isArray(selected)) return { disposition: 'Cancelled', snapshot: await this.settingsSnapshot() }
    const imported = await invoke<TauriSettingsSnapshot>('import_font', { sourcePath: selected })
    return { disposition: 'Imported', snapshot: await this.withStartupStatus(Promise.resolve(imported)) }
  }

  private async settingsSnapshot() {
    const [settings, startup] = await Promise.all([invoke<TauriSettingsSnapshot>('get_settings'), this.startupStatus()])
    return toWebSettings(settings, startup)
  }

  private async withStartupStatus(settingsPromise: Promise<TauriSettingsSnapshot>) {
    const [settings, startup] = await Promise.all([settingsPromise, this.startupStatus()])
    return toWebSettings(settings, startup)
  }

  private async startupStatus(): Promise<StartupRegistrationSnapshot | null> {
    try { return await invoke<StartupRegistrationSnapshot>('get_startup_registration_status') } catch { return null }
  }

  private async updateSettings(update: Record<string, unknown>) { return this.withStartupStatus(invoke<TauriSettingsSnapshot>('update_settings', { update })) }
  private ok(message: BridgeRequest, payload: unknown): BridgeResponse { return { protocolVersion: message.protocolVersion, requestId: message.requestId, success: true, payload, error: null } }
  private fail(message: BridgeRequest, error: unknown): BridgeResponse {
    if (isRecord(error) && typeof error.code === 'string' && typeof error.message === 'string') {
      return { protocolVersion: message.protocolVersion, requestId: message.requestId, success: false, payload: null, error: { code: error.code, message: error.message } }
    }
    const text = error instanceof Error ? error.message : String(error); const separator = text.indexOf(':')
    return { protocolVersion: message.protocolVersion, requestId: message.requestId, success: false, payload: null, error: { code: separator > 0 ? text.slice(0, separator) : 'TauriCommandFailed', message: separator > 0 ? text.slice(separator + 1).trim() : text } }
  }
}

function requiredString(value: unknown): string { if (typeof value !== 'string' || !value.trim()) throw new Error('InvalidPayload: a non-empty string is required.'); return value }
function requiredDimension(value: unknown, min: number, max: number): number { if (typeof value !== 'number' || !Number.isInteger(value) || value < min || value > max) throw new Error('InvalidPayload: window size is out of range.'); return value }
type ModulePackagePreview = { id: string; name: string; version: string; apiVersion: number; hostApiVersion: number; compatible: boolean; sha256: string }
type TauriHostInfo = { version: string; deviceName?: string | null; apiVersion?: number; backgroundUpdateChecks?: boolean; environmentKind?: 'Development' | 'Production'; environmentDisplayName?: string }
function appSnapshot(host: TauriHostInfo, modules: ModuleSummary[], runtime: ModuleRuntimeSnapshot[]) {
  return {
    backgroundUpdateChecks: host.backgroundUpdateChecks === true,
    environmentKind: host.environmentKind === 'Development' ? 'Development' : 'Production',
    environmentDisplayName: host.environmentDisplayName ?? 'QingToolbox',
    hostVersion: host.version,
    deviceName: host.deviceName ?? null,
    ...(Number.isInteger(host.apiVersion) && (host.apiVersion ?? 0) > 0 ? { apiVersion: host.apiVersion } : {}),
    protocolVersion: 4,
    totalModuleCount: modules.length,
    validModuleCount: modules.filter(item => item.valid).length,
    runningModuleCount: runtime.filter(item => item.state === 'running').length,
    generatedAt: new Date().toISOString(),
  }
}
export function toWebModule(module: ModuleSummary, runtime?: ModuleRuntimeSnapshot, startupEnabled = false, update?: import('./tauriTypes').ModuleUpdateView) {
  const state = runtime?.state === 'running' ? 'Running' : runtime?.state === 'loaded' ? 'Loaded' : runtime?.state === 'deactivated' ? 'Deactivated' : runtime?.state === 'starting' ? 'Starting' : runtime?.state === 'failed' ? 'Failed' : runtime?.state === 'stopped' ? 'Unloaded' : 'NotLoaded'; const valid = module.valid
  return { id: module.id, displayName: module.name, displayDescription: module.description ?? '', version: module.version, author: module.author ?? '', runtimeType: module.runtimeType ?? 'process', loadMode: 'Process', runtimeState: state, isValid: valid, errorCount: module.issues.length, errors: module.issues.map(issue => issue.message), permissions: [], minimumHostVersion: '', isUserInstalled: module.source === 'user', canRemove: module.source === 'user', canLoad: valid && (state === 'NotLoaded' || state === 'Unloaded' || state === 'Failed'), canActivate: valid && (state === 'Loaded' || state === 'Deactivated'), canOpen: valid && ['Loaded', 'Running', 'Deactivated'].includes(state) && module.uiKind === 'Web', canDeactivate: state === 'Running', canUnload: ['Loaded', 'Running', 'Deactivated'].includes(state), isBusy: state === 'Starting', isExecutionBlocked: !valid, isStartupEnabled: startupEnabled, startupAuthorizationState: valid ? startupEnabled ? 'Enabled' : 'NotEnabled' : 'Unavailable', canChangeStartupAuthorization: valid, isStartupAuthorizationBusy: false, updateStatus: update?.updateStatus ?? 'NotChecked', targetVersion: update?.targetVersion ?? null, releaseNotes: update?.releaseNotes ?? null, isFromStaleCache: false, isUpdateCheckEnabled: update?.isUpdateCheckEnabled ?? true, canCheckForUpdate: update?.canCheckForUpdate ?? true, isUpdateCheckBusy: update?.isUpdateCheckBusy ?? false, canDownloadUpdate: update?.canDownloadUpdate ?? false, downloadStatus: update?.downloadStatus ?? 'NotDownloaded', isDownloadActive: update?.isDownloadActive ?? false, downloadBytesReceived: update?.downloadBytesReceived ?? 0, downloadExpectedBytes: update?.downloadExpectedBytes ?? 0, canInstallVerifiedUpdate: update?.canInstallVerifiedUpdate ?? false, iconDataUrl: module.iconDataUrl }
}
function toWebSettings(value: TauriSettingsSnapshot, startup?: StartupRegistrationSnapshot | null) {
  const code = value.language === 'en-US' ? 'en-US' : value.language === 'zh-CN' ? 'zh-CN' : 'system'
  const fonts = (Array.isArray(value.fonts) ? value.fonts : []).map(toWebFont)
  const defaultFont: FontOption = { id: 'Default', source: 'default', displayName: 'Default', familyName: null, resourceUrl: null }
  const current = fonts.find(font => font.id === value.fontId) ?? defaultFont
  if (!fonts.some(font => font.id === defaultFont.id)) fonts.unshift(defaultFont)
  const registration = startup ?? { canConfigure: true, registered: value.launchAtLogin, canRepair: false, status: 'Unavailable', message: '无法读取登录启动注册状态。' }
  return { generatedAt: new Date().toISOString(), checkHostUpdatesOnStartup: value.checkHostUpdatesOnStartup ?? true, checkModuleUpdatesOnStartup: value.checkModuleUpdatesOnStartup ?? true, hostUpdateIntervalMinutes: value.hostUpdateIntervalMinutes ?? 360, moduleUpdateIntervalMinutes: value.moduleUpdateIntervalMinutes ?? 60, appearancePresetId: value.appearancePresetId, themeMode: value.themeMode, font: current, fonts, language: { code, effectiveCode: code === 'en-US' ? 'en-US' : 'zh-CN', displayName: code === 'en-US' ? 'English' : '系统默认', options: [{ code: 'system', displayName: 'System', nativeName: '系统默认' }, { code: 'zh-CN', displayName: 'Chinese', nativeName: '简体中文' }, { code: 'en-US', displayName: 'English', nativeName: 'English' }] }, showLogsInSidebar: value.showLogsInSidebar, mainWindowCloseBehavior: value.closeBehavior === 'exit' ? 'ExitApplication' : value.closeBehavior === 'tray' ? 'MinimizeToNotificationArea' : 'Ask', windowWidth: value.windowWidth, windowHeight: value.windowHeight, startupFullscreen: value.startupFullscreen, infoPopupCorner: value.infoPopupCorner ?? 'rightTop', infoPopupAnimation: value.infoPopupAnimation ?? true, infoPopupDurationMs: value.infoPopupDurationMs ?? 380, infoPopupDismissSeconds: value.infoPopupDismissSeconds ?? 15, closeBehaviorMessage: '', launchAtLogin: value.launchAtLogin, canConfigureLaunchAtLogin: registration.canConfigure, canRepairStartup: registration.canRepair, startupPresentationMode: value.startupPresentation === 'tray' ? 'FloatingBadge' : value.startupPresentation === 'minimized' ? 'Minimized' : 'MainWindow', startupBackend: 'Tauri autostart', startupStatus: registration.status, startupMessage: registration.message }
}
function toWebFont(font: FontOption): FontOption { return { id: font.id, source: font.source, displayName: font.displayName, familyName: font.familyName ?? null, resourceUrl: font.resourceUrl ?? null } }
function closeBehaviorToRust(value: string) { return value === 'ExitApplication' ? 'exit' : value === 'MinimizeToNotificationArea' ? 'tray' : 'ask' }
function startupToRust(value: string) { return value === 'FloatingBadge' ? 'tray' : value === 'Minimized' ? 'minimized' : 'main' }
