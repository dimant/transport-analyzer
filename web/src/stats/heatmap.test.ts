import { describe, expect, it } from 'vitest'
import { heatmap } from './heatmap'
import { methodById } from './registry'
import { fromRows } from './table'
import type { HeatmapSpec } from './method'

const counts = fromRows(
  ['period', 'vehicle_class', 'occupant_fatalities'],
  [
    [2022, 'helicopter', 100],
    [2022, 'helicopter', 1],
    [2022, 'airliner', 3],
    [2023, 'airliner', 5],
  ],
  { period: 'categorical', occupant_fatalities: 'count' },
)
const rates = fromRows(
  ['period', 'vehicle_class', 'numerator', 'denominator'],
  [
    [2022, 'light_duty', 10, 1e9],
    [2022, 'light_duty', 20, 2e9],
    [2022, 'heavy', 4, 0],
  ],
  { period: 'categorical', numerator: 'count', denominator: 'exposure' },
)
const dflt = { per: 100000000, normalize: 'none' as const, logScale: false }
const cellOf = (r: ReturnType<typeof heatmap.run>, x: string, y: string) => r.cells.find((c) => c.x === x && c.y === y)

describe('heatmap', () => {
  it('heatmap_countCells', () => {
    const r = heatmap.run(counts, { x: 'period', y: 'vehicle_class', value: 'occupant_fatalities' }, dflt)
    expect(cellOf(r, '2022', 'helicopter')?.value).toBe(101)
    expect(cellOf(r, '2023', 'helicopter')).toBeUndefined()
    expect(r.cells).toHaveLength(3)
  })

  it('heatmap_rateCells', () => {
    const r = heatmap.run(rates, { x: 'period', y: 'vehicle_class', value: 'numerator', denominator: 'denominator' }, dflt)
    expect(cellOf(r, '2022', 'light_duty')?.value).toBeCloseTo(1, 12)
  })

  it('heatmap_zeroDenominator_isNull', () => {
    const r = heatmap.run(rates, { x: 'period', y: 'vehicle_class', value: 'numerator', denominator: 'denominator' }, dflt)
    expect(cellOf(r, '2022', 'heavy')?.value).toBeNull()
  })

  it('heatmap_normalizeRow_rowSumsToOne', () => {
    const r = heatmap.run(counts, { x: 'period', y: 'vehicle_class', value: 'occupant_fatalities' }, { ...dflt, normalize: 'row' })
    const sum = r.cells.filter((c) => c.y === 'airliner').reduce((s, c) => s + (c.value ?? 0), 0)
    expect(sum).toBeCloseTo(1, 12)
    expect(cellOf(r, '2022', 'airliner')?.value).toBeCloseTo(3 / 8, 12)
  })

  it('heatmap_unboundValue_countsRows', () => {
    const r = heatmap.run(counts, { x: 'period', y: 'vehicle_class' }, dflt)
    expect(cellOf(r, '2022', 'helicopter')?.value).toBe(2)
  })

  it('heatmap_render_spec', () => {
    const m = methodById('heatmap')!
    const r = m.run(counts, { x: 'period', y: 'vehicle_class', value: 'occupant_fatalities' }, { ...dflt, logScale: true })
    const [s] = m.render(r) as HeatmapSpec[]
    expect(s.kind).toBe('heatmap')
    expect(s.x).toBe('period')
    expect(s.y).toBe('vehicle_class')
    expect(s.fill).toBe('value')
    expect(s.fillLabel).toBe('occupant_fatalities')
    expect(s.logScale).toBe(true)
    expect(s.data).toContainEqual({ period: '2022', vehicle_class: 'helicopter', value: 101 })
    const rr = m.run(rates, { x: 'period', y: 'vehicle_class', value: 'numerator', denominator: 'denominator' }, dflt)
    expect((m.render(rr)[0] as HeatmapSpec).fillLabel).toBe('numerator per 100M denominator')
  })
})
