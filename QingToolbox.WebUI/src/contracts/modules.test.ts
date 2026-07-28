import { describe, expect, it } from 'vitest'
import { isModuleManagementResult, isModuleSnapshot } from './modules'

const module = { id:'m', displayName:'M', displayDescription:'D', version:'1', author:'A', runtimeType:'InProcess', loadMode:'Manual', runtimeState:'NotLoaded', isValid:true, errorCount:0, errors:[], permissions:[], minimumHostVersion:'0.2', isUserInstalled:true, canRemove:true, canLoad:true, canActivate:false, canOpen:false, canDeactivate:false, canUnload:false, isBusy:false, isExecutionBlocked:false, isStartupEnabled:false, startupAuthorizationState:'NotEnabled', canChangeStartupAuthorization:true, isStartupAuthorizationBusy:false }
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
})
