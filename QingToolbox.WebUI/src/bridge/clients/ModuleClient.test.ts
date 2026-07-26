import { describe, expect, it, vi } from 'vitest'
import { ModuleClient } from './ModuleClient'
const snapshot={generatedAt:new Date().toISOString(),modules:[]}
describe('ModuleClient lifecycle commands',()=>{it('uses explicit load and activate commands',async()=>{const request=vi.fn().mockResolvedValue(snapshot);const client=new ModuleClient({request} as any);await client.load('qing.load');await client.activate('qing.activate');expect(request).toHaveBeenNthCalledWith(1,'modules.load',{moduleId:'qing.load'});expect(request).toHaveBeenNthCalledWith(2,'modules.activate',{moduleId:'qing.activate'})})})
