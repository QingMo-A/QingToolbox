// Deliberately small numeric grammar. Never eval/Function or execute scripts.
type Fn = (x: number) => number
const functions: Record<string, (v: number) => number> = { sin: Math.sin, cos: Math.cos, tan: Math.tan, asin: Math.asin, acos: Math.acos, atan: Math.atan, sqrt: Math.sqrt, abs: Math.abs, ln: Math.log, log: Math.log10, exp: Math.exp, floor: Math.floor, ceil: Math.ceil }
export function compile(source: string): Fn {
  if (!source.trim() || source.length > 256) throw new Error('请输入不超过 256 字符的函数。')
  const s = source.toLowerCase().replace(/^\s*y\s*=\s*/, '').replaceAll('π', 'pi').replaceAll('²', '^2').replaceAll('³', '^3')
  let pos = 0, depth = 0, nodes = 0
  const spaces = () => { while (/\s/.test(s[pos] ?? '') && pos < s.length) pos++ }
  const take = (c: string) => { spaces(); if (s[pos] === c) { pos++; return true } return false }
  function expression(): Fn { let a = term(); for (;;) { if (take('+')) { const l = a, r = term(); a = x => l(x) + r(x) } else if (take('-')) { const l = a, r = term(); a = x => l(x) - r(x) } else return a } }
  function term(): Fn { let a = unary(); for (;;) { if (take('*')) { const l = a, r = unary(); a = x => l(x) * r(x) } else if (take('/')) { const l = a, r = unary(); a = x => l(x) / r(x) } else return a } }
  function unary(): Fn { if (++nodes > 300 || ++depth > 40) throw new Error('表达式嵌套过深。'); try { if (take('+')) return unary(); if (take('-')) { const a = unary(); return x => -a(x) } return power() } finally { depth-- } }
  function power(): Fn { const a = atom(); if (take('^')) { const b = unary(); return x => a(x) ** b(x) } return a }
  function atom(): Fn {
    spaces()
    if (take('(')) { const a = expression(); if (!take(')')) throw new Error('缺少右括号。'); return a }
    const num = s.slice(pos).match(/^(?:\d+(?:\.\d*)?|\.\d+)(?:e[+-]?\d+)?/)
    if (num) { pos += num[0].length; const n = Number(num[0]); if (!Number.isFinite(n)) throw new Error('数值超出范围。'); return () => n }
    const word = s.slice(pos).match(/^[a-z]+/)
    if (word) { pos += word[0].length; const id = word[0]; if (id === 'x') return x => x; if (id === 'pi') return () => Math.PI; if (id === 'e') return () => Math.E; const fn = Object.hasOwn(functions,id) ? functions[id] : undefined; if (fn && take('(')) { const a = expression(); if (!take(')')) throw new Error('函数缺少右括号。'); return x => fn(a(x)) } }
    throw new Error(`无法解析第 ${pos + 1} 个字符；乘法请写 *，例如 2*x。`)
  }
  const result = expression(); spaces(); if (pos !== s.length) throw new Error(`第 ${pos + 1} 个字符无效；请显式使用 * 号。`)
  return result
}
