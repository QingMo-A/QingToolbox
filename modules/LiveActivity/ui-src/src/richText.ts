/** Safe, finite Markdown dialect. Keep in step with src/rich_text.rs and shared vectors. */
export interface TextStyle { bold: boolean; italic: boolean; strike: boolean; code: boolean }
export type RichRun = { kind: 'text'; text: string; style: TextStyle } | { kind: 'icon'; name: string; style: TextStyle }
export interface RichLine { left: RichRun[]; right: RichRun[] }
export interface RichDocument { lines: RichLine[] }
export interface RichContent { compact: RichDocument; peek: RichDocument; expanded: RichDocument }
const normal = (): TextStyle => ({ bold: false, italic: false, strike: false, code: false })
type Token = RichRun | 'space' | 'newline'

export function renderRich(template: string, values: Record<string, string | null>, fallback: string): RichDocument {
  const tokens: Token[] = []
  let budget = 65536
  const literal = (text: string, style: TextStyle) => {
    let count = 0
    for (const ch of text) {
      if (count++ >= 256) break
      if (budget <= 0) break
      budget--
      if (ch === '\n') { tokens.push('newline'); continue }
      if (/[\u0000-\u001f\u007f-\u009f]/u.test(ch)) continue
      const last = tokens.at(-1)
      if (typeof last === 'object' && last.kind === 'text' && Object.keys(style).every(key => last.style[key as keyof TextStyle] === style[key as keyof TextStyle])) last.text += ch
      else tokens.push({ kind: 'text', text: ch, style: { ...style } })
    }
  }
  const closing = (text: string, marker: string): number => {
    let i = 0
    while (i < text.length) {
      const rest = text.slice(i)
      if (rest.startsWith('\\')) { i++; if (i < text.length) i += String.fromCodePoint(text.codePointAt(i)!).length }
      else if (rest.startsWith('{') && rest.includes('}')) i += rest.indexOf('}') + 1
      else if (rest.startsWith(marker)) return i
      else i += String.fromCodePoint(text.codePointAt(i)!).length
    }
    return -1
  }
  const parse = (text: string, style: TextStyle, depth: number): void => {
    let rest = text
    while (rest.length && budget > 0) {
      if (rest.startsWith('\\') && rest.length > 1 && '\\*_~`{}'.includes(rest[1])) {
        literal(rest[1], style); rest = rest.slice(2); continue
      }
      if (rest.startsWith('{{') || rest.startsWith('}}')) { literal(rest[0], style); rest = rest.slice(2); continue }
      if (rest.startsWith('{') && rest.includes('}')) {
        const end = rest.indexOf('}')
        const body = rest.slice(1, end)
        const divider = body.indexOf('|')
        const key = (divider < 0 ? body : body.slice(0, divider)).trim()
        if (body === 'space') { tokens.push('space'); rest = rest.slice(end + 1); continue }
        if (body === 'icon.clock' || body === 'icon.status') {
          tokens.push({ kind: 'icon', name: body.slice(5), style: { ...style } }); budget--; rest = rest.slice(end + 1); continue
        }
        if (Object.hasOwn(values, key)) literal(values[key] ?? (divider < 0 ? fallback : body.slice(divider + 1)), style)
        else literal(rest.slice(0, end + 1), style)
        rest = rest.slice(end + 1); continue
      }
      let matched = false
      if (depth < 8) {
        for (const marker of ['***', '___', '**', '__', '~~', '*', '_', '`']) {
          if (!rest.startsWith(marker)) continue
          const tail = rest.slice(marker.length)
          const end = closing(tail, marker)
          if (end <= 0) continue
          const nested = { ...style }
          if (marker === '***' || marker === '___') { nested.bold = true; nested.italic = true }
          else if (marker === '**' || marker === '__') nested.bold = true
          else if (marker === '~~') nested.strike = true
          else if (marker === '*' || marker === '_') nested.italic = true
          else nested.code = true
          if (nested.code) literal(tail.slice(0, end), nested)
          else parse(tail.slice(0, end), nested, depth + 1)
          rest = tail.slice(end + marker.length); matched = true; break
        }
      }
      if (matched) continue
      const ch = String.fromCodePoint(rest.codePointAt(0)!)
      literal(ch, style); rest = rest.slice(ch.length)
    }
  }
  parse(Array.from(template).slice(0, 1024).join(''), normal(), 0)
  const lines: RichLine[] = []
  let row: Token[] = []
  const flush = () => {
    if (lines.length >= 16) { row = []; return }
    const last = row.lastIndexOf('space')
    const line: RichLine = { left: [], right: [] }
    row.forEach((token, i) => { if (typeof token === 'object') (last >= 0 && i > last ? line.right : line.left).push(token) })
    lines.push(line); row = []
  }
  tokens.forEach(token => { if (token === 'newline') flush(); else row.push(token) })
  flush()
  let remaining = 256
  const limit = (runs: RichRun[]) => runs.filter(run => {
    if (remaining <= 0) return false
    if (run.kind === 'icon') remaining--
    else {
      const text = Array.from(run.text).slice(0, remaining)
      run.text = text.join(''); remaining -= text.length
      if (!text.length) return false
    }
    return true
  })
  lines.forEach(line => { line.right = limit(line.right); line.left = limit(line.left) })
  return { lines }
}

export function richEmpty(document: RichDocument): boolean { return document.lines.every(line => !line.left.length && !line.right.length) }
