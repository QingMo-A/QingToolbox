import type { RgbColor } from './types'

/** Mirrors the colour helpers in the module's `renderer.rs`. */
type Rgb = [number, number, number]
const clamp = (value: number) => Math.round(Math.max(0, Math.min(255, Number(value) || 0)))
const luma = ([r, g, b]: Rgb) => r * 0.2126 + g * 0.7152 + b * 0.0722
const mix = (a: Rgb, b: Rgb, t: number): Rgb => [0, 1, 2].map(i => clamp(a[i] + (b[i] - a[i]) * t)) as Rgb
function saturate(color: Rgb, amount: number): Rgb {
  const grey = luma(color)
  return color.map(channel => clamp(grey + (channel - grey) * amount)) as Rgb
}
const css = (color: Rgb) => color.join(' ')

export function isLight(color: RgbColor): boolean {
  return luma([clamp(color.r), clamp(color.g), clamp(color.b)]) > 150
}

/**
 * Jelly's gel colours, as space-separated channels for `rgb(var(--x) / a)`:
 * the saturated body, its lit centre, its deep rim and the pooled light.
 */
export function gelPalette(color: RgbColor): { gel: string; lit: string; deep: string; glow: string } {
  const gel = saturate([clamp(color.r), clamp(color.g), clamp(color.b)], 1.35)
  return {
    gel: css(gel),
    lit: css(mix(gel, [255, 255, 255], 0.18)),
    deep: css(mix(gel, [0, 0, 0], 0.32)),
    glow: css(mix(gel, [255, 255, 255], 0.6)),
  }
}

/** Whether a colour is too grey for jelly to show off; samples then use candy. */
export function isMuted(color: RgbColor): boolean {
  const channels = [clamp(color.r), clamp(color.g), clamp(color.b)]
  return Math.max(...channels) - Math.min(...channels) < 40
}

/**
 * The jelly spring (`overlay::motion::spring`) as a CSS `linear()` easing, so
 * the settings preview wobbles exactly like the native window does.
 */
export function springEasing(frequency: number, samples = 48): string {
  const points: string[] = []
  for (let i = 0; i <= samples; i++) {
    const t = i / samples
    const value = t >= 1 ? 1 : 1 - Math.exp(-9.5 * t) * Math.cos(frequency * t)
    points.push(`${value.toFixed(4)} ${(t * 100).toFixed(1)}%`)
  }
  return `linear(${points.join(', ')})`
}
export const SPRING_MS = 560
export const SPRING_WIDTH = 10.5
export const SPRING_HEIGHT = 12

/** Fine monochrome grain for frosted glass, as a data-URI tile. */
export const FROST_GRAIN = `url("data:image/svg+xml,${encodeURIComponent("<svg xmlns='http://www.w3.org/2000/svg' width='96' height='96'><filter id='n'><feTurbulence type='fractalNoise' baseFrequency='.9' numOctaves='2' stitchTiles='stitch'/><feColorMatrix values='0 0 0 0 .5  0 0 0 0 .5  0 0 0 0 .5  0 0 0 .9 0'/></filter><rect width='96' height='96' filter='url(#n)' opacity='.5'/></svg>")}")`
