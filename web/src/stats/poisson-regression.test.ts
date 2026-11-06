import { describe, expect, it, vi } from 'vitest'
import { fitGlm } from './glm'
import type { ForestSpec, TableSpec } from './method'
import { poissonRegression, type PoissonParams } from './poisson-regression'
import type { RegressionResult } from './regression'
import { methodById, methods } from './registry'
import { fromRows } from './table'

// fitGlm passes through to the real fit unless a test overrides one call
vi.mock('./glm', async (importOriginal) => {
  const m = await importOriginal<typeof import('./glm')>()
  return { ...m, fitGlm: vi.fn(m.fitGlm) }
})
const realGlm = () => vi.importActual<typeof import('./glm')>('./glm')

const types = { numerator: 'count', denominator: 'exposure' } as const
const mk = (cols: string[], rows: unknown[][]) => fromRows(cols, rows, types)
const params: PoissonParams = { family: 'poisson', confidence: 0.95, per: 1000 }
const bind = (covariates: string[]) => ({ events: 'numerator', exposure: 'denominator', covariates })
const rel = (actual: number | null, expected: number) => {
  expect(actual).not.toBeNull()
  return Math.abs((actual as number) - expected) / Math.abs(expected)
}
const term = (r: RegressionResult, t: string, level: string) => r.terms.find((x) => x.term === t && x.level === level)!
const summary = (r: RegressionResult, label: string) => r.summary.find((s) => s.label === label)?.value

const oneFactor = [['A', 10, 1000], ['B', 30, 1500]]
const nbData = mk(
  ['g', 'numerator', 'denominator'],
  [
    ...[20, 3, 41, 11, 30].map((y) => ['A', y, 10]),
    ...[2, 9, 0, 14, 5].map((y) => ['B', y, 10]),
  ],
)

function expectOneFactorA(r: RegressionResult) {
  const a = term(r, 'g', 'A')
  expect(rel(a.estimate, 0.5)).toBeLessThan(1e-6)
  expect(rel(a.lo, 0.24443036)).toBeLessThan(1e-6)
  expect(rel(a.hi, 1.02278622)).toBeLessThan(1e-6)
  expect(rel(a.p, 0.05766162)).toBeLessThan(1e-6)
  expect(a.note).toBe('')
}

describe('poisson-regression', () => {
  it('poisson_oneFactor_rateRatio', () => {
    const r = poissonRegression.run(mk(['g', 'numerator', 'denominator'], oneFactor), bind(['g']), params)
    expectOneFactorA(r)
    const b = term(r, 'g', 'B')
    expect(b).toMatchObject({ estimate: 1, lo: null, hi: null, p: null, note: 'reference', events: 30, size: 1500 })
    expect(r.reference).toBe('g=B')
    expect(rel(summary(r, 'baseline rate per 1k') as number, 20)).toBeLessThan(1e-6)
    expect(r.summary.map((s) => s.label)).toEqual([
      'family', 'rows used', 'parameters', 'deviance', 'residual df', 'Pearson dispersion', 'AIC', 'baseline rate per 1k', 'converged',
    ])
    expect(summary(r, 'family')).toBe('poisson')
    expect(summary(r, 'Pearson dispersion')).toBeNull()
    expect(summary(r, 'converged')).toBe('yes')
    expect(r.warnings).toEqual(['No residual degrees of freedom: the model is saturated, so overdispersion cannot be checked.'])

    const [terms, forest, sum] = poissonRegression.render(r) as [TableSpec, ForestSpec, TableSpec]
    expect(terms.columns.map((c) => [c.key, c.format])).toEqual([
      ['term', 'text'], ['level', 'text'], ['estimate', 'rate'], ['lo', 'rate'], ['hi', 'rate'], ['p', 'rate'],
      ['events', 'integer'], ['size', 'number'], ['note', 'text'],
    ])
    expect(terms.columns[2].label).toBe('rate ratio')
    expect(terms.rows).toHaveLength(2)
    expect(forest.xLabel).toBe('rate ratio (95% CI)')
    expect(forest.caption).toBe('relative to g=B')
    expect(forest.data).toHaveLength(1)
    expect(forest.data[0][forest.label]).toBe('g: A')
    expect(sum.columns.map((c) => c.key)).toEqual(['label', 'value'])
  })

  it('poisson_zeroEventLevel_notEstimable', () => {
    const r = poissonRegression.run(mk(['g', 'numerator', 'denominator'], [...oneFactor, ['C', 0, 500]]), bind(['g']), params)
    expectOneFactorA(r)
    expect(term(r, 'g', 'C')).toMatchObject({ estimate: null, lo: null, hi: null, p: null, note: 'not estimable: no events' })
    expect(r.warnings).toContain('g=C: no events; not estimable')
  })

  it('poisson_aliasedCovariate_reported', () => {
    const r = poissonRegression.run(
      mk(['g', 'h', 'numerator', 'denominator'], oneFactor.map(([g, ...rest]) => [g, g, ...rest])),
      bind(['g', 'h']),
      params,
    )
    expectOneFactorA(r)
    expect(term(r, 'h', 'A')).toMatchObject({ estimate: null, lo: null, hi: null, p: null, note: 'aliased with other covariates' })
    expect(term(r, 'h', 'B')).toMatchObject({ estimate: null, note: 'reference; term aliased with other covariates' })
    expect(r.reference).toBe('g=B')
    expect(summary(r, 'parameters')).toBe(2)
    const forest = poissonRegression.render(r)[1] as ForestSpec
    expect(forest.caption).toBe('relative to g=B')
    expect(forest.data.map((d) => d.label)).toEqual(['g: A', 'h: A'])
  })

  it('poisson_overdispersion_warns', () => {
    const pois = poissonRegression.run(nbData, bind(['g']), { ...params, family: 'poisson' })
    const w = pois.warnings.find((x) => x.startsWith('Pearson dispersion'))
    expect(w).toBe(
      'Pearson dispersion 8.02 exceeds 1.5: counts vary more than Poisson allows, so these intervals are too narrow. Try family negative_binomial.',
    )

    const nb = poissonRegression.run(nbData, bind(['g']), { ...params, family: 'negative_binomial' })
    expect(nb.warnings.some((x) => x.startsWith('Pearson dispersion'))).toBe(false)
    const b = term(nb, 'g', 'B')
    expect(rel(b.estimate, 0.2857143)).toBeLessThan(1e-4)
    expect(rel(b.lo, 0.0976746)).toBeLessThan(1e-4)
    expect(rel(b.hi, 0.8357616)).toBeLessThan(1e-4)
    expect(summary(nb, 'family')).toBe('negative binomial (θ = 1.556)')
    // AIC = −2·logLik + 2·(2 parameters + θ)
    expect(rel(summary(nb, 'AIC') as number, 2 * 34.3738877 + 6)).toBeLessThan(1e-6)
  })

  it('poisson_realScale_matchesSmallScale', () => {
    const r = poissonRegression.run(
      mk(['g', 'numerator', 'denominator'], [['A', 10000, 1e12], ['B', 30000, 1.5e12]]),
      bind(['g']),
      { ...params, per: 1e8 },
    )
    const a = term(r, 'g', 'A')
    expect(rel(a.estimate, 0.5)).toBeLessThan(1e-6)
    const z = 1.959963984540054
    const se = (Math.log(a.hi!) - Math.log(a.lo!)) / (2 * z)
    expect(rel(se, Math.sqrt(1 / 10000 + 1 / 30000))).toBeLessThan(1e-6)
  })

  it('poisson_dropsUnusableRows', () => {
    const r = poissonRegression.run(
      mk(['g', 'numerator', 'denominator'], [...oneFactor, ['A', 3, null], ['B', 4, 0]]),
      bind(['g']),
      params,
    )
    expect(r.warnings).toContain('2 rows dropped: missing or non-positive exposure')
    expectOneFactorA(r)

    const empty = poissonRegression.run(
      mk(['g', 'numerator', 'denominator'], [['A', 3, null], ['B', 4, 0]]),
      bind(['g']),
      params,
    )
    expect(empty.warnings).toContain('No rows left to fit.')
    expect(empty.terms).toEqual([])
    expect(() => poissonRegression.render(empty)).not.toThrow()
  })

  it('poisson_summaryNamesExposureSources', () => {
    const r = poissonRegression.run(
      mk(
        ['g', 'numerator', 'denominator', 'exposure_source', 'provenance'],
        [
          ['A', 10, 1000, 'bts-passenger-miles', 'survey_estimate'],
          ['B', 30, 1500, 'bts-passenger-miles', 'measured'],
          ['B', 0, -1, 'other', 'measured'],
        ],
      ),
      bind(['g']),
      params,
    )
    expect(summary(r, 'exposure sources')).toBe('bts-passenger-miles (measured) + bts-passenger-miles (survey_estimate)')
    expect(r.summary.at(-1)?.label).toBe('exposure sources')
  })

  it('poisson_defaultBindings_usesRateClassAndYear', () => {
    const t = fromRows(
      ['rate_class', 'year', 'mode', 'numerator', 'denominator'],
      [['part_121', 2019, 'aviation', 1, 10]],
      { year: 'categorical', numerator: 'count', denominator: 'exposure' },
    )
    expect(poissonRegression.defaultBindings!(t)).toEqual({ events: 'numerator', exposure: 'denominator', covariates: ['rate_class', 'year'] })
    const noYear = fromRows(['rate_class', 'year', 'numerator'], [['x', 2019, 1]], { year: 'continuous', numerator: 'count' })
    expect(poissonRegression.defaultBindings!(noYear)).toEqual({ events: 'numerator', covariates: ['rate_class'] })
  })

  it('poisson_oneLevelLeft_referenceNotInModel', () => {
    const r = poissonRegression.run(mk(['g', 'numerator', 'denominator'], [['A', 0, 1000], ['B', 30, 1500]]), bind(['g']), params)
    expect(term(r, 'g', 'B')).toMatchObject({ estimate: null, note: 'reference; term not in the model' })
    expect(r.reference).toBe('')
  })

  it('poisson_summaryFormats_perRow', () => {
    const rows = Array.from({ length: 1234 }, (_, i) => [i % 2 ? 'A' : 'B', (i % 7) + 1, 100])
    const r = poissonRegression.run(mk(['g', 'numerator', 'denominator'], rows), bind(['g']), params)
    const cells = Object.fromEntries((poissonRegression.render(r)[2] as TableSpec).rows.map((row) => [row.label, row.value]))
    expect(cells['rows used']).toBe('1,234')
    expect(cells['parameters']).toBe('2')
    expect(cells['residual df']).toBe('1,232')
    expect(cells['converged']).toBe('yes')

    const saturated = poissonRegression.run(mk(['g', 'numerator', 'denominator'], oneFactor), bind(['g']), params)
    expect(summary(saturated, 'deviance')).toBe(0)
    const sat = Object.fromEntries((poissonRegression.render(saturated)[2] as TableSpec).rows.map((row) => [row.label, row.value]))
    expect(sat['deviance']).toBe('0')
    expect(sat['Pearson dispersion']).toBe('—')
    expect(sat['AIC']).toBe('13.4')
  })

  it('poisson_confidence90_narrowsInterval', () => {
    const r = poissonRegression.run(mk(['g', 'numerator', 'denominator'], oneFactor), bind(['g']), { ...params, confidence: 0.9 })
    const a = term(r, 'g', 'A')
    const se = Math.sqrt(1 / 10 + 1 / 30)
    expect(rel(a.lo, 0.5 * Math.exp(-1.6448536 * se))).toBeLessThan(1e-6)
    expect(rel(a.hi, 0.5 * Math.exp(1.6448536 * se))).toBeLessThan(1e-6)
    expect(a.lo!).toBeGreaterThan(0.24443036)
    expect((poissonRegression.render(r)[1] as ForestSpec).xLabel).toBe('rate ratio (90% CI)')
  })

  it('poisson_invalidConfidence_warnsAndNullsIntervals', () => {
    for (const confidence of [0, 1, 95]) {
      const r = poissonRegression.run(mk(['g', 'numerator', 'denominator'], oneFactor), bind(['g']), { ...params, confidence })
      expect(r.warnings).toContain('confidence must be between 0 and 1')
      const a = term(r, 'g', 'A')
      expect(rel(a.estimate, 0.5)).toBeLessThan(1e-6)
      expect(a.lo).toBeNull()
      expect(a.hi).toBeNull()
    }
  })

  it('poisson_notConverged_warns', async () => {
    const real = (await realGlm()).fitGlm
    vi.mocked(fitGlm).mockImplementationOnce((...args) => ({ ...real(...args), converged: false }))
    const r = poissonRegression.run(mk(['g', 'numerator', 'denominator'], oneFactor), bind(['g']), params)
    expect(r.warnings).toContain('The fit did not converge in 50 iterations; estimates are unreliable.')
    expect(summary(r, 'converged')).toBe('no')
  })

  it('poisson_nonFiniteEstimate_notAliased', async () => {
    const real = (await realGlm()).fitGlm
    vi.mocked(fitGlm).mockImplementationOnce((...args) => {
      const fit = real(...args)
      fit.beta[1] = NaN
      fit.se[1] = NaN
      return { ...fit, converged: false }
    })
    const r = poissonRegression.run(mk(['g', 'numerator', 'denominator'], oneFactor), bind(['g']), params)
    expect(term(r, 'g', 'A')).toMatchObject({ estimate: null, lo: null, hi: null, p: null, note: 'not estimable: fit did not converge' })
    expect(term(r, 'g', 'B').note).toBe('reference')
  })

  it('poisson_thetaCapped_warns', () => {
    const t = mk(
      ['g', 'h', 'numerator', 'denominator'],
      [['a', 'x', 10, 100], ['a', 'y', 20, 100], ['b', 'x', 30, 100], ['b', 'y', 60, 100]],
    )
    const r = poissonRegression.run(t, bind(['g', 'h']), { ...params, family: 'negative_binomial' })
    expect(r.warnings).toContain('No overdispersion found: negative binomial reduces to Poisson.')
    expect(summary(r, 'family')).toBe('negative binomial (θ = 100000000)')
  })

  it('poisson_registered_afterHeatmap', () => {
    expect(methodById('poisson-regression')).toBe(poissonRegression)
    expect(methods.indexOf(poissonRegression)).toBe(methods.findIndex((m) => m.id === 'heatmap') + 1)
    expect(poissonRegression.params.find((p) => p.key === 'family')).toMatchObject({
      kind: 'select', options: ['poisson', 'negative_binomial'], default: 'poisson',
    })
  })
})
