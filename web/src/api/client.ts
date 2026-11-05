import { fromRows } from '../stats/table'
import { columnType } from './columns'
import type { Filters, Notice, Query, QueryResult, Source, Vocabulary } from './types'
import type { ColumnType, Table } from '../stats/table'

export class ApiError extends Error {
  status: number
  body: string
  constructor(status: number, message: string, body: string) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.body = body
  }
}

const enc = (v: string[]) => v.map(encodeURIComponent).join(',')

function filterParams(filters: Filters): string[] {
  return Object.entries(filters)
    .filter(([, v]) => v.length > 0)
    // the server splits lists on ',' after decoding, so a value containing a literal comma cannot be expressed
    .map(([k, v]) => `f.${k}=${enc(v)}`)
}

export function buildUrl(q: Query): string {
  const parts: string[] = []
  let path: string
  if (q.endpoint === 'aggregate') {
    path = '/api/events/aggregate'
    parts.push(`grain=${q.grain}`, `from=${encodeURIComponent(q.from)}`, `to=${encodeURIComponent(q.to)}`, `bucket=${q.bucket}`)
    if (q.groupBy.length > 0) parts.push(`groupBy=${enc(q.groupBy)}`)
    parts.push(...filterParams(q.filters))
  } else if (q.endpoint === 'sample') {
    path = '/api/events/sample'
    parts.push(`from=${encodeURIComponent(q.from)}`, `to=${encodeURIComponent(q.to)}`, `n=${q.n}`, `seed=${q.seed}`, ...filterParams(q.filters))
  } else {
    path = '/api/rates'
    parts.push(`measure=${q.measure}`, `metric=${q.metric}`, `fromYear=${q.fromYear}`, `toYear=${q.toYear}`)
    if (q.groupBy.length > 0) parts.push(`groupBy=${enc(q.groupBy)}`)
    if (q.exposureSource) parts.push(`exposureSource=${encodeURIComponent(q.exposureSource)}`)
  }
  return parts.length ? `${path}?${parts.join('&')}` : path
}

async function getJson<T>(url: string, fetchFn: typeof fetch): Promise<T> {
  const res = await fetchFn(url)
  const text = await res.text()
  if (!res.ok) {
    let message = text
    try {
      const err = JSON.parse(text)?.error
      if (typeof err === 'string') message = err
    } catch {
      // not JSON: keep the response text
    }
    throw new ApiError(res.status, message, text)
  }
  return JSON.parse(text) as T
}

function tableOf(columns: string[], rows: unknown[][], meta: Record<string, string> = {}): Table {
  const types: Record<string, ColumnType> = {}
  for (const c of columns) types[c] = columnType(c)
  return fromRows(columns, rows, types, meta)
}

interface RatesResponse {
  columns: string[]
  rows: unknown[][]
  unmatched: { mode: string | null; year: number | null; vehicleClass: string | null; regulationClass: string | null; numerator: number; reason: string }[]
  unattributed: { mode: string | null; year: number | null; fatalities: number }[]
}

export async function runQuery(q: Query, fetchFn: typeof fetch = fetch): Promise<QueryResult> {
  const data = await getJson<Record<string, unknown>>(buildUrl(q), fetchFn)
  if (q.endpoint === 'rates') {
    const r = data as unknown as RatesResponse
    const table = tableOf(r.columns, r.rows, { endpoint: 'rates', measure: q.measure, metric: q.metric })
    const notices: Notice[] = [{ kind: 'scope', text: 'US events only' }]
    if (r.unmatched?.length) {
      const cols = ['mode', 'year', 'vehicle_class', 'regulation_class', 'numerator', 'reason']
      const rows = r.unmatched.map((u) => [u.mode, u.year, u.vehicleClass, u.regulationClass, u.numerator, u.reason])
      notices.push({ kind: 'unmatched', text: 'Numerators with no usable exposure', table: tableOf(cols, rows) })
    }
    if (r.unattributed?.length) {
      const cols = ['mode', 'year', 'fatalities']
      const rows = r.unattributed.map((u) => [u.mode, u.year, u.fatalities])
      notices.push({ kind: 'unattributed', text: 'Fatalities not attributed to a vehicle', table: tableOf(cols, rows) })
    }
    return { table, notices }
  }
  const r = data as { columns: string[]; rows: unknown[][]; grain?: string; total?: number; n?: number; seed?: number }
  if (q.endpoint === 'sample') {
    return {
      table: tableOf(r.columns, r.rows, { endpoint: 'sample', grain: 'vehicle' }),
      notices: [{ kind: 'sample', text: `${r.n} of ${r.total} matching rows, seed ${r.seed}` }],
    }
  }
  return { table: tableOf(r.columns, r.rows, { endpoint: 'aggregate', grain: q.grain }), notices: [] }
}

export function getSources(fetchFn: typeof fetch = fetch): Promise<Source[]> {
  return getJson<Source[]>('/api/sources', fetchFn)
}

export function getVocabulary(fetchFn: typeof fetch = fetch): Promise<Vocabulary> {
  return getJson<Vocabulary>('/api/vocabulary', fetchFn)
}
