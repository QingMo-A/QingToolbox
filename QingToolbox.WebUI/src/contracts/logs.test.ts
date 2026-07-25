import { describe,expect,it } from 'vitest'
import { isLogSnapshot } from './logs'
const valid={generatedAt:'2026-07-25T12:00:00Z',entries:[{timestamp:'2026-07-25T11:59:59Z',level:'Information',category:'App',message:'Ready'}]}
describe('log snapshot contract',()=>{
  it('accepts the minimal safe DTO',()=>expect(isLogSnapshot(valid)).toBe(true))
  it.each(['Debug','Trace','Fatal',1,null])('rejects unsupported level %s',level=>expect(isLogSnapshot({...valid,entries:[{...valid.entries[0],level}]})).toBe(false))
  it('rejects invalid generated timestamps',()=>expect(isLogSnapshot({...valid,generatedAt:'later'})).toBe(false))
  it('rejects invalid entry timestamps',()=>expect(isLogSnapshot({...valid,entries:[{...valid.entries[0],timestamp:'now'}]})).toBe(false))
  it.each(['category','message'])('requires string %s',field=>expect(isLogSnapshot({...valid,entries:[{...valid.entries[0],[field]:42}]})).toBe(false))
  it('rejects non-array entries',()=>expect(isLogSnapshot({...valid,entries:{}})).toBe(false))
})
