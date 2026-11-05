import { describe, expect, it } from 'vitest'
import { categoryLabel, column, fromRows, groupKey, naturalCompare } from './table'

describe('table', () => {
  it('fromRows_typesColumns', () => {
    const t = fromRows(['mode', 'numerator'], [['aviation', 3], ['highway', 5]], { numerator: 'count' })
    expect(t.length).toBe(2)
    const mode = column(t, 'mode')
    expect(mode.type).toBe('categorical')
    expect(mode.values).toEqual(['aviation', 'highway'])
    const num = column(t, 'numerator')
    expect(num.type).toBe('count')
    expect(num.values).toBeInstanceOf(Float64Array)
    expect(Array.from(num.values as Float64Array)).toEqual([3, 5])
  })

  it('fromRows_nullCategorical_isNull', () => {
    const t = fromRows(['a'], [[null], [undefined], [2022]], {})
    expect(column(t, 'a').values).toEqual([null, null, '2022'])
    expect(categoryLabel(null)).toBe('(none)')
    expect(categoryLabel('2022')).toBe('2022')
  })

  it('fromRows_nullNumeric_isNaN', () => {
    const t = fromRows(['n'], [[null], [undefined], [Infinity], [4]], { n: 'continuous' })
    const v = Array.from(column(t, 'n').values as Float64Array)
    expect(v.slice(0, 3).every(Number.isNaN)).toBe(true)
    expect(v[3]).toBe(4)
  })

  it('column_unknown_throws', () => {
    const t = fromRows(['a'], [], {})
    expect(() => column(t, 'x')).toThrow('no column "x"')
  })

  it('groupKey_joinsLabels', () => {
    const t = fromRows(['m', 'y', 'n'], [['air', null, NaN]], { n: 'count' })
    expect(groupKey(t, ['m', 'y'], 0)).toBe('air · (none)')
    expect(groupKey(t, ['n'], 0)).toBe('(none)')
  })

  it('naturalCompare_sortsNumbersNumerically', () => {
    expect(['10', '9', '(none)', '2'].sort(naturalCompare)).toEqual(['2', '9', '10', '(none)'])
    expect(['b', 'a'].sort(naturalCompare)).toEqual(['a', 'b'])
  })
})
