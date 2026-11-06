import { describe, expect, it } from 'vitest'
import { buildDesign } from './design'
import { fromRows } from './table'

const types = { events: 'count', size: 'exposure', age: 'continuous' } as const

function tbl(cols: string[], rows: unknown[][]) {
  return fromRows(cols, rows, types)
}

describe('design', () => {
  it('buildDesign_referenceIsLargestSize', () => {
    const t = tbl(['g', 'events', 'size'], [['A', 10, 1000], ['B', 30, 1500]])
    const d = buildDesign(t, { response: 'events', size: 'size', sizeKind: 'exposure', covariates: ['g'] })
    expect(d.columns).toEqual([{ term: '(intercept)', level: null }, { term: 'g', level: 'A' }])
    expect(d.levels.find((l) => l.level === 'B')?.status).toBe('reference')
    expect(d.levels.find((l) => l.level === 'A')?.status).toBe('estimated')
    expect(Array.from(d.X.data)).toEqual([1, 1, 1, 0])
    expect(d.X.rows).toBe(2)
    expect(Array.from(d.y)).toEqual([10, 30])
    expect(Array.from(d.size)).toEqual([1000, 1500])
    expect(d.offset[0]).toBeCloseTo(Math.log(1000), 12)
  })

  it('buildDesign_dropsLevelWithoutEvents', () => {
    const t = tbl(['g', 'events', 'size'], [['A', 10, 1000], ['B', 30, 1500], ['C', 0, 500]])
    const d = buildDesign(t, { response: 'events', size: 'size', sizeKind: 'exposure', covariates: ['g'] })
    expect(d.levels.find((l) => l.level === 'C')).toEqual({ term: 'g', level: 'C', events: 0, size: 500, status: 'not_estimable' })
    expect(d.rows).toEqual([0, 1])
    expect(d.warnings).toEqual(['g=C: no events; not estimable'])
    expect(d.columns.map((c) => c.level)).toEqual([null, 'A'])
  })

  it('buildDesign_dropsUnusableRowsWithCounts', () => {
    const t = tbl(
      ['g', 'events', 'size', 'age'],
      [
        ['A', 5, 100, 1], ['B', 6, 200, 2], ['A', 1, 0, 1], ['A', 1, -3, 1], ['B', NaN, 10, 1],
        ['A', 1, NaN, 1], ['B', -1, 10, 1], ['A', 4, 100, NaN], ['B', 4, 100, NaN],
      ],
    )
    const d = buildDesign(t, { response: 'events', size: 'size', sizeKind: 'exposure', covariates: ['g', 'age', 'age'] })
    expect(d.rows).toEqual([0, 1])
    expect(d.warnings).toEqual([
      '4 rows dropped: missing or non-positive exposure',
      '1 row dropped: negative count',
      '2 rows dropped: missing age',
    ])
    const tr = buildDesign(
      tbl(['g', 'events', 'size'], [['A', 2, 10], ['B', 3, 10], ['B', 1, 0], ['A', 11, 10]]),
      { response: 'events', size: 'size', sizeKind: 'trials', covariates: ['g'] },
    )
    expect(tr.warnings).toEqual([
      '1 row dropped: missing or non-positive trials',
      '1 row dropped: successes above trials',
    ])
    expect(Array.from(tr.offset)).toEqual([0, 0])
  })

  it('buildDesign_nullCategoryIsALevel', () => {
    const t = tbl(['g', 'events', 'size'], [['A', 10, 1000], [null, 3, 100]])
    const d = buildDesign(t, { response: 'events', size: 'size', sizeKind: 'exposure', covariates: ['g'] })
    expect(d.levels.map((l) => l.level)).toEqual(['A', '(none)'])
    expect(d.columns[1]).toEqual({ term: 'g', level: '(none)' })
  })

  it('buildDesign_singleLevelLeftOut', () => {
    const t = tbl(['g', 'events', 'size', 'age'], [['A', 10, 1000, 3], ['A', 3, 100, 5]])
    const d = buildDesign(t, { response: 'events', size: 'size', sizeKind: 'exposure', covariates: ['g', 'age'] })
    expect(d.columns).toEqual([{ term: '(intercept)', level: null }, { term: 'age', level: null }])
    expect(d.warnings).toEqual(['g has one level left; not in the model'])
    expect(Array.from(d.X.data)).toEqual([1, 3, 1, 5])
  })

  it('buildDesign_trialsAllOrNone_notEstimable', () => {
    const t = tbl(['c', 'events', 'size'], [['0', 4, 4], ['1', 2, 10], ['2', 5, 20]])
    const d = buildDesign(t, { response: 'events', size: 'size', sizeKind: 'trials', covariates: ['c'] })
    expect(d.warnings).toEqual(['c=0: all or none had the outcome; not estimable'])
    expect(d.rows).toEqual([1, 2])
  })

  it('buildDesign_iteratesUntilStable', () => {
    // dropping g=C leaves h=y with no successes
    const t = tbl(
      ['g', 'h', 'events', 'size'],
      [['A', 'x', 2, 10], ['B', 'x', 3, 10], ['C', 'y', 5, 5], ['A', 'y', 0, 6]],
    )
    const d = buildDesign(t, { response: 'events', size: 'size', sizeKind: 'trials', covariates: ['g', 'h'] })
    expect(d.warnings).toEqual([
      'g=C: all or none had the outcome; not estimable',
      'h=y: no events; not estimable',
      'h has one level left; not in the model',
    ])
    expect(d.rows).toEqual([0, 1])
    expect(d.levels.find((l) => l.term === 'h' && l.level === 'y')?.events).toBe(0)
  })
})
