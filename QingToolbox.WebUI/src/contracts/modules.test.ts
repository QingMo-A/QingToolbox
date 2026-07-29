import { describe, expect, it } from 'vitest'
import { isModuleManagementResult, isModuleSnapshot, isModuleUpdateInstallResult } from './modules'

const module = { id:'m', displayName:'M', displayDescription:'D', version:'1', author:'A', runtimeType:'InProcess', loadMode:'Manual', runtimeState:'NotLoaded', isValid:true, errorCount:0, errors:[], permissions:[], minimumHostVersion:'0.2', isUserInstalled:true, canRemove:true, canLoad:true, canActivate:false, canOpen:false, canDeactivate:false, canUnload:false, isBusy:false, isExecutionBlocked:false, isStartupEnabled:false, startupAuthorizationState:'NotEnabled', canChangeStartupAuthorization:true, isStartupAuthorizationBusy: false, updateStatus: 'NotChecked', targetVersion: null, releaseNotes: null, isFromStaleCache: false, canCheckForUpdate: true, isUpdateCheckBusy: false, canDownloadUpdate: false, downloadStatus: 'NotDownloaded', isDownloadActive: false, downloadBytesReceived: 0, downloadExpectedBytes: 0, canInstallVerifiedUpdate:false }
const snapshot = { generatedAt:new Date().toISOString(), modules:[module] }

describe('module snapshot contract', () => {
  it('requires host management, lifecycle, and startup capabilities', () => {
    expect(isModuleSnapshot(snapshot)).toBe(true)
    for (const key of ['canRemove','canDeactivate','canUnload','isStartupEnabled','startupAuthorizationState','canChangeStartupAuthorization','isStartupAuthorizationBusy']) {
      const copy:any={...module}; delete copy[key]
      expect(isModuleSnapshot({generatedAt:new Date().toISOString(),modules:[copy]})).toBe(false)
    }
  })

  it('accepts only safe structured management results', () => {
    expect(isModuleManagementResult({ disposition:'Succeeded', snapshot })).toBe(true)
    expect(isModuleManagementResult({ disposition:'SucceededWithWarning', snapshot })).toBe(true)
    expect(isModuleManagementResult({ disposition:'Failed', snapshot })).toBe(false)
    expect(isModuleManagementResult({ disposition:'Succeeded', snapshot, modulePath:'C:/private' })).toBe(false)
  })

  it('strictly validates update states and finite non-negative transfer counts', () => {
    for (const key of ['updateStatus','downloadStatus','downloadBytesReceived','downloadExpectedBytes','canInstallVerifiedUpdate']) {
      const copy:any={...module}; delete copy[key]
      expect(isModuleSnapshot({generatedAt:new Date().toISOString(),modules:[copy]})).toBe(false)
    }
    expect(isModuleSnapshot({generatedAt:new Date().toISOString(),modules:[{...module,updateStatus:'Unknown'}]})).toBe(false)
    expect(isModuleSnapshot({generatedAt:new Date().toISOString(),modules:[{...module,downloadStatus:'Installed'}]})).toBe(false)
    expect(isModuleSnapshot({generatedAt:new Date().toISOString(),modules:[{...module,downloadBytesReceived:-1}]})).toBe(false)
    expect(isModuleSnapshot({generatedAt:new Date().toISOString(),modules:[{...module,downloadExpectedBytes:Number.NaN}]})).toBe(false)
  })

  it('accepts only safe structured verified update installation results', () => {
    const result={disposition:'Installed',sourceVersion:'1.0.0',targetVersion:'1.1.0',snapshot}
    expect(isModuleUpdateInstallResult(result)).toBe(true)
    expect(isModuleUpdateInstallResult({...result,disposition:'RolledBack'})).toBe(true)
    expect(isModuleUpdateInstallResult({...result,disposition:'RecoveryRequired'})).toBe(true)
    expect(isModuleUpdateInstallResult({...result,packagePath:'C:/private'})).toBe(false)
    expect(isModuleUpdateInstallResult({...result,targetVersion:''})).toBe(false)
  })
})
