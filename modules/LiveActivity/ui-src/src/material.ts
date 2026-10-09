import type { RgbColor } from './types'

/** Mirrors the colour and motion helpers in the module's renderer/overlay. */
const clamp = (value: number) => Math.round(Math.max(0, Math.min(255, Number(value) || 0)))
type Rgb = [number, number, number]
const rgb = (color: RgbColor): Rgb => [clamp(color.r), clamp(color.g), clamp(color.b)]
const lumaOf = ([r, g, b]: Rgb) => r * 0.2126 + g * 0.7152 + b * 0.0722
const mix = (a: Rgb, b: Rgb, t: number): Rgb => [0, 1, 2].map(i => clamp(a[i] + (b[i] - a[i]) * t)) as Rgb
const saturate = (color: Rgb, amount: number): Rgb => {
  const grey = lumaOf(color)
  return color.map(channel => clamp(grey + (channel - grey) * amount)) as Rgb
}
const channels = (color: Rgb) => color.join(' ')

export function luma(color: RgbColor): number {
  return lumaOf(rgb(color))
}
export function isLight(color: RgbColor): boolean {
  return luma(color) > 150
}
/** Too grey to show off jelly; samples then use a candy colour. */
export function isMuted(color: RgbColor): boolean {
  const c = rgb(color)
  return Math.max(...c) - Math.min(...c) < 40
}

/** `renderer::liquid_tint`: the strength floor (35%) is clear glass. */
export function liquidTint(opacity: number): number {
  return Math.max(0, Math.min(1, (opacity - 0.35) / 0.65)) * 0.55
}
/** Jelly's centre density: the strength maps to 55%..95%. */
export function jellyDensity(opacity: number): number {
  return 0.55 + 0.4 * Math.max(0, Math.min(1, (opacity - 0.35) / 0.65))
}
/** Jelly's colours (`gel`, `gel_lit`, `gel_deep`) as `r g b` channels. */
export function jellyPalette(color: RgbColor): { gel: string; lit: string; deep: string } {
  const gel = saturate(rgb(color), 1.3)
  return { gel: channels(gel), lit: channels(mix(gel, [255, 255, 255], 0.28)), deep: channels(mix(gel, [0, 0, 0], 0.28)) }
}

/**
 * The liquid glass lens as an `feDisplacementMap` source, mirroring the
 * native renderer: a convex bezel as deep as the corner (≤16 logical px)
 * that bends the view outward by up to 12 logical px at the rim, plus a 2.5%
 * magnification across the face.
 *
 * Browsers only refract what lies inside a backdrop-filtered element's own
 * box, and only that element's own border-radius clips the result, so a
 * page lens cannot look past its rim. With `margin` 0 the rim therefore bends
 * inward — it magnifies what lies just inside the edge — which reads as the
 * same thick, curved glass. The native window bends outward.
 */
export function liquidMap(width: number, height: number, radius: number, scale: number, margin: number): { href: string; range: number } {
  const w = Math.max(1, Math.round(width))
  const h = Math.max(1, Math.round(height))
  const W = w + margin * 2
  const H = h + margin * 2
  const bezel = Math.max(3 * scale, Math.min(radius, 16 * scale))
  const lens = 12 * scale
  const range = 2 * (lens + 0.025 * Math.max(w, h) / 2) + 2
  const canvas = document.createElement('canvas')
  canvas.width = W
  canvas.height = H
  const context = canvas.getContext('2d')
  if (!context) return { href: '', range: 0 }
  const image = context.createImageData(W, H)
  for (let y = 0; y < H; y++) {
    for (let x = 0; x < W; x++) {
      const dx = x + 0.5 - W / 2
      const dy = y + 0.5 - H / 2
      const qx = Math.abs(dx) - (w / 2 - radius)
      const qy = Math.abs(dy) - (h / 2 - radius)
      const distance = Math.hypot(Math.max(qx, 0), Math.max(qy, 0)) + Math.min(Math.max(qx, qy), 0) - radius
      let offsetX = 0
      let offsetY = 0
      if (distance <= 0.5) {
        const bevel = Math.max(0, Math.min(1, 1 + distance / bezel))
        let nx = 0
        let ny = 0
        if (qx > 0 && qy > 0) {
          const length = Math.hypot(qx, qy) || 1
          nx = Math.sign(dx || 1) * qx / length
          ny = Math.sign(dy || 1) * qy / length
        } else if (qx > qy) nx = Math.sign(dx || 1)
        else ny = Math.sign(dy || 1)
        const shift = lens * Math.pow(bevel, 1.6)
        const direction = margin > 0 ? 1 : -1
        offsetX = -dx * 0.025 + direction * nx * shift
        offsetY = -dy * 0.025 + direction * ny * shift
      }
      const at = (y * W + x) * 4
      image.data[at] = clamp((offsetX / range + 0.5) * 255)
      image.data[at + 1] = clamp((offsetY / range + 0.5) * 255)
      image.data[at + 2] = 128
      image.data[at + 3] = 255
    }
  }
  context.putImageData(image, 0, 0)
  return { href: canvas.toDataURL('image/png'), range }
}

/* ---- motion (overlay::motion) ---- */
const springAt = (t: number, decay: number, frequency: number) => t >= 1 ? 1 : 1 - Math.exp(-decay * t) * Math.cos(frequency * t)
const easeOut = (t: number) => 1 - Math.pow(1 - Math.min(1, Math.max(0, t)), 3)
function curve(at: (t: number) => number, samples = 48): string {
  const points: string[] = []
  for (let i = 0; i <= samples; i++) points.push(`${at(i / samples).toFixed(4)} ${(i / samples * 100).toFixed(1)}%`)
  return `linear(${points.join(', ')})`
}
/** Liquid glass flows: a smooth settle with a barely visible overshoot. */
export const FLUID = { ms: 420, easing: curve(t => springAt(t, 8, 7)) }
/**
 * Jelly: the axis changing most overshoots (~114% → 98% → 100%); the other
 * squashes against it — a little short while the first is long.
 */
export const JELLY = {
  ms: 640,
  primary: curve(t => springAt(t, 8.1, 13)),
  secondary: curve(t => easeOut(t) - 0.6 * (springAt(t, 8.1, 13) - easeOut(t))),
}
