const LANCZOS = [
  0.99999999999980993, 676.5203681218851, -1259.1392167224028, 771.32342877765313,
  -176.61502916214059, 12.507343278686905, -0.13857109526572012, 9.9843695780195716e-6,
  1.5056327351493116e-7,
]

/** ln Γ(x) for x > 0: Stirling series for large x, Lanczos below. */
function logGamma(x: number): number {
  if (x >= 15) {
    const inv = 1 / x
    const inv2 = inv * inv
    const series = inv * (1 / 12 - inv2 * (1 / 360 - inv2 * (1 / 1260 - inv2 * (1 / 1680))))
    return (x - 0.5) * Math.log(x) - x + 0.5 * Math.log(2 * Math.PI) + series
  }
  if (x < 0.5) return Math.log(Math.PI / Math.sin(Math.PI * x)) - logGamma(1 - x)
  const z = x - 1
  let sum = LANCZOS[0]
  for (let i = 1; i < 9; i++) sum += LANCZOS[i] / (z + i)
  const t = z + 7.5
  return 0.5 * Math.log(2 * Math.PI) + (z + 0.5) * Math.log(t) - t + Math.log(sum)
}

const EPS = 1e-16
const TINY = 1e-300
const MAX_ITER = 1_000_000

/** Regularized lower incomplete gamma P(a, x) and its complement Q. */
function gammaPQ(a: number, x: number): { p: number; q: number } {
  if (x <= 0) return { p: 0, q: 1 }
  const logPrefix = a * Math.log(x) - x - logGamma(a)
  if (x < a + 1) {
    let term = 1 / a
    let sum = term
    for (let n = 1; n < MAX_ITER; n++) {
      term *= x / (a + n)
      sum += term
      if (Math.abs(term) < Math.abs(sum) * EPS) break
    }
    const p = Math.min(1, sum * Math.exp(logPrefix))
    return { p, q: 1 - p }
  }
  // Modified Lentz continued fraction for Q
  let b = x + 1 - a
  let c = 1 / TINY
  let d = 1 / b
  let h = d
  for (let i = 1; i < MAX_ITER; i++) {
    const an = -i * (i - a)
    b += 2
    d = an * d + b
    if (Math.abs(d) < TINY) d = TINY
    c = b + an / c
    if (Math.abs(c) < TINY) c = TINY
    d = 1 / d
    const delta = d * c
    h *= delta
    if (Math.abs(delta - 1) < EPS) break
  }
  const q = Math.min(1, h * Math.exp(logPrefix))
  return { p: 1 - q, q }
}

export function regularizedGammaP(a: number, x: number): number {
  if (!(a > 0)) throw new RangeError('shape must be positive')
  return gammaPQ(a, x).p
}

/** Quantile of Gamma(shape, 1): the x with P(shape, x) = p. */
export function gammaQuantile(p: number, shape: number): number {
  if (!(p > 0 && p < 1)) throw new RangeError('p must be in (0, 1)')
  if (!(shape > 0)) throw new RangeError('shape must be positive')

  // Wilson-Hilferty start; a Gamma(shape) is chi-square(2 shape) / 2.
  const z = normalQuantile(p)
  const w = 1 - 1 / (9 * shape) + z / (3 * Math.sqrt(shape))
  let x = w > 0 ? shape * w * w * w : Math.exp((Math.log(p) + logGamma(shape + 1)) / shape)
  if (!(x > 0) || !isFinite(x)) x = shape

  let lo = 0
  let hi = Infinity
  for (let i = 0; i < 200; i++) {
    const f = gammaPQ(shape, x)
    // Use the smaller tail for the residual so p near 1 keeps precision.
    const resid = p < 0.5 ? f.p - p : 1 - p - f.q
    if (resid < 0) lo = x
    else hi = x
    const pdf = Math.exp((shape - 1) * Math.log(x) - x - logGamma(shape))
    let next = x - resid / pdf
    if (!(next > lo && next < hi)) next = isFinite(hi) ? (lo + hi) / 2 : x * 2
    if (Math.abs(next - x) <= 1e-14 * next) return next
    x = next
  }
  return x
}

/** Acklam's rational approximation to the standard normal quantile. */
function normalQuantile(p: number): number {
  const a = [-39.69683028665376, 220.9460984245205, -275.9285104469687, 138.357751867269, -30.66479806614716, 2.506628277459239]
  const b = [-54.47609879822406, 161.5858368580409, -155.6989798598866, 66.80131188771972, -13.28068155288572]
  const c = [-0.007784894002430293, -0.3223964580411365, -2.400758277161838, -2.549732539343734, 4.374664141464968, 2.938163982698783]
  const d = [0.007784695709041462, 0.3224671290700398, 2.445134137142996, 3.754408661907416]
  const plow = 0.02425
  if (p < plow) {
    const q = Math.sqrt(-2 * Math.log(p))
    return (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) /
      ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1)
  }
  if (p > 1 - plow) {
    const q = Math.sqrt(-2 * Math.log(1 - p))
    return -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) /
      ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1)
  }
  const q = p - 0.5
  const r = q * q
  return ((((((a[0] * r + a[1]) * r + a[2]) * r + a[3]) * r + a[4]) * r + a[5]) * q) /
    (((((b[0] * r + b[1]) * r + b[2]) * r + b[3]) * r + b[4]) * r + 1)
}

/** Exact (Garwood) Poisson confidence interval for a count k. */
export function garwood(k: number, confidence = 0.95): { lo: number; hi: number } {
  if (!Number.isInteger(k) || k < 0) throw new RangeError('k must be a non-negative integer')
  if (!(confidence > 0 && confidence < 1)) throw new RangeError('confidence must be in (0, 1)')
  const alpha = 1 - confidence
  const lo = k === 0 ? 0 : gammaQuantile(alpha / 2, k)
  const hi = gammaQuantile(1 - alpha / 2, k + 1)
  return { lo, hi }
}
