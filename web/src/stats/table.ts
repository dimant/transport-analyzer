export type ColumnType = 'categorical' | 'count' | 'continuous' | 'exposure' | 'weight'
export type CategoricalColumn = { name: string; type: 'categorical'; values: (string | null)[] }
export type NumericColumn = { name: string; type: Exclude<ColumnType, 'categorical'>; values: Float64Array } // NaN = null
export type Column = CategoricalColumn | NumericColumn
export interface Table { columns: Column[]; length: number; meta: Record<string, string> }

const NONE = '(none)'

export function fromRows(
  columns: string[],
  rows: unknown[][],
  types: Record<string, ColumnType>,
  meta: Record<string, string> = {},
): Table {
  const cols: Column[] = columns.map((name, i) => {
    const type = types[name] ?? 'categorical'
    if (type === 'categorical') {
      const values = rows.map((r) => (r[i] === null || r[i] === undefined ? null : String(r[i])))
      return { name, type, values }
    }
    const values = new Float64Array(rows.length)
    rows.forEach((r, j) => {
      const v = r[i]
      values[j] = typeof v === 'number' && Number.isFinite(v) ? v : NaN
    })
    return { name, type, values }
  })
  return { columns: cols, length: rows.length, meta }
}

export function column(t: Table, name: string): Column {
  const c = t.columns.find((x) => x.name === name)
  if (!c) throw new Error(`no column "${name}"`)
  return c
}

export function categoryLabel(v: string | null): string {
  return v === null ? NONE : v
}

export function groupKey(t: Table, names: string[], row: number): string {
  return names
    .map((n) => {
      const c = column(t, n)
      if (c.type === 'categorical') return categoryLabel(c.values[row])
      const v = c.values[row]
      return Number.isNaN(v) ? NONE : String(v)
    })
    .join(' · ')
}

export function naturalCompare(a: string, b: string): number {
  if (a === b) return 0
  if (a === NONE) return 1
  if (b === NONE) return -1
  const na = Number(a)
  const nb = Number(b)
  if (a.trim() !== '' && b.trim() !== '' && Number.isFinite(na) && Number.isFinite(nb)) return na - nb
  return a.localeCompare(b)
}
