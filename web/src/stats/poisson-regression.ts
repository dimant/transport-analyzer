import { buildDesign } from './design'
import { fitGlm, fitNegativeBinomial, type GlmFit } from './glm'
import type { Bindings, Method, RenderSpec } from './method'
import { formatPer } from './rate'
import { CONFIDENCE_WARNING, DISPERSION_LIMIT, referenceOf, renderRegression, termsFromFit, validConfidence, type RegressionResult } from './regression'
import { categoryLabel, column, naturalCompare, type Table } from './table'

export type PoissonParams = { family: 'poisson' | 'negative_binomial'; confidence: number; per: number }

const asList = (v: string | string[] | undefined): string[] => (v === undefined || v === '' ? [] : Array.isArray(v) ? v : [v])
// a saturated fit leaves rounding noise in the deviance
const DEVIANCE_ZERO = 1e-9

/** Distinct "exposure_source (provenance)" over the rows, or null when the table lacks either column. */
function exposureSources(table: Table, rows: number[]): string | null {
  const has = (n: string) => table.columns.some((c) => c.name === n && c.type === 'categorical')
  if (!has('exposure_source') || !has('provenance')) return null
  const src = column(table, 'exposure_source').values as (string | null)[]
  const prov = column(table, 'provenance').values as (string | null)[]
  const seen = new Set(rows.map((i) => `${categoryLabel(src[i])} (${categoryLabel(prov[i])})`))
  return [...seen].sort(naturalCompare).join(' + ')
}

export const poissonRegression: Method<PoissonParams, RegressionResult> = {
  id: 'poisson-regression',
  name: 'Poisson regression (rate ratios)',
  description:
    'Rate ratios from a log-linear model with an exposure offset, so each covariate is compared with the others held fixed. Negative binomial allows for overdispersion.',
  inputs: [
    { key: 'events', label: 'Events (count)', types: ['count'] },
    { key: 'exposure', label: 'Exposure', types: ['exposure'] },
    { key: 'covariates', label: 'Covariates', types: ['categorical'], multiple: true },
  ],
  params: [
    { key: 'family', label: 'Family', kind: 'select', options: ['poisson', 'negative_binomial'], default: 'poisson' },
    { key: 'confidence', label: 'Confidence', kind: 'number', default: 0.95 },
    { key: 'per', label: 'Baseline rate per', kind: 'number', default: 100000000 },
  ],
  defaultBindings(table: Table): Bindings {
    const has = (name: string, type: string) => table.columns.some((c) => c.name === name && c.type === type)
    const out: Bindings = {}
    if (has('numerator', 'count')) out.events = 'numerator'
    if (has('denominator', 'exposure')) out.exposure = 'denominator'
    const covariates = ['rate_class', 'year'].filter((n) => has(n, 'categorical'))
    if (covariates.length > 0) out.covariates = covariates
    return out
  },
  run(table: Table, b: Bindings, p: PoissonParams): RegressionResult {
    const eventsName = b.events as string
    const exposureName = b.exposure as string
    const design = buildDesign(table, { response: eventsName, size: exposureName, sizeKind: 'exposure', covariates: asList(b.covariates) })
    const warnings = [...design.warnings]
    if (!validConfidence(p.confidence)) warnings.push(CONFIDENCE_WARNING)
    const result: RegressionResult = {
      ratioName: 'rate ratio',
      eventsLabel: table.meta.measure ?? eventsName,
      sizeLabel: table.meta.metric ?? exposureName,
      confidence: p.confidence,
      terms: [],
      reference: '',
      summary: [],
      warnings,
    }
    if (design.rows.length === 0) {
      warnings.push('No rows left to fit.')
      result.summary = [{ label: 'family', value: p.family }, { label: 'rows used', value: 0, format: 'integer' }]
      return result
    }

    let fit: GlmFit
    let familyText = 'poisson'
    let extraParameters = 0
    let thetaCapped = false
    if (p.family === 'negative_binomial') {
      const nb = fitNegativeBinomial(design.X, design.y, design.offset)
      fit = nb
      familyText = `negative binomial (θ = ${Number(nb.theta.toPrecision(4))})`
      extraParameters = 1
      thetaCapped = nb.thetaCapped
    } else {
      fit = fitGlm(design.X, design.y, design.offset, design.size, { kind: 'poisson' })
    }

    const parameters = fit.aliased.filter((a) => !a).length
    const df = fit.dfResidual
    const dispersion = df > 0 ? fit.pearson / df : null
    // the intercept is the log rate at every reference level
    const baseline = Math.exp(fit.beta[0]) * p.per
    result.terms = termsFromFit(design, fit, p.confidence)
    result.reference = referenceOf(result.terms)
    result.summary = [
      { label: 'family', value: familyText },
      { label: 'rows used', value: design.rows.length, format: 'integer' },
      { label: 'parameters', value: parameters, format: 'integer' },
      { label: 'deviance', value: Math.abs(fit.deviance) < DEVIANCE_ZERO ? 0 : fit.deviance },
      { label: 'residual df', value: df, format: 'integer' },
      { label: 'Pearson dispersion', value: dispersion },
      { label: 'AIC', value: -2 * fit.logLik + 2 * (parameters + extraParameters) },
      { label: `baseline rate per ${formatPer(p.per)}`, value: Number.isFinite(baseline) ? baseline : null },
      { label: 'converged', value: fit.converged ? 'yes' : 'no' },
    ]
    const sources = exposureSources(table, design.rows)
    if (sources !== null) result.summary.push({ label: 'exposure sources', value: sources })

    if (p.family === 'poisson' && dispersion !== null && dispersion > DISPERSION_LIMIT) {
      warnings.push(
        `Pearson dispersion ${dispersion.toFixed(2)} exceeds ${DISPERSION_LIMIT}: counts vary more than Poisson allows, so these intervals are too narrow. Try family negative_binomial.`,
      )
    }
    if (df === 0) warnings.push('No residual degrees of freedom: the model is saturated, so overdispersion cannot be checked.')
    if (!fit.converged) warnings.push('The fit did not converge in 50 iterations; estimates are unreliable.')
    if (thetaCapped) warnings.push('No overdispersion found: negative binomial reduces to Poisson.')
    return result
  },
  render(r: RegressionResult): RenderSpec[] {
    return renderRegression(r)
  },
}
