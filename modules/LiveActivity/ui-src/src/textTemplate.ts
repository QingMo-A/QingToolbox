/** Same finite plain-text grammar as Rust; values come from the native resolver. */
export function renderText(template: string, values: Record<string, string | null>, fallback: string): string {
  let result = ''
  let rest = template
  while (rest.length) {
    if (rest.startsWith('{{') || rest.startsWith('}}')) {
      result += rest[0]
      rest = rest.slice(2)
      continue
    }
    if (rest.startsWith('{')) {
      const end = rest.indexOf('}')
      if (end >= 0) {
        const body = rest.slice(1, end)
        const divider = body.indexOf('|')
        const key = (divider < 0 ? body : body.slice(0, divider)).trim()
        if (Object.hasOwn(values, key)) {
          const missing = divider < 0 ? fallback : body.slice(divider + 1)
          result += values[key] ?? missing
          rest = rest.slice(end + 1)
          continue
        }
      }
    }
    result += rest[0]
    rest = rest.slice(1)
  }
  return Array.from(result).slice(0, 96).join('')
}

export function usesPlaceholder(template: string, key: string): boolean {
  let rest = template
  while (rest.length) {
    if (rest.startsWith('{{') || rest.startsWith('}}')) { rest = rest.slice(2); continue }
    if (rest.startsWith('{')) {
      const end = rest.indexOf('}')
      if (end >= 0) {
        const body = rest.slice(1, end)
        const name = body.split('|', 1)[0].trim()
        if (name === key) return true
        if (['time', 'date', 'stopwatch', 'countdown', 'codex.remaining', 'codex.reset', 'codex.primary.remaining', 'codex.primary.reset', 'codex.secondary.remaining', 'codex.secondary.reset', 'codex.updated'].includes(name)) {
          rest = rest.slice(end + 1)
          continue
        }
      }
    }
    rest = rest.slice(rest.codePointAt(0)! > 0xffff ? 2 : 1)
  }
  return false
}
