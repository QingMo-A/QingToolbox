import { compile } from './math'
export type Tool = 'pen' | 'line' | 'curve' | 'rect' | 'ellipse' | 'text' | 'graph' | 'select' | 'eraser'
export interface Settings { color: string; width: number; fontSize: number; text: string; expression: string; xMin: number; xMax: number; yMin: number; yMax: number; grid: boolean; tool: Tool; theme: string; appearancePreset: string }
export const defaults: Settings = { color: '#3478f6', width: 4, fontSize: 30, text: '', expression: 'sin(x)', xMin: -10, xMax: 10, yMin: -5, yMax: 5, grid: true, tool: 'pen', theme: 'system', appearancePreset: 'qing-default' }
export const colors = ['#3478f6', '#ef4444', '#f59e0b', '#facc15', '#22c55e', '#06b6d4', '#a855f7', '#ec4899', '#ffffff', '#161b26']
export const tools: { id: Tool; name: string; glyph: string }[] = [
  { id: 'pen', name: '画笔', glyph: '✎' }, { id: 'line', name: '直线', glyph: '╱' }, { id: 'curve', name: '曲线', glyph: '∿' }, { id: 'rect', name: '矩形', glyph: '▭' }, { id: 'ellipse', name: '椭圆', glyph: '◯' }, { id: 'text', name: '文字', glyph: 'T' }, { id: 'graph', name: '函数', glyph: 'ƒ' }, { id: 'select', name: '选择', glyph: '↖' }, { id: 'eraser', name: '橡皮', glyph: '⌫' },
]
export interface Point { x: number; y: number }
export interface Item { id: number; kind: 'stroke' | 'rect' | 'ellipse' | 'text' | 'graph'; points: Point[]; color: string; width: number; text?: string; fontSize?: number; w?: number; h?: number; expression?: string; range?: [number, number, number, number]; grid?: boolean }
export const clone = <T>(v: T): T => JSON.parse(JSON.stringify(v))
export function snap(a: Point, b: Point): Point { const d = Math.hypot(b.x - a.x, b.y - a.y), angle = Math.round(Math.atan2(b.y - a.y, b.x - a.x) / (Math.PI / 4)) * Math.PI / 4; return { x: a.x + d * Math.cos(angle), y: a.y + d * Math.sin(angle) } }
// Least-squares quadratic Bezier fit. Collinear gestures become an exact line;
// curved gestures become one smooth geometric curve, not a jagged polyline.
export function regularize(points: Point[]): Point[] {
  if (points.length < 3) return clone(points)
  const a = points[0], b = points[points.length - 1], chord = Math.hypot(b.x - a.x, b.y - a.y)
  const lengths = [0]; for (let i = 1; i < points.length; i++) lengths.push(lengths[i - 1] + Math.hypot(points[i].x - points[i - 1].x, points[i].y - points[i - 1].y))
  const total = lengths[lengths.length - 1]; if (total < 1) return [a, b]
  const deviation = Math.max(...points.map(p => chord > 0 ? Math.abs((b.x - a.x) * (a.y - p.y) - (a.x - p.x) * (b.y - a.y)) / chord : 0))
  if (chord > 0 && deviation < Math.max(3, chord * 0.055)) return [a, b]
  let cx = 0, cy = 0, denom = 0
  points.forEach((p, i) => { const t = lengths[i] / total, k = 2 * (1 - t) * t; cx += k * (p.x - (1 - t) ** 2 * a.x - t * t * b.x); cy += k * (p.y - (1 - t) ** 2 * a.y - t * t * b.y); denom += k * k })
  if (denom < 1e-6) return [a, b]
  const c = { x: cx / denom, y: cy / denom }
  return Array.from({ length: 97 }, (_, i) => { const t = i / 96; return { x: (1 - t) ** 2 * a.x + 2 * (1 - t) * t * c.x + t * t * b.x, y: (1 - t) ** 2 * a.y + 2 * (1 - t) * t * c.y + t * t * b.y } })
}
export function shapeEnd(a: Point, b: Point, square: boolean): Point { if (!square) return b; const size = Math.max(Math.abs(b.x - a.x), Math.abs(b.y - a.y)); return { x: a.x + Math.sign(b.x - a.x || 1) * size, y: a.y + Math.sign(b.y - a.y || 1) * size } }
export function bounds(item: Item) {
  if (item.kind === 'graph' || item.kind === 'text') { const p = item.points[0], size = item.fontSize ?? 30; return { x: p.x, y: p.y, w: item.w ?? Math.max(...(item.text ?? '').split('\n').map(s => [...s].reduce((n, c) => n + (/[^\x00-\xff]/.test(c) ? size : size * .62), 0)), size), h: item.h ?? (item.text ?? '').split('\n').length * size * 1.3 } }
  const xs = item.points.map(p => p.x), ys = item.points.map(p => p.y); return { x: Math.min(...xs), y: Math.min(...ys), w: Math.max(...xs) - Math.min(...xs), h: Math.max(...ys) - Math.min(...ys) }
}
function distance(p: Point, a: Point, b: Point) { const dx = b.x - a.x, dy = b.y - a.y, t = Math.max(0, Math.min(1, ((p.x - a.x) * dx + (p.y - a.y) * dy) / (dx * dx + dy * dy || 1))); return Math.hypot(p.x - a.x - t * dx, p.y - a.y - t * dy) }
export function hit(item: Item, p: Point, margin = 8): boolean {
  const r = bounds(item); if (p.x < r.x - margin || p.y < r.y - margin || p.x > r.x + r.w + margin || p.y > r.y + r.h + margin) return false
  if (item.kind === 'stroke') return item.points.some((b, i) => distance(p, item.points[Math.max(0, i - 1)], b) <= margin + item.width / 2)
  if (item.kind === 'rect') return Math.min(Math.abs(p.x - r.x), Math.abs(p.x - r.x - r.w), Math.abs(p.y - r.y), Math.abs(p.y - r.y - r.h)) <= margin + item.width / 2
  if (item.kind === 'ellipse') { const rx = r.w / 2 || 1, ry = r.h / 2 || 1; return Math.abs(Math.hypot((p.x - r.x - rx) / rx, (p.y - r.y - ry) / ry) - 1) * Math.min(rx, ry) <= margin + item.width / 2 }
  return true
}
export class History {
  items: Item[] = []; private undoStack: Item[][] = []; private redoStack: Item[][] = []
  checkpoint() { this.undoStack.push(clone(this.items)); this.redoStack = []; this.trim() }
  private trim() { const weight = (items:Item[]) => items.reduce((n,i)=>n+i.points.length+(i.text?.length??0)/8+1,0); let total=this.undoStack.reduce((n,v)=>n+weight(v),0); while(this.undoStack.length>60 || (total>120000 && this.undoStack.length>1)){total-=weight(this.undoStack.shift()!)} }
  undo() { const prev = this.undoStack.pop(); if (prev) { this.redoStack.push(clone(this.items)); this.items = prev } }
  redo() { const next = this.redoStack.pop(); if (next) { this.undoStack.push(clone(this.items)); this.items = next } }
}
const cache = new Map<string, ReturnType<typeof compile>>()
export function plot(item: Item): (Point | null)[] {
  const expression = item.expression ?? 'x'; let fn = cache.get(expression); if (!fn) { fn = compile(expression); if (cache.size > 100) cache.clear(); cache.set(expression, fn) }
  const [xmin, xmax, ymin, ymax] = item.range ?? [-10, 10, -5, 5], w = item.w ?? 480, h = item.h ?? 300, p = item.points[0]
  let previous: number | null = null
  return Array.from({ length: Math.min(2000, Math.max(200, Math.ceil(w * 2))) }, (_, i) => {
    // Fixed bound independent of malicious function growth / zoom level.
    const count = Math.min(2000, Math.max(200, Math.ceil(w * 2))), x = xmin + i / (count - 1) * (xmax - xmin), y = fn!(x), py = p.y + h - (y - ymin) / (ymax - ymin) * h
    if (!Number.isFinite(py) || py < p.y - h * 2 || py > p.y + h * 3) { previous = null; return null }
    if (previous !== null && Math.abs(py - previous) > h * .55) { previous = py; return null }
    previous = py; return { x: p.x + i / (count - 1) * w, y: py }
  })
}
