import { describe, expect, it, vi } from 'vitest'
import { HostUpdateClient } from './HostUpdateClient'

const snapshot={generatedAt:new Date().toISOString(),state:'UpdateAvailable',currentVersion:'0.2.1-alpha',latestVersion:'0.2.2-alpha',publishedAt:'',lastChecked:'',summary:'Update',showBanner:true,downloadState:'Idle',bytesReceived:0,expectedBytes:100,downloadError:'',canCheck:true,canDownload:true,canCancelDownload:false,canInstall:false,installationSupported:true,installMessage:''}
describe('HostUpdateClient',()=>{
  it.each([['getSnapshot','hostUpdate.getSnapshot'],['check','hostUpdate.check'],['download','hostUpdate.download'],['cancel','hostUpdate.cancel'],['install','hostUpdate.install']] as const)('validates %s snapshots',async(method,command)=>{
    const request=vi.fn(async()=>snapshot);const client=new HostUpdateClient({request} as never)
    expect(await client[method]()).toEqual(snapshot);expect(request).toHaveBeenCalledWith(command,{})
  })
  it('rejects malformed host update snapshots',async()=>{
    const client=new HostUpdateClient({request:vi.fn(async()=>({...snapshot,canInstall:'yes'}))} as never)
    await expect(client.getSnapshot()).rejects.toThrow('validation failed')
  })
})
