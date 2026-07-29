import { describe, expect, it, vi } from 'vitest'
import { ModuleClient } from './ModuleClient'
const snapshot={generatedAt:new Date().toISOString(),modules:[]}
describe('ModuleClient lifecycle commands',()=>{
  it('uses explicit lifecycle and startup authorization commands',async()=>{const request=vi.fn().mockResolvedValue(snapshot);const client=new ModuleClient({request} as any);await client.load('qing.load');await client.activate('qing.activate');await client.open('qing.open');await client.deactivate('qing.deactivate');await client.unload('qing.unload');await client.setStartupAuthorization('qing.startup',true);expect(request).toHaveBeenNthCalledWith(1,'modules.load',{moduleId:'qing.load'});expect(request).toHaveBeenNthCalledWith(2,'modules.activate',{moduleId:'qing.activate'});expect(request).toHaveBeenNthCalledWith(3,'modules.open',{moduleId:'qing.open'});expect(request).toHaveBeenNthCalledWith(4,'modules.deactivate',{moduleId:'qing.deactivate'});expect(request).toHaveBeenNthCalledWith(5,'modules.unload',{moduleId:'qing.unload'});expect(request).toHaveBeenNthCalledWith(6,'modules.setStartupAuthorization',{moduleId:'qing.startup',enabled:true})})
  it('requests native module import without a path and validates the result',async()=>{
    const result={disposition:'Cancelled',importedModuleId:null,snapshot}
    const request=vi.fn().mockResolvedValue(result)
    await expect(new ModuleClient({request} as any).importModule()).resolves.toEqual(result)
    expect(request).toHaveBeenCalledWith('modules.import',{})
    request.mockResolvedValue({disposition:'Imported',importedModuleId:null,snapshot})
    await expect(new ModuleClient({request} as any).importModule()).rejects.toThrow('validation failed')
    request.mockResolvedValue({...result,packagePath:'C:/private/module.qmod'})
    await expect(new ModuleClient({request} as any).importModule()).rejects.toThrow('validation failed')
  })
  it('sends module management commands with only the module ID',async()=>{
    const result={disposition:'Succeeded',snapshot}
    const request=vi.fn().mockResolvedValue(result)
    const client=new ModuleClient({request} as any)
    await expect(client.openDirectory('qing.folder')).resolves.toEqual(result)
    await expect(client.remove('qing.remove')).resolves.toEqual(result)
    expect(request).toHaveBeenNthCalledWith(1,'modules.openDirectory',{moduleId:'qing.folder'})
    expect(request).toHaveBeenNthCalledWith(2,'modules.remove',{moduleId:'qing.remove'})
    request.mockResolvedValue({...result,directoryPath:'C:/private'})
    await expect(client.openDirectory('qing.folder')).rejects.toThrow('validation failed')
  })
  it('sends update commands with only the module ID and validates complete snapshots',async()=>{
    const request=vi.fn().mockResolvedValue(snapshot)
    const client=new ModuleClient({request} as any)
    await client.checkUpdate('qing.check')
    await client.downloadUpdate('qing.download')
    expect(request).toHaveBeenNthCalledWith(1,'modules.checkUpdate',{moduleId:'qing.check'})
    expect(request).toHaveBeenNthCalledWith(2,'modules.downloadUpdate',{moduleId:'qing.download'})
    request.mockResolvedValue({generatedAt:new Date().toISOString(),modules:[{id:'incomplete'}]})
    await expect(client.downloadUpdate('qing.invalid')).rejects.toThrow('validation failed')
  })
})
