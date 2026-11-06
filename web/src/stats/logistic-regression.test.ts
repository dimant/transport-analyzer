import { describe, expect, it } from 'vitest'
import { logisticRegression, type LogisticParams } from './logistic-regression'
import type { RegressionResult } from './regression'
import { methodById, methods } from './registry'
import { fromRows } from './table'

const types = { successes: 'count', n: 'count', x: 'continuous' } as const
const mk = (cols: string[], rows: unknown[][]) => fromRows(cols, rows, types)
const params: LogisticParams = { confidence: 0.95 }
const bind = (covariates: string[]) => ({ outcome: 'successes', trials: 'n', covariates })
const rel = (actual: number | null, expected: number) => {
  expect(actual).not.toBeNull()
  return Math.abs((actual as number) - expected) / Math.abs(expected)
}
const term = (r: RegressionResult, t: string, level: string) => r.terms.find((x) => x.term === t && x.level === level)!
const summary = (r: RegressionResult, label: string) => r.summary.find((s) => s.label === label)?.value

describe('logistic-regression', () => {
  it('logistic_2x2_oddsRatio', () => {
    const r = logisticRegression.run(mk(['g', 'successes', 'n'], [['exposed', 20, 100], ['unexposed', 10, 200]]), bind(['g']), params)
    const e = term(r, 'g', 'exposed')
    expect(rel(e.estimate, 4.75)).toBeLessThan(1e-5)
    expect(rel(e.lo, 2.12838802)).toBeLessThan(1e-5)
    expect(rel(e.hi, 10.60074532)).toBeLessThan(1e-5)
    expect(r.ratioName).toBe('odds ratio')
    expect(r.reference).toBe('g=unexposed')
    expect(rel(summary(r, 'baseline probability') as number, 10 / 200)).toBeLessThan(1e-6)
    expect(r.summary.map((s) => s.label)).toEqual([
      'rows used', 'parameters', 'deviance', 'residual df', 'Pearson dispersion', 'AIC', 'baseline probability', 'converged',
    ])
    expect(r.warnings).toEqual([])
  })

  it('logistic_continuous_perUnit', () => {
    const t = mk(['x', 'successes', 'n'], [0, 1, 2, 3, 4].map((x, i) => [x, [5, 8, 12, 15, 22][i], 50]))
    const r = logisticRegression.run(t, bind(['x']), params)
    const x = term(r, 'x', 'per unit')
    expect(rel(x.estimate, 1.6001176)).toBeLessThan(1e-5)
    expect(rel(x.lo, 1.2784029)).toBeLessThan(1e-5)
    expect(rel(x.hi, 2.0027928)).toBeLessThan(1e-5)
  })

  it('logistic_dropsUnusableRows', () => {
    const base = [['a', 20, 100], ['b', 10, 200]]
    const r = logisticRegression.run(
      mk(['g', 'successes', 'n', 'x'], [...base.map((row) => [...row, 1]), ['a', 5, 3, 1], ['b', 1, 0, 1], ['a', 2, 10, NaN]]),
      bind(['g', 'x']),
      params,
    )
    expect(r.warnings).toContain('1 row dropped: missing or non-positive trials')
    expect(r.warnings).toContain('1 row dropped: successes above trials')
    expect(r.warnings).toContain('1 row dropped: missing x')
    expect(summary(r, 'rows used')).toBe(2)
  })

  it('logistic_separation_warns', () => {
    const t = mk(['x', 'successes', 'n'], [0, 1, 2, 3, 4, 5].map((x, i) => [x, i < 3 ? 0 : 20, 20]))
    const r = logisticRegression.run(t, bind(['x']), params)
    expect(r.warnings.filter((w) => w.startsWith('Estimates are unstable'))).toEqual([
      'Estimates are unstable (possible separation: a covariate predicts the outcome almost perfectly). Do not read these odds ratios.',
    ])
    expect(r.warnings).toHaveLength(1)
  })

  it('logistic_categoricalAllOrNone_notEstimable', () => {
    const t = mk(['g', 'successes', 'n'], [['a', 5, 24], ['b', 12, 24], ['c', 0, 24]])
    const r = logisticRegression.run(t, bind(['g']), params)
    expect(term(r, 'g', 'c')).toMatchObject({ estimate: null, note: 'not estimable: all or none had the outcome' })
  })

  it('logistic_defaultBindings_andRegistered', () => {
    const t = fromRows(['vehicles', 'vehicles_with_occupant_fatality'], [[1, 0]], { vehicles: 'count', vehicles_with_occupant_fatality: 'count' })
    expect(logisticRegression.defaultBindings!(t)).toEqual({ outcome: 'vehicles_with_occupant_fatality', trials: 'vehicles' })
    expect(methodById('logistic-regression')).toBe(logisticRegression)
    expect(methods.indexOf(logisticRegression)).toBe(methods.findIndex((m) => m.id === 'poisson-regression') + 1)
  })

  it('logistic_noRows_andInvalidConfidence', () => {
    const empty = logisticRegression.run(mk(['g', 'successes', 'n'], [['a', 1, 0]]), bind(['g']), params)
    expect(empty.warnings).toContain('No rows left to fit.')
    expect(() => logisticRegression.render(empty)).not.toThrow()
    const r = logisticRegression.run(mk(['g', 'successes', 'n'], [['a', 20, 100], ['b', 10, 200]]), bind(['g']), { confidence: 1 })
    expect(r.warnings).toContain('confidence must be between 0 and 1')
  })
})
