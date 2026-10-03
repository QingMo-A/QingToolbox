import { describe, expect, it } from 'vitest'
import { formatFileSize } from './fileSize'

describe('human-readable file sizes', () => {
  it.each([
    [0, '0 B'], [1, '1 B'], [1023, '1023 B'],
    [1024, '1 KB'], [1536, '1.5 KB'],
    [1024 ** 2, '1 MB'], [1234567, '1.18 MB'],
    [1024 ** 3, '1 GB'], [8 * 1024 ** 3, '8 GB'],
    [1024 ** 4, '1 TB'], [1024 ** 2 - 1, '1 MB'],
    [-1, '0 B'], [NaN, '0 B'], [Infinity, '0 B'],
  ])('formats %s as %s', (bytes, expected) => {
    expect(formatFileSize(bytes)).toBe(expected)
  })
})
