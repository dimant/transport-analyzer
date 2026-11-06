const LANCZOS = [
  0.99999999999980993, 676.5203681218851, -1259.1392167224028, 771.32342877765313,
  -176.61502916214059, 12.507343278686905, -0.13857109526572012, 9.9843695780195716e-6,
  1.5056327351493116e-7,
]

/** ln Γ(x) for x > 0: Stirling series for large x, Lanczos below. */
export function logGamma(x: number): number {
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

/** Standard normal quantile, Wichura's AS241 (PPND16, about 1e-16 relative). */
export function normalQuantile(p: number): number {
  if (!(p > 0 && p < 1)) throw new RangeError('p must be in (0, 1)')
  const q = p - 0.5
  if (Math.abs(q) <= 0.425) {
    const r = 0.180625 - q * q
    return (
      (q *
        (((((((2509.0809287301226727 * r + 33430.575583588128105) * r + 67265.770927008700853) * r +
          45921.953931549871457) * r + 13731.693765509461125) * r + 1971.5909503065514427) * r +
          133.14166789178437745) * r + 3.387132872796366608)) /
      (((((((5226.495278852854561 * r + 28729.085735721942674) * r + 39307.89580009271061) * r +
        21213.794301586595867) * r + 5394.1960214247511077) * r + 687.1870074920579083) * r +
        42.313330701600911252) * r + 1)
    )
  }
  let r = q < 0 ? p : 1 - p
  r = Math.sqrt(-Math.log(r))
  let value: number
  if (r <= 5) {
    r -= 1.6
    value =
      (((((((7.7454501427834140764e-4 * r + 0.0227238449892691845833) * r + 0.24178072517745061177) * r +
        1.27045825245236838258) * r + 3.64784832476320460504) * r + 5.7694972214606914055) * r +
        4.6303378461565452959) * r + 1.42343711074968357734) /
      (((((((1.05075007164441684324e-9 * r + 5.475938084995344946e-4) * r + 0.0151986665636164571966) * r +
        0.14810397642748007459) * r + 0.68976733498510000455) * r + 1.6763848301838038494) * r +
        2.05319162663775882187) * r + 1)
  } else {
    r -= 5
    value =
      (((((((2.01033439929228813265e-7 * r + 2.71155556874348757815e-5) * r + 0.0012426609473880784386) * r +
        0.026532189526576123093) * r + 0.29656057182850489123) * r + 1.7848265399172913358) * r +
        5.4637849111641143699) * r + 6.6579046435011037772) /
      (((((((2.04426310338993978564e-15 * r + 1.4215117583164458887e-7) * r + 1.8463183175100546818e-5) * r +
        7.868691311456132591e-4) * r + 0.0148753612908506148525) * r + 0.13692988092273580531) * r +
        0.59983220655588793769) * r + 1)
  }
  return q < 0 ? -value : value
}

/** Two-sided standard normal p-value for z, as Q(1/2, z^2/2) so far tails keep precision. */
export function normalTwoSidedP(z: number): number {
  return gammaPQ(0.5, (z * z) / 2).q
}

/** ψ(x) for x > 0: recurrence up to x >= 6, then the asymptotic series. */
export function digamma(x: number): number {
  if (!(x > 0)) throw new RangeError('x must be positive')
  let shift = 0
  while (x < 6) {
    shift -= 1 / x
    x += 1
  }
  const inv2 = 1 / (x * x)
  const series =
    inv2 * (1 / 12 - inv2 * (1 / 120 - inv2 * (1 / 252 - inv2 * (1 / 240 - inv2 * (1 / 132 - inv2 * (691 / 32760))))))
  return shift + Math.log(x) - 0.5 / x - series
}

/** ψ'(x) for x > 0: recurrence up to x >= 6, then the asymptotic series. */
export function trigamma(x: number): number {
  if (!(x > 0)) throw new RangeError('x must be positive')
  let shift = 0
  while (x < 6) {
    shift += 1 / (x * x)
    x += 1
  }
  const inv = 1 / x
  const inv2 = inv * inv
  const series =
    inv *
    (1 +
      inv *
        (0.5 +
          inv * (1 / 6 - inv2 * (1 / 30 - inv2 * (1 / 42 - inv2 * (1 / 30 - inv2 * (5 / 66)))))))
  return shift + series
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
