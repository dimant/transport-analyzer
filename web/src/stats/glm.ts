import { digamma, logGamma, trigamma } from './distributions'
import { cholesky, choleskyInverse, choleskySolve, matrix, weightedCrossprod, weightedXty } from './linalg'
import type { Mat } from './linalg'

export type Family = { kind: 'poisson' } | { kind: 'binomial' } | { kind: 'negative_binomial'; theta: number }

export interface GlmFit {
  beta: Float64Array
  se: Float64Array
  aliased: boolean[] // beta/se NaN where aliased
  mu: Float64Array
  deviance: number
  pearson: number
  logLik: number
  dfResidual: number
  iterations: number
  converged: boolean
}

const MAX_ITER = 50
const EPSILON = 1e-10
const MAX_HALVINGS = 10
const LOGIT_ETA_MAX = 30
const LOG_ETA_MAX = 700
const THETA_CAP = 1e8
const MAX_THETA_ROUNDS = 25
const MAX_THETA_NEWTON = 50
const THETA_TOL = 1e-8

/** y·log(y/m), taken as 0 when y = 0. */
function ylogy(y: number, m: number): number {
  return y === 0 ? 0 : y * Math.log(y / m)
}

/** μ from an already clamped η. */
function linkInverse(family: Family, eta: number, size: number): number {
  return family.kind === 'binomial' ? size / (1 + Math.exp(-eta)) : Math.exp(eta)
}

function variance(family: Family, mu: number, size: number): number {
  if (family.kind === 'poisson') return mu
  if (family.kind === 'negative_binomial') return mu + (mu * mu) / family.theta
  return mu * (1 - mu / size)
}

function deviance(family: Family, y: Float64Array, mu: Float64Array, size: Float64Array): number {
  let d = 0
  for (let i = 0; i < y.length; i++) {
    const yi = y[i]
    const m = mu[i]
    if (family.kind === 'poisson') d += ylogy(yi, m) - (yi - m)
    else if (family.kind === 'binomial') d += ylogy(yi, m) + ylogy(size[i] - yi, size[i] - m)
    else {
      const t = family.theta
      // log1p keeps the second term exact when θ is huge.
      d += ylogy(yi, m) - (yi + t) * Math.log1p((yi - m) / (m + t))
    }
  }
  return 2 * d
}

/** ln Γ(y+θ) − ln Γ(θ), by the Stirling difference when θ is large enough to cancel. */
function logGammaRatio(y: number, theta: number): number {
  if (theta < 1e4) return logGamma(y + theta) - logGamma(theta)
  const x = y + theta
  return (theta - 0.5) * Math.log1p(y / theta) + y * Math.log(x) - y + 1 / (12 * x) - 1 / (12 * theta)
}

function logLikelihood(family: Family, y: Float64Array, mu: Float64Array, size: Float64Array): number {
  let ll = 0
  for (let i = 0; i < y.length; i++) {
    const yi = y[i]
    const m = mu[i]
    if (family.kind === 'poisson') {
      ll += (yi === 0 ? 0 : yi * Math.log(m)) - m - logGamma(yi + 1)
    } else if (family.kind === 'binomial') {
      const n = size[i]
      const p = m / n
      ll += logGamma(n + 1) - logGamma(yi + 1) - logGamma(n - yi + 1)
      ll += (yi === 0 ? 0 : yi * Math.log(p)) + (n === yi ? 0 : (n - yi) * Math.log(1 - p))
    } else {
      const t = family.theta
      ll += logGammaRatio(yi, t) - logGamma(yi + 1)
      ll -= t * Math.log1p(m / t)
      if (yi !== 0) ll += yi * Math.log(m / (t + m))
    }
  }
  return ll
}

/** Columns of X listed in keep, as a new n×keep.length matrix. */
function selectColumns(X: Mat, keep: number[]): Mat {
  const out = matrix(X.rows, keep.length)
  for (let r = 0; r < X.rows; r++) {
    for (let k = 0; k < keep.length; k++) out.data[r * keep.length + k] = X.data[r * X.cols + keep[k]]
  }
  return out
}

/** Linear predictor Xβ + offset, clamped for the family's link. */
function linearPredictor(family: Family, X: Mat, beta: Float64Array, offset: Float64Array): Float64Array {
  const { rows: n, cols: p, data } = X
  const eta = new Float64Array(n)
  for (let r = 0; r < n; r++) {
    let s = offset[r]
    for (let j = 0; j < p; j++) s += data[r * p + j] * beta[j]
    eta[r] =
      family.kind === 'binomial'
        ? Math.min(LOGIT_ETA_MAX, Math.max(-LOGIT_ETA_MAX, s))
        : Math.min(LOG_ETA_MAX, s)
  }
  return eta
}

/** IRLS weights and working responses (excluding the offset) at μ. */
function workingValues(family: Family, y: Float64Array, mu: Float64Array, eta: Float64Array, offset: Float64Array, size: Float64Array) {
  const n = y.length
  const w = new Float64Array(n)
  const z = new Float64Array(n)
  for (let i = 0; i < n; i++) {
    // dμ/dη is μ for the log link and size·p(1−p) = Var for the logit link.
    const v = variance(family, mu[i], size[i])
    const dmu = family.kind === 'binomial' ? v : mu[i]
    w[i] = v > 0 ? (dmu * dmu) / v : 0
    z[i] = eta[i] - offset[i] + (dmu > 0 ? (y[i] - mu[i]) / dmu : 0)
  }
  return { w, z }
}

/**
 * GLM by IRLS with step-halving. Aliasing is decided once from the
 * unweighted XᵀX; a column the final weighted XᵀX also flags is reported
 * as aliased too.
 */
export function fitGlm(X: Mat, y: Float64Array, offset: Float64Array, size: Float64Array, family: Family): GlmFit {
  const n = X.rows
  const p = X.cols
  const structural = cholesky(weightedCrossprod(X, new Float64Array(n).fill(1))).aliased
  const keep: number[] = []
  for (let j = 0; j < p; j++) if (!structural[j]) keep.push(j)
  const Xk = selectColumns(X, keep)

  let mu: Float64Array = new Float64Array(n)
  let eta: Float64Array = new Float64Array(n)
  for (let i = 0; i < n; i++) {
    if (family.kind === 'binomial') {
      const pr = (y[i] + 0.5) / (size[i] + 1)
      mu[i] = size[i] * pr
      eta[i] = Math.log(pr / (1 - pr))
    } else {
      mu[i] = y[i] + 0.1
      eta[i] = Math.log(mu[i])
    }
  }

  let devOld = deviance(family, y, mu, size)
  const relChange = (dev: number) => (dev - devOld) / (Math.abs(dev) + 0.1)
  let beta: Float64Array | null = null
  let converged = false
  let iterations = 0
  for (let iter = 1; iter <= MAX_ITER; iter++) {
    iterations = iter
    const { w, z } = workingValues(family, y, mu, eta, offset, size)
    let next = choleskySolve(cholesky(weightedCrossprod(Xk, w)), weightedXty(Xk, w, z))
    let etaNext = linearPredictor(family, Xk, next, offset)
    let muNext = etaNext.map((e, i) => linkInverse(family, e, size[i]))
    let dev = deviance(family, y, muNext, size)
    // Step-halving towards the previous β while the deviance rises or is non-finite.
    for (let h = 0; beta && h < MAX_HALVINGS && !(relChange(dev) < EPSILON); h++) {
      const prev = beta
      next = next.map((b, j) => (b + prev[j]) / 2)
      etaNext = linearPredictor(family, Xk, next, offset)
      muNext = etaNext.map((e, i) => linkInverse(family, e, size[i]))
      dev = deviance(family, y, muNext, size)
    }
    beta = next
    eta = etaNext
    mu = muNext
    converged = Math.abs(relChange(dev)) < EPSILON
    devOld = dev
    if (converged) break
  }

  const { w } = workingValues(family, y, mu, eta, offset, size)
  const chol = cholesky(weightedCrossprod(Xk, w))
  const inv = choleskyInverse(chol)
  const k = keep.length
  const fullBeta = new Float64Array(p).fill(NaN)
  const se = new Float64Array(p).fill(NaN)
  const aliased = structural.slice()
  keep.forEach((j, idx) => {
    if (chol.aliased[idx]) {
      aliased[j] = true
      return
    }
    fullBeta[j] = beta ? beta[idx] : NaN
    se[j] = Math.sqrt(inv.data[idx * k + idx])
  })

  let pearson = 0
  for (let i = 0; i < n; i++) {
    const v = variance(family, mu[i], size[i])
    if (v > 0) pearson += (y[i] - mu[i]) ** 2 / v
  }

  return {
    beta: fullBeta,
    se,
    aliased,
    mu,
    deviance: devOld,
    pearson,
    logLik: logLikelihood(family, y, mu, size),
    dfResidual: n - aliased.filter((a) => !a).length,
    iterations,
    converged,
  }
}

/** ML estimate of the NB2 θ given μ, by Newton steps on the score. */
function thetaMl(y: Float64Array, mu: Float64Array, theta: number): number {
  for (let step = 0; step < MAX_THETA_NEWTON; step++) {
    let score = 0
    let info = 0
    for (let i = 0; i < y.length; i++) {
      const tm = theta + mu[i]
      score += digamma(y[i] + theta) - digamma(theta) + Math.log(theta) + 1 - Math.log(tm) - (y[i] + theta) / tm
      info += trigamma(y[i] + theta) - trigamma(theta) + 1 / theta - 2 / tm + (y[i] + theta) / (tm * tm)
    }
    // info is the score's derivative; Newton needs it negative (concave log-likelihood).
    let next = info < 0 ? theta - score / info : score > 0 ? theta * 2 : theta / 2
    if (!(next > 0)) next = theta / 2
    if (next >= THETA_CAP) return THETA_CAP
    const done = Math.abs(next - theta) / theta < THETA_TOL
    theta = next
    if (done) break
  }
  return theta
}

/**
 * Negative binomial (NB2, Var = μ + μ²/θ) with θ by maximum likelihood,
 * alternating θ given μ with the GLM fit given θ. θ is capped at 1e8, where
 * the counts show no overdispersion.
 */
export function fitNegativeBinomial(X: Mat, y: Float64Array, offset: Float64Array): GlmFit & { theta: number; thetaCapped: boolean } {
  const n = y.length
  const ones = new Float64Array(n).fill(1)
  const pois = fitGlm(X, y, offset, ones, { kind: 'poisson' })
  let ss = 0
  for (let i = 0; i < n; i++) ss += (y[i] / pois.mu[i] - 1) ** 2
  let theta = Math.min(THETA_CAP, n / ss)
  let mu = pois.mu
  let fit: GlmFit | null = null
  let thetaConverged = false
  for (let round = 0; round < MAX_THETA_ROUNDS; round++) {
    const next = theta >= THETA_CAP ? THETA_CAP : thetaMl(y, mu, theta)
    const change = Math.abs(next - theta) / theta
    theta = next
    fit = fitGlm(X, y, offset, ones, { kind: 'negative_binomial', theta })
    mu = fit.mu
    if (theta >= THETA_CAP || (round > 0 && change < THETA_TOL)) {
      thetaConverged = true
      break
    }
  }
  const final = fit as GlmFit
  return { ...final, converged: final.converged && thetaConverged, theta, thetaCapped: theta >= THETA_CAP }
}
