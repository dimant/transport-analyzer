import { matrix, type Mat } from './linalg'
import { categoryLabel, column, naturalCompare, type Table } from './table'

export interface DesignSpec { response: string; size: string; sizeKind: 'exposure' | 'trials'; covariates: string[] }
export interface DesignColumn { term: string; level: string | null }
export interface LevelInfo { term: string; level: string; events: number; size: number; status: 'reference' | 'estimated' | 'not_estimable' }
export interface Design {
  X: Mat
  y: Float64Array
  size: Float64Array
  offset: Float64Array
  rows: number[]
  columns: DesignColumn[]
  levels: LevelInfo[]
  warnings: string[]
}

const rowsWord = (n: number) => (n === 1 ? '1 row dropped' : `${n} rows dropped`)

function numeric(t: Table, name: string): Float64Array {
  const c = column(t, name)
  if (c.type === 'categorical') throw new Error(`column "${name}" is not numeric`)
  return c.values
}

export function buildDesign(table: Table, spec: DesignSpec): Design {
  const warnings: string[] = []
  const y = numeric(table, spec.response)
  const size = numeric(table, spec.size)
  const covariates = [...new Set(spec.covariates)].map((name) => {
    const c = column(table, name)
    return { name, c }
  })

  // Drop unusable rows, one reason per row, in the order the warnings are listed.
  let rows: number[] = []
  let badSize = 0
  let badResponse = 0
  let overTrials = 0
  for (let i = 0; i < table.length; i++) {
    if (Number.isNaN(y[i]) || Number.isNaN(size[i]) || size[i] <= 0) badSize++
    else if (y[i] < 0) badResponse++
    else if (spec.sizeKind === 'trials' && y[i] > size[i]) overTrials++
    else rows.push(i)
  }
  if (badSize) warnings.push(`${rowsWord(badSize)}: missing or non-positive ${spec.sizeKind}`)
  if (badResponse) warnings.push(`${rowsWord(badResponse)}: negative ${spec.sizeKind === 'trials' ? 'successes' : 'count'}`)
  if (overTrials) warnings.push(`${rowsWord(overTrials)}: successes above trials`)
  for (const { name, c } of covariates) {
    if (c.type === 'categorical') continue
    const before = rows.length
    rows = rows.filter((i) => !Number.isNaN(c.values[i]))
    if (rows.length < before) warnings.push(`${rowsWord(before - rows.length)}: missing ${name}`)
  }

  const label = (c: (typeof covariates)[number]['c'], i: number) =>
    c.type === 'categorical' ? categoryLabel(c.values[i]) : ''
  const categorical = covariates.filter((v) => v.c.type === 'categorical')
  const removed = new Map<string, LevelInfo>() // key term\0level
  const key = (term: string, level: string) => `${term}\0${level}`

  const totals = (c: (typeof covariates)[number]['c']) => {
    const m = new Map<string, { events: number; size: number }>()
    for (const i of rows) {
      const l = label(c, i)
      const t = m.get(l) ?? { events: 0, size: 0 }
      t.events += y[i]
      t.size += size[i]
      m.set(l, t)
    }
    return m
  }

  // First pass fixes the level universe; later passes remove not-estimable levels until stable.
  const universe = new Map<string, string[]>()
  for (const { name, c } of categorical) {
    universe.set(name, [...totals(c).keys()].sort(naturalCompare))
  }
  for (let changed = true; changed; ) {
    changed = false
    for (const { name, c } of categorical) {
      const t = totals(c)
      const drop: string[] = []
      for (const l of [...t.keys()].sort(naturalCompare)) {
        const { events, size: s } = t.get(l)!
        if (events === 0) {
          warnings.push(`${name}=${l}: no events; not estimable`)
        } else if (spec.sizeKind === 'trials' && events === s) {
          warnings.push(`${name}=${l}: all or none had the outcome; not estimable`)
        } else continue
        drop.push(l)
        removed.set(key(name, l), { term: name, level: l, events, size: s, status: 'not_estimable' })
      }
      if (drop.length) {
        rows = rows.filter((i) => !drop.includes(label(c, i)))
        changed = true
      }
    }
  }

  const levels: LevelInfo[] = []
  const columns: DesignColumn[] = [{ term: '(intercept)', level: null }]
  type Col = { fill: (i: number) => number }
  const cols: Col[] = [{ fill: () => 1 }]
  for (const { name, c } of covariates) {
    if (c.type !== 'categorical') {
      const values = c.values
      columns.push({ term: name, level: null })
      cols.push({ fill: (i) => values[i] })
      continue
    }
    const t = totals(c)
    const kept = [...t.keys()].sort(naturalCompare)
    const ref = kept.reduce<string | null>((best, l) => (best === null || t.get(l)!.size > t.get(best)!.size ? l : best), null)
    for (const l of universe.get(name)!) {
      const gone = removed.get(key(name, l))
      if (gone) levels.push(gone)
      else if (t.has(l)) {
        const { events, size: s } = t.get(l)!
        levels.push({ term: name, level: l, events, size: s, status: l === ref ? 'reference' : 'estimated' })
      }
    }
    if (kept.length < 2) {
      warnings.push(`${name} has one level left; not in the model`)
      continue
    }
    for (const l of kept) {
      if (l === ref) continue
      columns.push({ term: name, level: l })
      cols.push({ fill: (i) => (label(c, i) === l ? 1 : 0) })
    }
  }

  const n = rows.length
  const X = matrix(n, columns.length)
  const out = { y: new Float64Array(n), size: new Float64Array(n), offset: new Float64Array(n) }
  rows.forEach((i, r) => {
    cols.forEach((col, j) => { X.data[r * X.cols + j] = col.fill(i) })
    out.y[r] = y[i]
    out.size[r] = size[i]
    out.offset[r] = spec.sizeKind === 'exposure' ? Math.log(size[i]) : 0
  })
  return { X, ...out, rows, columns, levels, warnings }
}
