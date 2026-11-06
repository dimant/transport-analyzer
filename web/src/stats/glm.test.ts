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
})
