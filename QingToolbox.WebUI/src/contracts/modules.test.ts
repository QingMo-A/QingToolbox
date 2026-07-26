import { describe, expect, it } from 'vitest'
import { isModuleSnapshot } from './modules'
const module={id:'m',displayName:'M',displayDescription:'D',version:'1',author:'A',runtimeType:'InProcess',loadMode:'Manual',runtimeState:'NotLoaded',isValid:true,errorCount:0,errors:[],permissions:[],minimumHostVersion:'0.2',isUserInstalled:true,canLoad:true,canActivate:false,isBusy:false,isExecutionBlocked:false}
describe('module snapshot contract',()=>{it('requires the host capability projection',()=>{expect(isModuleSnapshot({generatedAt:new Date().toISOString(),modules:[module]})).toBe(true);const{canLoad,...missing}=module;expect(isModuleSnapshot({generatedAt:new Date().toISOString(),modules:[missing]})).toBe(false)})})
