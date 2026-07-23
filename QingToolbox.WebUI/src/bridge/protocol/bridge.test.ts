import { describe, expect, it } from 'vitest'
import { MockTransport } from '../transport/MockTransport'
import { RequestClient } from './RequestClient'
import { EventDispatcher } from './EventDispatcher'
import { protocolVersion } from '../../contracts/app'
import { createPinia, setActivePinia } from 'pinia'
import { useAppStore } from '../../app/store'

describe('Qing Bridge frontend', () => {
  it('pings and returns an explicit mock snapshot', async () => { const c = new RequestClient(new MockTransport()); expect((await c.request<{pong:boolean}>('app.ping')).pong).toBe(true); expect((await c.request<{environmentKind:string}>('app.getSnapshot')).environmentKind).toBe('Mock') })
  it('rejects a mismatched response request id', async () => { const transport = new MockTransport(); transport.request = async request => ({ protocolVersion, requestId: request.requestId+'bad', success:true,payload:{},error:null }); await expect(new RequestClient(transport).request('app.ping')).rejects.toThrow('identity') })
  it('rejects protocol mismatch and unknown command', async () => { const transport = new MockTransport(); expect((await transport.request({protocolVersion:2,requestId:'x',command:'app.ping',payload:{}})).success).toBe(false); await expect(new RequestClient(transport).request('bad')).rejects.toThrow('UnknownCommand') })
  it('dispatches known events and safely ignores unknown events', () => { const d = new EventDispatcher(); let value=''; d.on('app.hostEvent', payload => value=String(payload)); d.dispatch({protocolVersion,event:'unknown',payload:'bad'}); expect(value).toBe(''); d.dispatch({protocolVersion,event:'app.hostEvent',payload:'ok'}); expect(value).toBe('ok') })
  it('reports unavailable WebView transport outside WebView', async () => { const transport = new MockTransport(); transport.request = async () => { throw new Error('Transport unavailable') }; await expect(new RequestClient(transport).request('app.ping')).rejects.toThrow('unavailable') })
  it('propagates a bounded transport timeout', async () => { const transport = new MockTransport(); transport.request = async () => { throw new Error('Bridge request timed out.') }; await expect(new RequestClient(transport).request('app.ping')).rejects.toThrow('timed out') })
  it('rebuilds projected state from a fresh snapshot after reload', () => { setActivePinia(createPinia()); const store = useAppStore(); const first = { environmentKind:'Mock', environmentDisplayName:'Mock A', hostVersion:'1', protocolVersion, totalModuleCount:1, validModuleCount:1, runningModuleCount:0, generatedAt:new Date().toISOString() }; store.rebuild(first); store.rebuild({ ...first, totalModuleCount: 4, generatedAt:new Date().toISOString() }); expect(store.snapshot?.totalModuleCount).toBe(4) })
})
