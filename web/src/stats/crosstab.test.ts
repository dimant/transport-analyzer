import { describe, expect, it } from 'vitest'
import { crosstab, crosstabCells } from './crosstab'
import { methodById } from './registry'
import { fromRows } from './table'
import type { TableSpec } from './method'

const t = fromRows(
  ['mode', 'vehicle_class', 'occupant_fatalities'],
  [
    ['aviation', 'airliner', 3],
    ['aviation', 'helicopter', 101],
    ['highway', 'bus', 19],
    ['highway', 'bus', 1],
    ['highway', null, 7],
  ],
  { occupant_fatalities: 'count' },
)

describe('crosstab', () => {
  it('crosstab_sumsValueByRowAndColumn', () => {
    const c = crosstabCells(t, 'mode', 'vehicle_class', 'occupant_fatalities')
    expect(c.rowLabels).toEqual(['aviation', 'highway'])
    expect(c.cells[c.rowLabels.indexOf('highway')][c.colLabels.indexOf('bus')]).toBe(20)
    expect(c.cells[c.rowLabels.indexOf('aviation')][c.colLabels.indexOf('helicopter')]).toBe(101)
    expect(c.total).toBe(131)
  })

  it('crosstab_noValue_countsRows', () => {
    const c = crosstabCells(t, 'mode', 'vehicle_class', undefined)
    expect(c.cells[c.rowLabels.indexOf('highway')][c.colLabels.indexOf('bus')]).toBe(2)
    expect(c.total).toBe(5)
  })

  it('crosstab_noCol_usesSingleTotalColumn', () => {
    const c = crosstabCells(t, 'mode', undefined, undefined)
    expect(c.colLabels).toEqual(['total'])
    expect(c.cells).toEqual([[2], [3]])
  })

  it('crosstab_nullCategory_labelledNone', () => {
    const c = crosstabCells(t, 'mode', 'vehicle_class', undefined)
    expect(c.colLabels).toContain('(none)')
    expect(c.colLabels[c.colLabels.length - 1]).toBe('(none)')
  })

  it('crosstab_skipsNaNValues', () => {
    const n = fromRows(['a', 'v'], [['x', 1], ['x', null], ['x', 2]], { v: 'continuous' })
    expect(crosstabCells(n, 'a', undefined, 'v').total).toBe(3)
  })

  it('crosstab_normalizeRow_rowsSumToOne', () => {
    const r = crosstab.run(t, { row: 'mode', col: 'vehicle_class', value: 'occupant_fatalities' }, { normalize: 'row', margins: true })
    for (const row of r.cells) expect(row.reduce((a, b) => a + b, 0)).toBeCloseTo(1)
    expect(r.rowTotals).toEqual([1, 1])
  })

  it('crosstab_normalizeColumnAndTotal_nonzeroShares', () => {
    const b = { row: 'mode', col: 'vehicle_class', value: 'occupant_fatalities' }
    const col = crosstab.run(t, b, { normalize: 'column', margins: true })
    const ci = col.colLabels.indexOf('bus')
    expect(col.cells[col.rowLabels.indexOf('highway')][ci]).toBeCloseTo(1, 12)
    expect(col.colTotals.every((x) => x === 1)).toBe(true)
    const ri = col.rowLabels.indexOf('aviation')
    expect(col.rowTotals[ri]).toBeCloseTo(104 / 131, 12)
    const tot = crosstab.run(t, b, { normalize: 'total', margins: true })
    expect(tot.cells[tot.rowLabels.indexOf('aviation')][tot.colLabels.indexOf('helicopter')]).toBeCloseTo(101 / 131, 12)
    expect(tot.total).toBe(1)
    expect(tot.cells.flat().reduce((a, x) => a + x, 0)).toBeCloseTo(1, 12)
  })

  it('crosstab_normalize_zeroDivisorGivesZero', () => {
    const z = fromRows(['a', 'v'], [['x', 0]], { v: 'count' })
    const r = crosstab.run(z, { row: 'a', value: 'v' }, { normalize: 'total', margins: true })
    expect(r.cells).toEqual([[0]])
  })

  it('crosstab_render_marginsAddTotalRowAndColumn', () => {
    const b = { row: 'mode', col: 'vehicle_class', value: 'occupant_fatalities' }
    const withM = crosstab.render(crosstab.run(t, b, { normalize: 'none', margins: true }))[0] as TableSpec
    expect(withM.rows[withM.rows.length - 1].row).toBe('Total')
    expect(withM.columns[withM.columns.length - 1].label).toBe('Total')
    expect(withM.columns[0].label).toBe('mode')
    const without = crosstab.render(crosstab.run(t, b, { normalize: 'none', margins: false }))[0] as TableSpec
    expect(without.rows.length).toBe(2)
    expect(without.columns.some((c) => c.label === 'Total')).toBe(false)
  })

  it('crosstab_render_formatFollowsNormalizeAndValueType', () => {
    const fmt = (b: Record<string, string | undefined>, normalize: string) => {
      const s = crosstab.render(crosstab.run(t, b, { normalize: normalize as 'none', margins: false }))[0] as TableSpec
      return s.columns[1].format
    }
    expect(fmt({ row: 'mode', value: 'occupant_fatalities' }, 'none')).toBe('integer')
    expect(fmt({ row: 'mode' }, 'none')).toBe('integer')
    expect(fmt({ row: 'mode', value: 'occupant_fatalities' }, 'row')).toBe('percent')
    const c = fromRows(['a', 'v'], [['x', 1.5]], { v: 'continuous' })
    const s = crosstab.render(crosstab.run(c, { row: 'a', value: 'v' }, { normalize: 'none', margins: false }))[0] as TableSpec
    expect(s.columns[1].format).toBe('number')
  })

  it('registry_findsCrosstab', () => {
    expect(methodById('crosstab')).toBe(crosstab)
    expect(methodById('nope')).toBeUndefined()
  })
})
