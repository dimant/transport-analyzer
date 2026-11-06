import { describe, expect, it } from 'vitest'
import { formatValue, ratioDigits } from './format'

describe('format', () => {
  it('formatValue_ratio_closeIntervalGetsDistinctDigits', () => {
    const peers = [1.0563, 1.0551, 1.0575]
    const shown = peers.map((v) => formatValue(v, 'ratio', peers))
    expect(new Set(shown).size).toBe(3)
    expect(shown).toEqual(['1.056', '1.055', '1.058'])
  })

  it('formatValue_ratio_wideIntervalKeepsThreeFigures', () => {
    const peers = [0.5, 0.24443036, 1.02284]
    expect(peers.map((v) => formatValue(v, 'ratio', peers))).toEqual(['0.5', '0.244', '1.02'])
    expect(formatValue(1, 'ratio', [1, null, null])).toBe('1')
    expect(formatValue(1.23456, 'ratio')).toBe('1.23')
  })

  it('ratioDigits_capsAtSix', () => {
    expect(ratioDigits([1.0000001, 1.0000002, 1.0000003])).toBe(6)
    expect(ratioDigits([1.0001, 1.0002, 1.0003])).toBe(5)
    expect(ratioDigits([2, 2])).toBe(6)
  })

  it('formatValue_p_underflowShowsFloor', () => {
    expect(formatValue(0, 'p')).toBe('< 1e-300')
    expect(formatValue(1e-310, 'p')).toBe('< 1e-300')
    expect(formatValue(3.2e-12, 'p')).toBe('3.20e-12')
    expect(formatValue(0.04567, 'p')).toBe('0.0457')
    expect(formatValue(null, 'p')).toBe('—')
  })
})
