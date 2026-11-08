import { describe, it, expect } from 'vitest'
import { compile } from './math'
import { History, regularize, shapeEnd, snap, hit, plot, type Item } from './model'
describe('numeric grammar', () => {
  it('supports functions, constants, powers and unary precedence', () => { expect(compile('y=sin(pi/2)+x²')(3)).toBe(10); expect(compile('-2^2')(0)).toBe(-4); expect(compile('2^3^2')(0)).toBe(512); expect(compile('sqrt(abs(x))')(-9)).toBe(3) })
  it('rejects code, implicit multiplication and unbounded nesting', () => { for (const s of ['window.close()', 'x;alert(1)', '2x', '('.repeat(100)+'x'+')'.repeat(100), 'x'.repeat(257)]) expect(() => compile(s)).toThrow() })
  it('keeps math domain errors nonfinite, not crashes', () => { expect(Number.isNaN(compile('sqrt(x)')(-1))).toBe(true) })
})
describe('constrained drawing', () => {
  it('straightens near-collinear strokes, preserving endpoints', () => { expect(regularize([{x:0,y:0},{x:50,y:1},{x:100,y:0}])).toEqual([{x:0,y:0},{x:100,y:0}]) })
  it('fits a smooth curve through a bent gesture', () => { const p=regularize([{x:0,y:0},{x:25,y:32},{x:50,y:45},{x:75,y:32},{x:100,y:0}]); expect(p).toHaveLength(97); expect(p[48].y).toBeGreaterThan(35); expect(p[0]).toEqual({x:0,y:0}); expect(p[96]).toEqual({x:100,y:0}) })
  it('constrains angles and squares in every drag direction', () => { expect(snap({x:0,y:0},{x:100,y:2}).y).toBe(0); expect(shapeEnd({x:20,y:20},{x:5,y:10},true)).toEqual({x:5,y:5}) })
  it('handles stationary gestures without NaNs', () => { expect(regularize(Array(10).fill({x:2,y:2}))).toEqual([{x:2,y:2},{x:2,y:2}]) })
})
describe('objects and undo', () => {
  const line:Item={id:1,kind:'stroke',points:[{x:0,y:0},{x:100,y:100}],color:'#000000',width:2}
  it('hit tests the stroke, not just its bounding box', () => { expect(hit(line,{x:10,y:80})).toBe(false);expect(hit(line,{x:50,y:51})).toBe(true) })
  it('restores erased objects and invalidates redo on new edits', () => { const h=new History(); h.checkpoint();h.items=[line];h.checkpoint();h.items=[];h.undo();expect(h.items).toHaveLength(1);h.redo();expect(h.items).toHaveLength(0);h.undo();h.checkpoint();h.items=[];h.redo();expect(h.items).toHaveLength(0) })
  it('breaks function paths at asymptotes', () => { const points=plot({...line,kind:'graph',expression:'1/x',range:[-1,1,-5,5],w:400,h:300}); expect(points.some(p=>p===null)).toBe(true);expect(points.length).toBeLessThanOrEqual(2000) })
})
