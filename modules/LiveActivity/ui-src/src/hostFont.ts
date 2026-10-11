// The native window and preview share the host's actual face. Imported fonts
// use the host's verified qfont route, never a module-selected filesystem URL.
let revision = 0
let key = ''
let registered: FontFace | null = null
export async function adoptFont(font: { family: string; resourceUrl: string | null }): Promise<void> {
  const next = JSON.stringify(font)
  if (next === key) return
  key = next
  const current = ++revision
  try {
    let family = font.family
    let face: FontFace | null = null
    if (font.resourceUrl) {
      if (!/^http:\/\/qfont\.localhost\/user-fonts\/font-[0-9a-f]{64}\.(ttf|otf|ttc)$/.test(font.resourceUrl)) throw new Error('Invalid host font route')
      face = await new FontFace('QingIslandHostFont', `url("${font.resourceUrl}")`).load()
      family = 'QingIslandHostFont'
    }
    if (current !== revision) return
    if (registered) document.fonts.delete(registered)
    if (face) document.fonts.add(face)
    registered = face
    document.documentElement.style.setProperty('--q-font-family', `${JSON.stringify(family)}, "Segoe UI", sans-serif`)
  } catch {
    if (current === revision) key = '' // retry a temporarily unavailable asset
  }
}
