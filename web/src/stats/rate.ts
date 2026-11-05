import { garwood } from './distributions'
import type { Bindings, Cell, IntervalSpec, Method, TableSpec } from './method'
import { column, groupKey, naturalCompare, type Table } from './table'

export interface RateRow {
  key: string
  groups: Record<string, string>
  numerator: number
  denominator: number
  rate: number | null
  lo: number | null
  hi: number | null
  carry: Record<string, string>
}
export interface RateResult {
  groupColumns: string[]
  per: number
  confidence: number
  logScale: boolean
  numeratorLabel: string
  denominatorLabel: string
  rows: RateRow[]
  warnings: string[]
}
export type RateParams = { per: number; confidence: number; logScale: boolean }

export function formatPer(per: number): string {
  for (const [size, suffix] of [[1e9, 'B'], [1e6, 'M'], [1e3, 'k']] as const) {
    if (per >= size && Number.isInteger(per / size)) return `${per / size}${suffix}`
  }
  return per.toLocaleString('en-US')
}

const asList = (v: string | string[] | undefined): string[] => (v === undefined || v === '' ? [] : Array.isArray(v) ? v : [v])
const TIME_COLUMNS = ['year', 'period']

interface Pool { groups: Record<string, string>; numerator: number; denominator: number; nonInteger: boolean; carry: Map<string, Set<string>> }

export const rate: Method<RateParams, RateResult> = {
  id: 'rate',
  name: 'Rate with exact interval',
  description: 'Events per unit of exposure with an exact (Garwood) Poisson confidence interval.',
  inputs: [
    { key: 'numerator', label: 'Events (numerator)', types: ['count'] },
    { key: 'denominator', label: 'Exposure (denominator)', types: ['exposure'] },
    { key: 'groups', label: 'Group by', types: ['categorical'], multiple: true, optional: true },
    { key: 'carry', label: 'Show alongside', types: ['categorical'], multiple: true, optional: true },
  ],
  params: [
    { key: 'per', label: 'Rate per', kind: 'number', default: 100000000 },
    { key: 'confidence', label: 'Confidence', kind: 'number', default: 0.95 },
    { key: 'logScale', label: 'Log scale', kind: 'boolean', default: true },
  ],
  defaultBindings(table: Table): Bindings {
    const has = (name: string, type: string) => table.columns.some((c) => c.name === name && c.type === type)
    const out: Bindings = {}
    if (has('numerator', 'count')) out.numerator = 'numerator'
    if (has('denominator', 'exposure')) out.denominator = 'denominator'
    // every rate is shown with where its exposure came from
    const carry = ['join_level', 'exposure_source', 'provenance'].filter((n) => has(n, 'categorical'))
    if (carry.length > 0) out.carry = carry
    return out
  },
  run(table: Table, b: Bindings, p: RateParams): RateResult {
    const nName = b.numerator as string
    const dName = b.denominator as string
    const groupColumns = asList(b.groups)
    const carryColumns = asList(b.carry)
    const nCol = column(table, nName)
    const dCol = column(table, dName)
    if (nCol.type === 'categorical' || dCol.type === 'categorical') throw new Error('numerator and denominator must be numeric')
    const labelOf = (name: string, i: number) => groupKey(table, [name], i)
    const pools = new Map<string, Pool>()
    const warnings: string[] = []
    let skipped = 0
    for (let i = 0; i < table.length; i++) {
      const n = nCol.values[i]
      const d = dCol.values[i]
      if (Number.isNaN(n) || Number.isNaN(d)) {
        skipped++
        continue
      }
      const key = groupColumns.length ? groupKey(table, groupColumns, i) : 'all'
      let pool = pools.get(key)
      if (!pool) {
        pool = {
          groups: Object.fromEntries(groupColumns.map((g) => [g, labelOf(g, i)])),
          numerator: 0,
          denominator: 0,
          nonInteger: false,
          carry: new Map(carryColumns.map((c) => [c, new Set<string>()])),
        }
        pools.set(key, pool)
      }
      pool.numerator += n
      pool.denominator += d
      if (!Number.isInteger(n)) pool.nonInteger = true
      for (const c of carryColumns) pool.carry.get(c)!.add(labelOf(c, i))
    }
    if (skipped > 0) warnings.push(`${skipped} rows skipped: missing numerator or denominator`)
    const rows: RateRow[] = []
    for (const [key, pool] of pools) {
      const carry = Object.fromEntries(
        carryColumns.map((c) => [c, [...pool.carry.get(c)!].sort(naturalCompare).join(' + ')]),
      )
      let r: number | null = null
      let lo: number | null = null
      let hi: number | null = null
      if (pool.denominator > 0) {
        if (pool.nonInteger || !Number.isInteger(pool.numerator)) {
          warnings.push(`${key}: non-integer numerator ${pool.numerator} rounded for the interval`)
        }
        const k = Math.max(0, Math.round(pool.numerator))
        const ci = garwood(k, p.confidence)
        const scale = p.per / pool.denominator
        r = pool.numerator * scale
        lo = ci.lo * scale
        hi = ci.hi * scale
      } else {
        warnings.push(`${key}: denominator is ${pool.denominator}, rate not computed`)
      }
      rows.push({ key, groups: pool.groups, numerator: pool.numerator, denominator: pool.denominator, rate: r, lo, hi, carry })
    }
    rows.sort((a, b2) => {
      for (const g of groupColumns) {
        const c = naturalCompare(a.groups[g], b2.groups[g])
        if (c !== 0) return c
      }
      return 0
    })
    return {
      groupColumns,
      per: p.per,
      confidence: p.confidence,
      logScale: p.logScale,
      numeratorLabel: table.meta.measure ?? nName,
      denominatorLabel: table.meta.metric ?? dName,
      rows,
      warnings,
    }
  },
  render(r: RateResult): [TableSpec, IntervalSpec] {
    const carryColumns = r.rows.length ? Object.keys(r.rows[0].carry) : []
    const tableSpec: TableSpec = {
      kind: 'table',
      columns: [
        ...r.groupColumns.map((g) => ({ key: g, label: g, format: 'text' as const })),
        { key: 'numerator', label: r.numeratorLabel, format: 'integer' },
        { key: 'denominator', label: r.denominatorLabel, format: 'number' },
        { key: 'rate', label: 'rate', format: 'rate' },
        { key: 'lo', label: 'lo', format: 'rate' },
        { key: 'hi', label: 'hi', format: 'rate' },
        ...carryColumns.map((c) => ({ key: c, label: c, format: 'text' as const })),
      ],
      rows: r.rows.map((row) => ({
        ...row.groups,
        numerator: row.numerator,
        denominator: row.denominator,
        rate: row.rate,
        lo: row.lo,
        hi: row.hi,
        ...row.carry,
      })),
    }
    const xCol = r.groupColumns.find((g) => TIME_COLUMNS.includes(g))
    const rest = r.groupColumns.filter((g) => g !== xCol)
    const data = r.rows.map((row) => {
      const d: Record<string, Cell> = { ...row.groups, ...row.carry }
      if (xCol) {
        if (rest.length) d.series = rest.map((g) => row.groups[g]).join(' · ')
      } else {
        d.group = row.key
      }
      d.numerator = row.numerator
      d.denominator = row.denominator
      d.rate = row.rate
      d.lo = row.lo
      d.hi = row.hi
      return d
    })
    const spec: IntervalSpec = {
      kind: 'intervals',
      x: xCol ?? 'group',
      y: 'rate',
      lo: 'lo',
      hi: 'hi',
      yLabel: `${r.numeratorLabel} per ${formatPer(r.per)} ${r.denominatorLabel}`,
      logScale: r.logScale,
      data,
    }
    if (xCol && rest.length) spec.series = 'series'
    return [tableSpec, spec]
  },
}
