import type { Design } from './design'
import { normalQuantile, normalTwoSidedP } from './distributions'
import type { GlmFit } from './glm'
import { formatValue, type Format } from './format'
import type { Cell, ForestSpec, TableSpec } from './method'

export interface RegressionTerm { term: string; level: string; estimate: number | null; lo: number | null; hi: number | null; p: number | null; events: number; size: number; note: string }
// format defaults to 3 significant figures for numbers and text otherwise
export interface SummaryRow { label: string; value: Cell; format?: Format }
export interface RegressionResult {
  ratioName: 'rate ratio' | 'odds ratio'
  eventsLabel: string
  sizeLabel: string
  confidence: number
  terms: RegressionTerm[]
  reference: string // 'vehicle_class=light_duty, year=2019'
  summary: SummaryRow[]
  warnings: string[]
}

const INTERCEPT = '(intercept)'
const PER_UNIT = 'per unit'
const REFERENCE = 'reference'
const ALIASED = 'aliased with other covariates'
const FIT_FAILED = 'not estimable: fit did not converge'

export const validConfidence = (c: number) => c > 0 && c < 1
export const CONFIDENCE_WARNING = 'confidence must be between 0 and 1'
// Pearson χ² / residual df above this is reported as overdispersion
export const DISPERSION_LIMIT = 1.5

/**
 * One term per design level, and one per continuous covariate, as exp(β) with a Wald interval.
 * Intervals are null when confidence is outside (0, 1).
 */
export function termsFromFit(design: Design, fit: GlmFit, confidence: number): RegressionTerm[] {
  const z = validConfidence(confidence) ? normalQuantile(1 - (1 - confidence) / 2) : NaN
  const notEstimable = design.sizeKind === 'trials' ? 'not estimable: all or none had the outcome' : 'not estimable: no events'
  const colIndex = new Map(design.columns.map((c, j) => [`${c.term}\0${c.level ?? ''}`, j]))
  const blank = { estimate: null, lo: null, hi: null, p: null }
  const finiteOrNull = (v: number) => (Number.isFinite(v) ? v : null)
  const fromColumn = (j: number): Pick<RegressionTerm, 'estimate' | 'lo' | 'hi' | 'p' | 'note'> => {
    const b = fit.beta[j]
    const se = fit.se[j]
    if (fit.aliased[j]) return { ...blank, note: ALIASED }
    if (!Number.isFinite(b) || !Number.isFinite(se)) return { ...blank, note: FIT_FAILED }
    return {
      estimate: Math.exp(b),
      lo: finiteOrNull(Math.exp(b - z * se)),
      hi: finiteOrNull(Math.exp(b + z * se)),
      p: normalTwoSidedP(b / se),
      note: '',
    }
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
    if (l.status === 'reference') add({ ...base, estimate: 1, lo: null, hi: null, p: null, note: REFERENCE })
    else if (l.status === 'not_estimable') add({ ...base, ...blank, note: notEstimable })
    else {
      const j = colIndex.get(`${l.term}\0${l.level}`)
      add({ ...base, ...(j === undefined ? { ...blank, note: ALIASED } : fromColumn(j)) })
    }
  }
  // A term with no level left in the model is not a reference for anything.
  for (const terms of byTerm.values()) {
    const ref = terms.find((t) => t.note === REFERENCE)
    if (!ref) continue
    const others = terms.filter((t) => t !== ref)
    if (others.some((t) => t.note !== ALIASED && t.note !== notEstimable)) continue
    ref.estimate = null
    ref.note = others.some((t) => t.note === ALIASED) ? `${REFERENCE}; term ${ALIASED}` : `${REFERENCE}; term not in the model`
  }
  return [...byTerm.values()].flat()
}

/** The reference levels the ratios are relative to, as 'term=level, term=level'. */
export function referenceOf(terms: RegressionTerm[]): string {
  return terms.filter((t) => t.note === REFERENCE).map((t) => `${t.term}=${t.level}`).join(', ')
}

export function renderRegression(r: RegressionResult): [TableSpec, ForestSpec, TableSpec] {
  const terms: TableSpec = {
    kind: 'table',
    columns: [
      { key: 'term', label: 'term', format: 'text' },
      { key: 'level', label: 'level', format: 'text' },
      { key: 'estimate', label: r.ratioName, format: 'ratio' },
      { key: 'lo', label: 'lo', format: 'ratio' },
      { key: 'hi', label: 'hi', format: 'ratio' },
      { key: 'p', label: 'p', format: 'p' },
      { key: 'events', label: r.eventsLabel, format: 'integer' },
      { key: 'size', label: r.sizeLabel, format: 'integer' },
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
    xLabel: validConfidence(r.confidence) ? `${r.ratioName} (${Number((r.confidence * 100).toPrecision(12))}% CI)` : r.ratioName,
    caption: `relative to ${r.reference}`,
    data: r.terms
      .filter((t) => !t.note.startsWith(REFERENCE))
      .map((t) => ({ label: `${t.term}: ${t.level}`, estimate: t.estimate, lo: t.lo, hi: t.hi, note: t.note })),
  }
  const summary: TableSpec = {
    kind: 'table',
    columns: [
      { key: 'label', label: 'label', format: 'text' },
      { key: 'value', label: 'value', format: 'text' },
    ],
    rows: r.summary.map((s) => ({ label: s.label, value: formatValue(s.value, s.format ?? (typeof s.value === 'string' ? 'text' : 'rate')) })),
  }
  return [terms, forest, summary]
}
