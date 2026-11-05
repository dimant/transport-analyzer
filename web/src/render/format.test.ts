import { describe, expect, it } from 'vitest'
import { formatValue } from './format'

describe('format', () => {
  it('formatValue_cases', () => {
    expect(formatValue(null, 'rate')).toBe('—')
    expect(formatValue(NaN, 'number')).toBe('—')
    expect(formatValue(1234567, 'integer')).toBe('1,234,567')
    expect(formatValue(1234.56, 'number')).toBe('1,234.6')
    expect(formatValue(12, 'number')).toBe('12')
    expect(formatValue(0.000128, 'rate')).toBe('0.000128')
    expect(formatValue(0.6183, 'rate')).toBe('0.618')
    expect(formatValue(26.31, 'rate')).toBe('26.3')
    expect(formatValue(1180.4, 'rate')).toBe('1,180')
    expect(formatValue(0, 'rate')).toBe('0')
    expect(formatValue(2.5e-7, 'rate')).toMatch(/e-7$/)
    expect(formatValue(12.34, 'percent')).toBe('12.3%')
    expect(formatValue('abc', 'text')).toBe('abc')
    expect(formatValue(7, 'text')).toBe('7')
  })
})
