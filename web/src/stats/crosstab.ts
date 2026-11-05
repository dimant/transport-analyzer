import type { Bindings, Cell, Method, TableSpec } from './method'
import { categoryLabel, column, naturalCompare, type Table } from './table'

type ColumnFormat = TableSpec['columns'][number]['format']

export interface CrosstabCells {
  rowLabels: string[]
  colLabels: string[]
  cells: number[][]
  rowTotals: number[]
  colTotals: number[]
  total: number
}

export type CrosstabParams = { normalize: 'none' | 'row' | 'column' | 'total'; margins: boolean }
// valueFormat is the unnormalized cell format: integer for counts (or row counting), number otherwise
export type CrosstabResult = CrosstabCells & { normalize: string; margins: boolean; rowName: string; valueFormat: 'integer' | 'number' }

function labelsOf(t: Table, name: string): { labels: string[]; of: string[] } {
  const c = column(t, name)
  const of: string[] = []
  for (let i = 0; i < t.length; i++) {
    if (c.type === 'categorical') of.push(categoryLabel(c.values[i]))
    else of.push(Number.isNaN(c.values[i]) ? categoryLabel(null) : String(c.values[i]))
  }
  return { labels: [...new Set(of)].sort(naturalCompare), of }
}

export function crosstabCells(t: Table, row: string, col: string | undefined, value: string | undefined): CrosstabCells {
  const r = labelsOf(t, row)
  const c = col ? labelsOf(t, col) : { labels: ['total'], of: Array<string>(t.length).fill('total') }
  const v = value ? column(t, value) : undefined
  const rowIdx = new Map(r.labels.map((l, i) => [l, i]))
  const colIdx = new Map(c.labels.map((l, i) => [l, i]))
  const cells = r.labels.map(() => c.labels.map(() => 0))
  const rowTotals = r.labels.map(() => 0)
  const colTotals = c.labels.map(() => 0)
  let total = 0
  for (let i = 0; i < t.length; i++) {
    let x = 1
    if (v) {
      if (v.type === 'categorical') throw new Error(`value column "${v.name}" is not numeric`)
      x = v.values[i]
      if (Number.isNaN(x)) continue
    }
    const ri = rowIdx.get(r.of[i])!
    const ci = colIdx.get(c.of[i])!
    cells[ri][ci] += x
    rowTotals[ri] += x
    colTotals[ci] += x
    total += x
  }
  return { rowLabels: r.labels, colLabels: c.labels, cells, rowTotals, colTotals, total }
}

const div = (a: number, b: number) => (b === 0 ? 0 : a / b)

function normalized(c: CrosstabCells, mode: CrosstabParams['normalize']): CrosstabCells {
  if (mode === 'none') return c
  const cells = c.cells.map((row, i) =>
    row.map((x, j) => div(x, mode === 'row' ? c.rowTotals[i] : mode === 'column' ? c.colTotals[j] : c.total)),
  )
  // margins show normalized totals: the normalized axis totals are 1, the other axis shows shares of the grand total
  const share = (xs: number[]) => xs.map((x) => div(x, c.total))
  const ones = (xs: number[]) => xs.map((x) => (x === 0 ? 0 : 1))
  const rowTotals = mode === 'row' ? ones(c.rowTotals) : share(c.rowTotals)
  const colTotals = mode === 'column' ? ones(c.colTotals) : share(c.colTotals)
  return { ...c, cells, rowTotals, colTotals, total: c.total === 0 ? 0 : 1 }
}

export const crosstab: Method<CrosstabParams, CrosstabResult> = {
  id: 'crosstab',
  name: 'Cross-tabulation',
  description: 'Count rows, or sum a value, by one or two categorical columns.',
  inputs: [
    { key: 'row', label: 'Rows', types: ['categorical'] },
    { key: 'col', label: 'Columns', types: ['categorical'], optional: true },
    { key: 'value', label: 'Value (blank counts rows)', types: ['count', 'continuous', 'exposure'], optional: true },
  ],
  params: [
    { key: 'normalize', label: 'Normalize', kind: 'select', options: ['none', 'row', 'column', 'total'], default: 'none' },
    { key: 'margins', label: 'Show totals', kind: 'boolean', default: true },
  ],
  run(table: Table, b: Bindings, p: CrosstabParams): CrosstabResult {
    const row = b.row as string
    const col = (b.col as string | undefined) || undefined
    const value = (b.value as string | undefined) || undefined
    const cells = crosstabCells(table, row, col, value)
    const valueFormat = value && column(table, value).type !== 'count' ? 'number' : 'integer'
    return { ...normalized(cells, p.normalize), normalize: p.normalize, margins: p.margins, rowName: row, valueFormat }
  },
  render(r: CrosstabResult): TableSpec[] {
    // keys: 'row' = row label, 'c:<label>' = a column (so a category named 'row' cannot collide), '__total' = Total
    const format: ColumnFormat = r.normalize === 'none' ? r.valueFormat : 'percent'
    const columns: TableSpec['columns'] = [
      { key: 'row', label: r.rowName, format: 'text' },
      ...r.colLabels.map((l) => ({ key: 'c:' + l, label: l, format })),
    ]
    if (r.margins) columns.push({ key: '__total', label: 'Total', format })
    const rows: Record<string, Cell>[] = r.rowLabels.map((label, i) => {
      const out: Record<string, Cell> = { row: label }
      r.colLabels.forEach((l, j) => (out['c:' + l] = r.cells[i][j]))
      if (r.margins) out.__total = r.rowTotals[i]
      return out
    })
    if (r.margins) {
      const out: Record<string, Cell> = { row: 'Total' }
      r.colLabels.forEach((l, j) => (out['c:' + l] = r.colTotals[j]))
      out.__total = r.total
      rows.push(out)
    }
    return [{ kind: 'table', columns, rows }]
  },
}
