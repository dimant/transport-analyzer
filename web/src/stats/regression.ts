import type { Design } from './design'
import { normalQuantile, normalTwoSidedP } from './distributions'
import type { GlmFit } from './glm'
import type { Cell, ForestSpec, TableSpec } from './method'

export interface RegressionTerm { term: string; level: string; estimate: number | null; lo: number | null; hi: number | null; p: number | null; events: number; size: number; note: string }
export interface RegressionResult {
  ratioName: 'rate ratio' | 'odds ratio'
  eventsLabel: string
  sizeLabel: string
  confidence: number
  terms: RegressionTerm[]
  reference: string // 'vehicle_class=light_duty, year=2019'
  summary: { label: string; value: Cell }[]
  warnings: string[]
}

const INTERCEPT = '(intercept)'
const PER_UNIT = 'per unit'

/** One term per design level, and one per continuous covariate, as exp(β) with a Wald interval. */
export function termsFromFit(design: Design, fit: GlmFit, confidence: number): RegressionTerm[] {
  const z = normalQuantile(1 - (1 - confidence) / 2)
  const notEstimable = design.sizeKind === 'trials' ? 'not estimable: all or none had the outcome' : 'not estimable: no events'
  const colIndex = new Map(design.columns.map((c, j) => [`${c.term}\0${c.level ?? ''}`, j]))
  const blank = { estimate: null, lo: null, hi: null, p: null }
  const fromColumn = (j: number): Pick<RegressionTerm, 'estimate' | 'lo' | 'hi' | 'p' | 'note'> => {
    const b = fit.beta[j]
    const se = fit.se[j]
    if (fit.aliased[j] || !Number.isFinite(b) || !Number.isFinite(se)) return { ...blank, note: 'aliased with other covariates' }
    return { estimate: Math.exp(b), lo: Math.exp(b - z * se), hi: Math.exp(b + z * se), p: normalTwoSidedP(b / se), note: '' }
  }

  let events = 0
  let size = 0
  for (let i = 0; i < design.y.length; i++) {
    events += design.y[i]
    size += design.size[i]
  }
  const byTerm = new Map<string, RegressionTerm[]>()
  const add = (t: RegressionTerm) => byTerm.set(t.term, [...(byTerm.get(t.term) ?? []), t])
  design.columns.forEach((c, j) => {
    if (c.level === null && c.term !== INTERCEPT) add({ term: c.term, level: PER_UNIT, events, size, ...fromColumn(j) })
  })
  for (const l of design.levels) {
    const base = { term: l.term, level: l.level, events: l.events, size: l.size }
    if (l.status === 'reference') add({ ...base, estimate: 1, lo: null, hi: null, p: null, note: 'reference' })
    else if (l.status === 'not_estimable') add({ ...base, ...blank, note: notEstimable })
    else {
      const j = colIndex.get(`${l.term}\0${l.level}`)
      add({ ...base, ...(j === undefined ? { ...blank, note: 'aliased with other covariates' } : fromColumn(j)) })
    }
  }
  return [...byTerm.values()].flat()
}

export function renderRegression(r: RegressionResult): [TableSpec, ForestSpec, TableSpec] {
  const terms: TableSpec = {
    kind: 'table',
    columns: [
      { key: 'term', label: 'term', format: 'text' },
      { key: 'level', label: 'level', format: 'text' },
      { key: 'estimate', label: r.ratioName, format: 'rate' },
      { key: 'lo', label: 'lo', format: 'rate' },
      { key: 'hi', label: 'hi', format: 'rate' },
      { key: 'p', label: 'p', format: 'rate' },
      { key: 'events', label: r.eventsLabel, format: 'integer' },
      { key: 'size', label: r.sizeLabel, format: 'number' },
      { key: 'note', label: 'note', format: 'text' },
    ],
    rows: r.terms.map((t) => ({ ...t })),
  }
  const forest: ForestSpec = {
    kind: 'forest',
    label: 'label',
    estimate: 'estimate',
    lo: 'lo',
    hi: 'hi',
    xLabel: `${r.ratioName} (${Number((r.confidence * 100).toPrecision(12))}% CI)`,
    caption: `relative to ${r.reference}`,
    data: r.terms
      .filter((t) => t.note !== 'reference')
      .map((t) => ({ label: `${t.term}: ${t.level}`, estimate: t.estimate, lo: t.lo, hi: t.hi, note: t.note })),
  }
  const summary: TableSpec = {
    kind: 'table',
    columns: [
      { key: 'label', label: 'label', format: 'text' },
      { key: 'value', label: 'value', format: 'rate' },
    ],
    rows: r.summary.map((s) => ({ label: s.label, value: s.value })),
  }
  return [terms, forest, summary]
}
