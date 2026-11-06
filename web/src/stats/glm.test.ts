import { describe, expect, it } from 'vitest'
import { fitGlm, fitNegativeBinomial } from './glm'
import { matrix } from './linalg'
import type { Mat } from './linalg'

function fromRows(rows: number[][]): Mat {
  const m = matrix(rows.length, rows[0].length)
  rows.forEach((r, i) => r.forEach((v, j) => (m.data[i * m.cols + j] = v)))
  return m
}
const f64 = (xs: number[]) => Float64Array.from(xs)
const fill = (n: number, v: number) => new Float64Array(n).fill(v)
const rel = (actual: number, expected: number) => Math.abs(actual - expected) / Math.abs(expected)

const X2x2 = [[1, 0, 0], [1, 0, 1], [1, 1, 0], [1, 1, 1]]

describe('glm', () => {
  it('poisson_oneFactor_closedForm', () => {
    const X = fromRows([[1, 1], [1, 0]])
    const fit = fitGlm(X, f64([10, 30]), f64([Math.log(1000), Math.log(1500)]), f64([1000, 1500]), { kind: 'poisson' })
    expect(rel(Math.exp(fit.beta[1]), 0.5)).toBeLessThan(1e-6)
    expect(rel(fit.se[1], 0.36514837167011)).toBeLessThan(1e-6)
    expect(rel(Math.exp(fit.beta[0]) * 1000, 20)).toBeLessThan(1e-6)
    expect(Math.abs(fit.deviance)).toBeLessThan(1e-8)
    expect(fit.dfResidual).toBe(0)
    expect(fit.converged).toBe(true)
    expect(fit.aliased).toEqual([false, false])
  })

  it('poisson_independence2x2_closedForm', () => {
    const fit = fitGlm(fromRows(X2x2), f64([10, 20, 30, 40]), fill(4, Math.log(100)), fill(4, 100), { kind: 'poisson' })
    expect(rel(Math.exp(fit.beta[1]), 7 / 3)).toBeLessThan(1e-6)
    expect(rel(fit.se[1], Math.sqrt(1 / 30 + 1 / 70))).toBeLessThan(1e-6)
    expect(rel(Math.exp(fit.beta[2]), 1.5)).toBeLessThan(1e-6)
    expect(rel(fit.se[2], Math.sqrt(1 / 40 + 1 / 60))).toBeLessThan(1e-6)
    ;[12, 18, 28, 42].forEach((m, i) => expect(rel(fit.mu[i], m)).toBeLessThan(1e-6))
    expect(rel(fit.deviance, 0.8043486)).toBeLessThan(1e-6)
    expect(rel(fit.pearson, 0.7936508)).toBeLessThan(1e-6)
    expect(fit.dfResidual).toBe(1)
    expect(fit.converged).toBe(true)
  })

  it('binomial_2x2_closedForm', () => {
    const fit = fitGlm(fromRows([[1, 1], [1, 0]]), f64([20, 10]), fill(2, 0), f64([100, 200]), { kind: 'binomial' })
    expect(rel(Math.exp(fit.beta[1]), 4.75)).toBeLessThan(1e-6)
    expect(rel(fit.se[1], 0.409589)).toBeLessThan(1e-6)
    expect(fit.converged).toBe(true)
  })

  it('binomial_continuous_matchesScipy', () => {
    const X = fromRows([0, 1, 2, 3, 4].map((x) => [1, x]))
    const fit = fitGlm(X, f64([5, 8, 12, 15, 22]), fill(5, 0), fill(5, 50), { kind: 'binomial' })
    expect(rel(Math.exp(fit.beta[1]), 1.6001176)).toBeLessThan(1e-5)
    expect(rel(fit.se[1], 0.1145253)).toBeLessThan(1e-5)
    expect(rel(fit.beta[0], -2.1583223)).toBeLessThan(1e-5)
    expect(rel(fit.deviance, 0.172954)).toBeLessThan(1e-5)
    expect(fit.dfResidual).toBe(3)
    expect(fit.converged).toBe(true)
  })

  it('negativeBinomial_matchesScipy', () => {
    const X = fromRows([...Array(5).fill([1, 1]), ...Array(5).fill([1, 0])])
    const y = f64([2, 9, 0, 14, 5, 20, 3, 41, 11, 30])
    const offset = fill(10, Math.log(10))
    const fit = fitNegativeBinomial(X, y, offset)
    expect(rel(fit.theta, 1.5561152)).toBeLessThan(1e-4)
    expect(fit.thetaCapped).toBe(false)
    expect(rel(Math.exp(fit.beta[1]), 0.2857143)).toBeLessThan(1e-6)
    expect(rel(fit.se[1], 0.5476381)).toBeLessThan(1e-4)
    expect(rel(fit.logLik, -34.3738877)).toBeLessThan(1e-6)
    expect(fit.converged).toBe(true)

    const pois = fitGlm(X, y, offset, fill(10, 1), { kind: 'poisson' })
    expect(rel(pois.se[1], 0.2070197)).toBeLessThan(1e-6)
    expect(rel(pois.pearson / pois.dfResidual, 8.0178572)).toBeLessThan(1e-6)
  })

  it('negativeBinomial_poissonData_capsTheta', () => {
    const X = fromRows(X2x2)
    const y = f64([10, 20, 30, 60])
    const offset = fill(4, Math.log(100))
    const nb = fitNegativeBinomial(X, y, offset)
    const pois = fitGlm(X, y, offset, fill(4, 1), { kind: 'poisson' })
    expect(nb.thetaCapped).toBe(true)
    expect(nb.theta).toBe(1e8)
    expect(nb.converged).toBe(true)
    expect(Math.abs(nb.logLik - pois.logLik)).toBeLessThan(1e-5)
    for (let j = 0; j < 3; j++) expect(Math.abs(nb.beta[j] - pois.beta[j])).toBeLessThan(1e-8)
  })

  it('glm_aliasedColumn_isNaN', () => {
    const y = f64([10, 20, 30, 40])
    const offset = fill(4, Math.log(100))
    const size = fill(4, 1)
    const dup = fitGlm(fromRows(X2x2.map((r) => [...r, r[1]])), y, offset, size, { kind: 'poisson' })
    const ref = fitGlm(fromRows(X2x2), y, offset, size, { kind: 'poisson' })
    expect(dup.aliased).toEqual([false, false, false, true])
    expect(dup.beta[3]).toBeNaN()
    expect(dup.se[3]).toBeNaN()
    for (let j = 0; j < 3; j++) {
      expect(Math.abs(dup.beta[j] - ref.beta[j])).toBeLessThan(1e-12)
      expect(Math.abs(dup.se[j] - ref.se[j])).toBeLessThan(1e-12)
    }
    expect(dup.deviance).toBeCloseTo(ref.deviance, 12)
    expect(dup.dfResidual).toBe(1)
  })

  it('negativeBinomial_poissonMuUnderflow_doesNotThrow', () => {
    // Row 1 alone has an event, so the Poisson fit drives μ elsewhere to 0 (x = 50 underflows).
    const X = fromRows([0, 1, 2, 3, 4, 5, 6, 50].map((x, i) => [1, x, i < 4 ? 0 : 1]))
    const y = f64([1, 0, 0, 0, 0, 0, 0, 0])
    const pois = fitGlm(X, y, fill(8, 0), fill(8, 1), { kind: 'poisson' })
    expect(pois.mu[7]).toBe(0)
    const nb = fitNegativeBinomial(X, y, fill(8, 0))
    expect(nb.theta).toBeGreaterThan(0)
    expect(Number.isFinite(nb.theta)).toBe(true)
    expect(nb.aliased).toEqual([false, false, false])
    expect(Number.isFinite(nb.beta[0])).toBe(true)
    expect(Math.abs(nb.mu[0] - 1)).toBeLessThan(1e-6)
  })

  it('glm_infiniteOffset_keepsStructuralAliasing', () => {
    const X = fromRows([[1, 0], [1, 0], [1, 1], [1, 1]])
    const fit = fitGlm(X, f64([5, 7, 3, 0]), f64([0, 0, 0, -Infinity]), fill(4, 1), { kind: 'poisson' })
    expect(fit.aliased).toEqual([false, false])
    expect(fit.dfResidual).toBe(2)
    // The −∞-offset row has μ = 0 and carries no information, so group 1 is fitted from y = 3 alone.
    expect(fit.converged).toBe(true)
    expect(rel(Math.exp(fit.beta[0]), 6)).toBeLessThan(1e-8)
    expect(rel(Math.exp(fit.beta[1]), 0.5)).toBeLessThan(1e-8)
    expect(fit.mu[3]).toBe(0)
  })

  it('glm_firstIterationNonFinite_isFailureNotAliasing', () => {
    // y > 0 on a row with offset −∞: no β gives a finite deviance.
    const X = fromRows([[1, 0], [1, 0], [1, 1], [1, 1]])
    const fit = fitGlm(X, f64([5, 7, 3, 2]), f64([0, 0, 0, -Infinity]), fill(4, 1), { kind: 'poisson' })
    expect(fit.converged).toBe(false)
    expect(fit.aliased).toEqual([false, false])
    expect(fit.dfResidual).toBe(2)
    expect(fit.deviance).toBeNaN()
    expect(fit.se[0]).toBeNaN()
  })

  it('negativeBinomial_slowThetaAlternation_converges', () => {
    // Needs 38 θ/β rounds (more than the old limit of 25); scipy profile ML logLik −19.140021766.
    const X = fromRows([0.4843, 0.6954, 0.4955, 1.1193, 0.9234].map((x) => [1, x]))
    const y = f64([135, 36, 75, 38, 121])
    const offset = f64([2.1001, 0, 1.3081, 0, 1.3975])
    const nb = fitNegativeBinomial(X, y, offset)
    expect(nb.converged).toBe(true)
    expect(nb.thetaCapped).toBe(false)
    expect(rel(nb.logLik, -19.140021766)).toBeLessThan(1e-9)
    // θ is the profile maximum: moving it 1% either way lowers the log-likelihood.
    for (const k of [0.99, 1.01]) {
      const at = fitGlm(X, y, offset, fill(5, 1), { kind: 'negative_binomial', theta: nb.theta * k })
      expect(at.logLik).toBeLessThan(nb.logLik)
    }
  })

  it('negativeBinomial_largeTheta_scoreDoesNotCancel', () => {
    // θ ≈ 5182: the digamma-difference score was noisy at 1e-5 and never met the 1e-8 θ tolerance.
    const xs = [5.1, 4.4, 3.3, 7, 0.1, 0, 4.2, 4.6, 8.7, 6.2, 6.7, 5.9, 10, 9.4, 4, 1.8, 5.1, 2.7, 5.3, 4, 3.8, 7.6, 6.1, 7.3, 8.9, 5.2, 2.1, 3.9, 3.2, 2.8, 5.7]
    const g = [1, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 0, 0, 1, 0, 1, 1, 1, 0, 0, 1, 1, 1, 0]
    const y = f64([15, 3, 1, 9, 6, 4, 6, 4, 5, 5, 4, 8, 8, 11, 11, 5, 11, 7, 2, 4, 8, 10, 9, 4, 12, 4, 5, 7, 3, 6, 3])
    const nb = fitNegativeBinomial(fromRows(xs.map((x, i) => [1, x, g[i]])), y, fill(31, 0))
    expect(nb.converged).toBe(true)
    expect(nb.thetaCapped).toBe(false)
    expect(rel(nb.logLik, -71.133377805)).toBeLessThan(1e-9)
  })

  it('glm_stepHalving_rescuesOvershoot', () => {
    // NB θ = 0.5: the full IRLS step raises the deviance at iterations ≥ 2 (3 halvings).
    // Without halving this fit does not converge in 50 iterations.
    const X = fromRows([4, 6, 1, 3, 4, 0].map((x) => [1, x]))
    const fit = fitGlm(X, f64([0, 28, 0, 0, 0, 17]), fill(6, 0), fill(6, 1), { kind: 'negative_binomial', theta: 0.5 })
    expect(fit.converged).toBe(true)
    // scipy BFGS: β = [1.72415507, 0.08626198], logLik −15.3715407403.
    expect(rel(fit.logLik, -15.3715407403)).toBeLessThan(1e-9)
    expect(rel(fit.beta[0], 1.72415507)).toBeLessThan(1e-3)
    expect(rel(fit.beta[1], 0.08626198)).toBeLessThan(1e-3)
  })
})
