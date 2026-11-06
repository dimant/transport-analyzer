import { buildDesign } from './design'
import { fitGlm } from './glm'
import type { Bindings, Method, RenderSpec } from './method'
import { CONFIDENCE_WARNING, DISPERSION_LIMIT, referenceOf, renderRegression, termsFromFit, validConfidence, type RegressionResult } from './regression'
import type { Table } from './table'

export type LogisticParams = { confidence: number }

const asList = (v: string | string[] | undefined): string[] => (v === undefined || v === '' ? [] : Array.isArray(v) ? v : [v])
const DEVIANCE_ZERO = 1e-9
// |β| beyond this is an odds ratio of about 3 million: the likelihood is flat, not the data informative
const SEPARATION_BETA = 15
const SEPARATION_WARNING =
  'Estimates are unstable (possible separation: a covariate predicts the outcome almost perfectly). Do not read these odds ratios.'

export const logisticRegression: Method<LogisticParams, RegressionResult> = {
  id: 'logistic-regression',
  name: 'Logistic regression (odds ratios)',
  description:
    'Odds ratios from a logistic model for case-only attributes, where the outcome is per vehicle. Each covariate is compared with the others held fixed.',
  inputs: [
    { key: 'outcome', label: 'With the outcome', types: ['count'] },
    { key: 'trials', label: 'Out of', types: ['count'] },
    { key: 'covariates', label: 'Covariates', types: ['categorical', 'continuous'], multiple: true },
  ],
  params: [{ key: 'confidence', label: 'Confidence', kind: 'number', default: 0.95 }],
  defaultBindings(table: Table): Bindings {
    const has = (name: string) => table.columns.some((c) => c.name === name && c.type === 'count')
    const out: Bindings = {}
    if (has('vehicles_with_occupant_fatality')) out.outcome = 'vehicles_with_occupant_fatality'
    if (has('vehicles')) out.trials = 'vehicles'
    return out
  },
  run(table: Table, b: Bindings, p: LogisticParams): RegressionResult {
    const outcomeName = b.outcome as string
    const trialsName = b.trials as string
    const design = buildDesign(table, { response: outcomeName, size: trialsName, sizeKind: 'trials', covariates: asList(b.covariates) })
    const warnings = [...design.warnings]
    if (!validConfidence(p.confidence)) warnings.push(CONFIDENCE_WARNING)
    const result: RegressionResult = {
      ratioName: 'odds ratio',
      eventsLabel: table.meta.measure ?? outcomeName,
      sizeLabel: table.meta.metric ?? trialsName,
      confidence: p.confidence,
      terms: [],
      reference: '',
      summary: [],
      warnings,
    }
    if (design.rows.length === 0) {
      warnings.push('No rows left to fit.')
      result.summary = [{ label: 'rows used', value: 0, format: 'integer' }]
      return result
    }

    const fit = fitGlm(design.X, design.y, design.offset, design.size, { kind: 'binomial' })
    const parameters = fit.aliased.filter((a) => !a).length
    const df = fit.dfResidual
    const dispersion = df > 0 ? fit.pearson / df : null
    // the intercept is the log odds at every reference level and continuous covariate at 0
    const baseline = 1 / (1 + Math.exp(-fit.beta[0]))
    result.terms = termsFromFit(design, fit, p.confidence)
    result.reference = referenceOf(result.terms)
    result.summary = [
      { label: 'rows used', value: design.rows.length, format: 'integer' },
      { label: 'parameters', value: parameters, format: 'integer' },
      { label: 'deviance', value: Math.abs(fit.deviance) < DEVIANCE_ZERO ? 0 : fit.deviance },
      { label: 'residual df', value: df, format: 'integer' },
      { label: 'Pearson dispersion', value: dispersion },
      { label: 'AIC', value: -2 * fit.logLik + 2 * parameters },
      { label: 'baseline probability', value: Number.isFinite(baseline) ? baseline : null },
      { label: 'converged', value: fit.converged ? 'yes' : 'no' },
    ]

    // the intercept is exempt: an uncentered covariate such as year pushes it far out on a sound fit
    const unstable = fit.beta.some((beta, j) => j > 0 && !fit.aliased[j] && Math.abs(beta) > SEPARATION_BETA)
    if (!fit.converged || unstable) warnings.push(SEPARATION_WARNING)
    if (dispersion !== null && dispersion > DISPERSION_LIMIT) {
      warnings.push(
        `Pearson dispersion ${dispersion.toFixed(2)} exceeds ${DISPERSION_LIMIT}: outcomes vary more between groups than the binomial allows, so these intervals are too narrow.`,
      )
    }
    return result
  },
  render(r: RegressionResult): RenderSpec[] {
    return renderRegression(r)
  },
}
