import { crosstabCells } from './crosstab'
import type { Bindings, HeatmapSpec, Method } from './method'
import { formatPer } from './rate'
import { type Table } from './table'

export interface HeatmapResult {
  x: string
  y: string
  fillLabel: string
  logScale: boolean
  cells: { x: string; y: string; value: number | null }[]
}
export type HeatmapParams = { per: number; normalize: 'none' | 'row' | 'column'; logScale: boolean }

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
        if (dens) {
          const d = dens.cells[i][j]
          v = d > 0 ? (sums.cells[i][j] / d) * p.per : null
        } else if (p.normalize === 'none') {
          v = sums.cells[i][j]
        } else {
          // row = all cells sharing a y label; column = all cells sharing an x label
          const total = p.normalize === 'row' ? sums.colTotals[j] : sums.rowTotals[i]
          v = total === 0 ? null : sums.cells[i][j] / total
        }
        cells.push({ x: xl, y: yl, value: v })
      })
    })
    const base = value ?? 'count'
    return {
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
        data: r.cells.map((c) => ({ [r.x]: c.x, [r.y]: c.y, value: c.value })),
      },
    ]
  },
}
