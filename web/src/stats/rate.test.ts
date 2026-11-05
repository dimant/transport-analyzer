import { describe, expect, it } from 'vitest'
import { defaultParams, type IntervalSpec, type TableSpec } from './method'
import { formatPer, rate, type RateResult } from './rate'
import { methodById } from './registry'
import { fromRows } from './table'

const cols = ['mode', 'year', 'regulation_class', 'numerator', 'denominator', 'join_level', 'exposure_source', 'provenance']
const types = { year: 'categorical', numerator: 'count', denominator: 'exposure' } as const
const mk = (rows: unknown[][], meta: Record<string, string> = {}) => fromRows(cols, rows, types, meta)
const params = { per: 1e8, confidence: 0.95, logScale: true }
const near = (a: number | null, b: number) => {
  expect(a).not.toBeNull()
  expect(Math.abs((a as number) - b) / b).toBeLessThan(1e-6)
}
const b = (groups: string[], carry: string[] = []) => ({ numerator: 'numerator', denominator: 'denominator', groups, carry })

const part121 = mk([
  ['aviation', 2019, 'part_121', 4, 1.1e12, 'regulation_class', 'bts_t100', 'measured'],
  ['aviation', 2020, 'part_121', 0, 0.5e12, 'regulation_class', 'bts_t100', 'measured'],
  ['aviation', 2021, 'part_121', 0, 0.7e12, 'regulation_class', 'bts_t100', 'survey_estimate'],
  ['aviation', 2022, 'part_121', 0, 0.71e12, 'regulation_class', 'bts_t100', 'measured'],
  ['aviation', 2023, 'part_121', 0, 0.12e12, 'regulation_class', 'bts_t100', 'measured'],
  ['highway', 2019, null, 100, 3e12, 'mode', 'fhwa_vm1', 'measured'],
])

describe('rate', () => {
  it('rate_computesRateAndGarwoodInterval', () => {
    const r = rate.run(mk([['aviation', 2019, 'part_121', 4, 3.1e12, 'x', 'y', 'measured']]), b([]), params)
    const row = r.rows[0]
    near(row.rate, (4 / 3.1e12) * 1e8)
    near(row.lo, (1.089865 * 1e8) / 3.1e12)
    near(row.hi, (10.241589 * 1e8) / 3.1e12)
  })

  it('rate_zeroNumerator_hasUpperBound', () => {
    const r = rate.run(mk([['aviation', 2019, 'part_121', 0, 2e12, 'x', 'y', 'measured']]), b([]), params)
    expect(r.rows[0].rate).toBe(0)
    expect(r.rows[0].lo).toBe(0)
    near(r.rows[0].hi, (3.688879 * 1e8) / 2e12)
  })

  it('rate_poolsRowsWithSameGroupKey', () => {
    const r = rate.run(part121, b(['mode', 'regulation_class']), params)
    expect(r.rows).toHaveLength(2)
    const av = r.rows.find((x) => x.groups.mode === 'aviation')!
    expect(av.numerator).toBe(4)
    expect(av.denominator).toBeCloseTo(3.13e12, -3)
    expect(av.key).toBe('aviation · part_121')
  })

  it('rate_carryColumns_joinDistinctValues', () => {
    const r = rate.run(part121, b(['mode'], ['join_level', 'provenance']), params)
    const av = r.rows.find((x) => x.groups.mode === 'aviation')!
    expect(av.carry.join_level).toBe('regulation_class')
    expect(av.carry.provenance).toBe('measured + survey_estimate')
  })

  it('rate_nullGroup_labelledNone', () => {
    const r = rate.run(part121, b(['mode', 'regulation_class']), params)
    expect(r.rows.find((x) => x.groups.mode === 'highway')!.groups.regulation_class).toBe('(none)')
  })

  it('rate_zeroDenominator_nullRateWithWarning', () => {
    const r = rate.run(mk([['aviation', 2019, 'part_121', 2, 0, 'x', 'y', 'measured']]), b(['mode']), params)
    expect(r.rows[0].rate).toBeNull()
    expect(r.rows[0].lo).toBeNull()
    expect(r.rows[0].hi).toBeNull()
    expect(r.warnings.some((w) => w.includes('aviation'))).toBe(true)
  })

  it('rate_missingValues_skippedWithWarning', () => {
    const r = rate.run(
      mk([
        ['aviation', 2019, 'part_121', null, 1e12, 'x', 'y', 'measured'],
        ['aviation', 2020, 'part_121', 3, null, 'x', 'y', 'measured'],
        ['aviation', 2021, 'part_121', 3, 1e12, 'x', 'y', 'measured'],
      ]),
      b(['mode']),
      params,
    )
    expect(r.warnings).toContain('2 rows skipped: missing numerator or denominator')
    expect(r.rows[0].numerator).toBe(3)
  })

  it('rate_nonIntegerNumerator_warnsAndRoundsForInterval', () => {
    const r = rate.run(mk([['aviation', 2019, 'part_121', 3.6, 1e12, 'x', 'y', 'measured']]), b([]), params)
    expect(r.warnings.some((w) => w.includes('non-integer'))).toBe(true)
    near(r.rows[0].rate, 3.6 * 1e8 / 1e12)
    near(r.rows[0].hi, (10.241589 * 1e8) / 1e12)
  })

  it('rate_render_yearIsXAxis', () => {
    const r = rate.run(part121, b(['mode', 'regulation_class', 'year'], ['provenance']), params)
    const [table, spec] = rate.render(r) as [TableSpec, IntervalSpec]
    expect(table.kind).toBe('table')
    expect(table.columns.map((c) => c.key)).toEqual([
      'mode', 'regulation_class', 'year', 'numerator', 'denominator', 'rate', 'lo', 'hi', 'provenance',
    ])
    expect(spec.x).toBe('year')
    expect(spec.series).toBe('series')
    expect(spec.y).toBe('rate')
    expect(spec.data.some((d) => d.series === 'aviation · part_121')).toBe(true)
    const d = spec.data[0]
    for (const k of ['mode', 'year', 'rate', 'lo', 'hi', 'numerator', 'denominator', 'provenance']) expect(d).toHaveProperty(k)
  })

  it('rate_render_noYear_usesGroupLabelAsX', () => {
    const [, spec] = rate.render(rate.run(part121, b(['mode']), params)) as [TableSpec, IntervalSpec]
    expect(spec.x).toBe('group')
    expect(spec.series).toBeUndefined()
    expect(spec.data.map((d) => d.group)).toEqual(['aviation', 'highway'])
    const [, all] = rate.render(rate.run(part121, b([]), params)) as [TableSpec, IntervalSpec]
    expect(all.data[0].group).toBe('all')
  })

  it('rate_render_defaultsToLogScale', () => {
    expect(defaultParams(rate).logScale).toBe(true)
    const [, spec] = rate.render(rate.run(part121, b(['mode']), defaultParams(rate) as typeof params)) as [TableSpec, IntervalSpec]
    expect(spec.logScale).toBe(true)
  })

  it('rate_usesTableMetaLabels', () => {
    const t = mk([['aviation', 2019, 'part_121', 4, 1e12, 'x', 'y', 'measured']], {
      measure: 'occupant_fatalities',
      metric: 'passenger_miles',
    })
    const r: RateResult = rate.run(t, b([]), params)
    expect((rate.render(r)[1] as IntervalSpec).yLabel).toBe('occupant_fatalities per 100M passenger_miles')
  })

  it('rate_registered', () => {
    expect(methodById('rate')).toBe(rate)
  })

  it('formatPer_abbreviates', () => {
    expect([1e3, 1e5, 1e6, 1e8, 1e9, 250].map(formatPer)).toEqual(['1k', '100k', '1M', '100M', '1B', '250'])
  })
})

describe('rate.defaultBindings', () => {
  it('rate_defaultBindings_seedsNumeratorDenominatorAndCarry', () => {
    expect(rate.defaultBindings!(mk([]))).toEqual({
      numerator: 'numerator', denominator: 'denominator', carry: ['join_level', 'exposure_source', 'provenance'],
    })
  })

  it('rate_defaultBindings_omitsMissingColumns', () => {
    const t = fromRows(['mode', 'exposure_source', 'n'], [], { n: 'count' })
    expect(rate.defaultBindings!(t)).toEqual({ carry: ['exposure_source'] })
  })

  it('rate_defaultBindings_wrongTypeIsSkipped', () => {
    const t = fromRows(['numerator', 'denominator'], [], {})
    expect(rate.defaultBindings!(t)).toEqual({})
  })
})
