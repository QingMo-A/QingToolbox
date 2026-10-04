export const SEARCH_COMMANDS = [
  { prefix: '/e', label: 'Everything', description: '文件和文件夹' },
  { prefix: '/e:f', label: 'Everything · 文件', description: '只搜索文件' },
  { prefix: '/e:d', label: 'Everything · 文件夹', description: '只搜索文件夹' },
] as const

export function commandSuggestions(text: string) {
  if (!text.startsWith('/') || /\s/.test(text)) return []
  return SEARCH_COMMANDS.filter(command => command.prefix.startsWith(text.toLowerCase()))
}

export function completeCommand(prefix: string): string {
  return `${prefix} `
}
