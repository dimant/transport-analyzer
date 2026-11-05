import { crosstabCells } from './crosstab'
import { garwood } from './distributions'
import type { Bindings, HeatmapSpec, Method } from './method'
import { formatPer } from './rate'
import { categoryLabel, column, naturalCompare, type Table } from './table'

export interface HeatmapResult {
  x: string
  y: string
  fillLabel: string
  logScale: boolean
  format: HeatmapSpec['format']
  caption?: string
  // lo/hi are present on rate cells only (null when the cell has no exposure)
  cells: { x: string; y: string; value: number | null; lo?: number | null; hi?: number | null }[]
}
export type HeatmapParams = { per: number; normalize: 'none' | 'row' | 'column'; logScale: boolean; confidence: number }

// distinct values of a provenance column, or undefined when the table lacks it
function distinct(t: Table, name: string): string[] | undefined {
  const c = t.columns.find((x) => x.name === name)
  if (!c) return undefined
  const labels = new Set<string>()
  for (let i = 0; i < t.length; i++) labels.add(c.type === 'categorical' ? categoryLabel(c.values[i]) : String(c.values[i]))
  return [...labels].sort(naturalCompare)
}

const str = (v: string | string[] | undefined): string | undefined => (typeof v === 'string' && v !== '' ? v : undefined)

export const heatmap: Method<HeatmapParams, HeatmapResult> = {
  id: 'heatmap',
  name: 'Heatmap',
  description: 'Colour the cells of two categorical columns by a count, a sum, or a rate.',
  inputs: [
    { key: 'x', label: 'X axis', types: ['categorical'] },
    { key: 'y', label: 'Y axis', types: ['categorical'] },
    { key: 'value', label: 'Value (blank counts rows)', types: ['count', 'continuous', 'exposure'], optional: true },
    { key: 'denominator', label: 'Denominator (makes a rate)', types: ['exposure'], optional: true },
  ],
  params: [
    { key: 'per', label: 'Rate per', kind: 'number', default: 100000000 },
    { key: 'normalize', label: 'Normalize', kind: 'select', options: ['none', 'row', 'column'], default: 'none' },
    { key: 'confidence', label: 'Confidence', kind: 'number', default: 0.95 },
    { key: 'logScale', label: 'Log scale', kind: 'boolean', default: false },
  ],
  run(table: Table, b: Bindings, p: HeatmapParams): HeatmapResult {
    const x = b.x as string
    const y = b.y as string
    const value = str(b.value)
    const denominator = str(b.denominator)
    const present = crosstabCells(table, x, y, undefined)
    const sums = crosstabCells(table, x, y, value)
    const dens = denominator ? crosstabCells(table, x, y, denominator) : undefined
    const cells: HeatmapResult['cells'] = []
    present.rowLabels.forEach((xl, i) => {
      present.colLabels.forEach((yl, j) => {
        if (present.cells[i][j] === 0) return
        let v: number | null
        let lo: number | null | undefined
        let hi: number | null | undefined
        if (dens) {
          const d = dens.cells[i][j]
          const scale = p.per / d
          v = d > 0 ? sums.cells[i][j] * scale : null
          lo = hi = null
          if (d > 0) {
            const ci = garwood(Math.max(0, Math.round(sums.cells[i][j])), p.confidence)
            lo = ci.lo * scale
            hi = ci.hi * scale
          }
        } else if (p.normalize === 'none') {
          v = sums.cells[i][j]
        } else {
          // row = all cells sharing a y label; column = all cells sharing an x label
          const total = p.normalize === 'row' ? sums.colTotals[j] : sums.rowTotals[i]
          v = total === 0 ? null : sums.cells[i][j] / total
        }
        cells.push(lo === undefined ? { x: xl, y: yl, value: v } : { x: xl, y: yl, value: v, lo, hi })
      })
    })
    const base = value ?? 'count'
    const parts = [
      ['Exposure', distinct(table, 'exposure_source')],
      ['Provenance', distinct(table, 'provenance')],
    ].filter((x): x is [string, string[]] => x[1] !== undefined)
    const caption = denominator && parts.length ? parts.map(([l, v]) => `${l}: ${v.join(', ')}`).join(' · ') : undefined
    const valueType = value ? column(table, value).type : 'count'
    const format: HeatmapResult['format'] = denominator ? 'rate' : p.normalize !== 'none' ? 'percent' : valueType === 'count' ? 'integer' : 'number'
    return {
      format,
      ...(caption ? { caption } : {}),
      x,
      y,
      fillLabel: denominator ? `${base} per ${formatPer(p.per)} ${denominator}` : base,
      logScale: p.logScale,
      cells,
    }
  },
  render(r: HeatmapResult): HeatmapSpec[] {
    return [
      {
        kind: 'heatmap',
        x: r.x,
        y: r.y,
        fill: 'value',
        fillLabel: r.fillLabel,
        logScale: r.logScale,
        format: r.format,
        ...(r.caption ? { caption: r.caption } : {}),
        data: r.cells.map((c) => ({ [r.x]: c.x, [r.y]: c.y, value: c.value, ...(c.lo === undefined ? {} : { lo: c.lo, hi: c.hi ?? null }) })),
      },
    ]
  },
}
