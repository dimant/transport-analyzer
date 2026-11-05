import { column, type ColumnType, type Table } from './table'

export interface InputSpec { key: string; label: string; types: ColumnType[]; multiple?: boolean; optional?: boolean }
export type ParamSpec =
  | { key: string; label: string; kind: 'number'; default: number }
  | { key: string; label: string; kind: 'boolean'; default: boolean }
  | { key: string; label: string; kind: 'select'; options: string[]; default: string }
export type Bindings = Record<string, string | string[] | undefined>
export type Cell = string | number | null
export interface TableSpec {
  kind: 'table'
  columns: { key: string; label: string; format: 'integer' | 'number' | 'rate' | 'percent' | 'text' }[]
  rows: Record<string, Cell>[]
}
export interface HeatmapSpec {
  kind: 'heatmap'
  x: string
  y: string
  fill: string
  fillLabel: string
  logScale: boolean
  // how cell labels and hovers are formatted
  format: 'integer' | 'number' | 'rate' | 'percent'
  // exposure source and provenance of a rate heatmap; shown under the figure
  caption?: string
  data: Record<string, Cell>[]
}
export interface IntervalSpec {
  kind: 'intervals'
  x: string
  y: string
  lo: string
  hi: string
  series?: string
  yLabel: string
  logScale: boolean
  data: Record<string, Cell>[]
}
export type RenderSpec = TableSpec | HeatmapSpec | IntervalSpec

export interface Method<P extends Record<string, unknown> = Record<string, unknown>, R = unknown> {
  id: string
  name: string
  description: string
  inputs: InputSpec[]
  params: ParamSpec[]
  // method syntax keeps parameter bivariance so any Method<P, R> is assignable to Method
  // optional starting bindings for a table; keeps method-specific column conventions out of the UI
  defaultBindings?(table: Table): Bindings
  run(table: Table, bindings: Bindings, params: P): R
  render(result: R): RenderSpec[]
}

export function defaultParams(m: Method): Record<string, unknown> {
  const out: Record<string, unknown> = {}
  for (const p of m.params) out[p.key] = p.default
  return out
}

export function validateBindings(m: Method, table: Table, b: Bindings): string[] {
  const errors: string[] = []
  for (const input of m.inputs) {
    const bound = b[input.key]
    const names = bound === undefined ? [] : Array.isArray(bound) ? bound : [bound]
    if (names.length === 0 || (typeof bound === 'string' && bound === '')) {
      if (!input.optional) errors.push(`${input.label}: choose a column`)
      continue
    }
    if (Array.isArray(bound) && !input.multiple) {
      errors.push(`${input.label}: takes one column`)
      continue
    }
    for (const name of names) {
      let type: ColumnType
      try {
        type = column(table, name).type
      } catch {
        errors.push(`${input.label}: column "${name}" is not in the table`)
        continue
      }
      if (!input.types.includes(type)) {
        errors.push(`${input.label}: column "${name}" is ${type}, needs one of ${input.types.join(', ')}`)
      }
    }
  }
  return errors
}
