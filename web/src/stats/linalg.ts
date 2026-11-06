/** Row-major dense matrix. */
export interface Mat {
  rows: number
  cols: number
  data: Float64Array
}

export interface Cholesky {
  L: Mat
  aliased: boolean[]
}

export function matrix(rows: number, cols: number): Mat {
  return { rows, cols, data: new Float64Array(rows * cols) }
}

/** XᵀWX for an n×p design X and n weights. */
export function weightedCrossprod(X: Mat, w: Float64Array): Mat {
  const { rows: n, cols: p, data } = X
  const A = matrix(p, p)
  for (let r = 0; r < n; r++) {
    const wr = w[r]
    for (let i = 0; i < p; i++) {
      const wx = wr * data[r * p + i]
      for (let j = i; j < p; j++) A.data[i * p + j] += wx * data[r * p + j]
    }
  }
  for (let i = 0; i < p; i++) for (let j = 0; j < i; j++) A.data[i * p + j] = A.data[j * p + i]
  return A
}

/** XᵀWz for an n×p design X, n weights and n working responses. */
export function weightedXty(X: Mat, w: Float64Array, z: Float64Array): Float64Array {
  const { rows: n, cols: p, data } = X
  const out = new Float64Array(p)
  for (let r = 0; r < n; r++) {
    const wz = w[r] * z[r]
    for (let i = 0; i < p; i++) out[i] += data[r * p + i] * wz
  }
  return out
}

/**
 * Cholesky A = LLᵀ with aliasing: column j is aliased when its remaining pivot
 * is ≤ tol × A[j][j] (or A[j][j] is 0). Aliased columns are zero in L and are
 * ignored when eliminating later columns.
 */
export function cholesky(A: Mat, tol = 1e-9): Cholesky {
  const p = A.rows
  const L = matrix(p, p)
  const aliased: boolean[] = new Array(p).fill(false)
  for (let j = 0; j < p; j++) {
    const diag = A.data[j * p + j]
    let pivot = diag
    for (let k = 0; k < j; k++) {
      if (!aliased[k]) pivot -= L.data[j * p + k] ** 2
    }
    if (diag === 0 || !(pivot > tol * diag)) {
      aliased[j] = true
      continue
    }
    const ljj = Math.sqrt(pivot)
    L.data[j * p + j] = ljj
    for (let i = j + 1; i < p; i++) {
      let s = A.data[i * p + j]
      for (let k = 0; k < j; k++) {
        if (!aliased[k]) s -= L.data[i * p + k] * L.data[j * p + k]
      }
      L.data[i * p + j] = s / ljj
    }
  }
  return { L, aliased }
}

/** Solves Ax = b on the non-aliased block; aliased entries of x are 0. */
export function choleskySolve(c: Cholesky, b: Float64Array): Float64Array {
  const { L, aliased } = c
  const p = L.rows
  const y = new Float64Array(p)
  for (let i = 0; i < p; i++) {
    if (aliased[i]) continue
    let s = b[i]
    for (let k = 0; k < i; k++) s -= L.data[i * p + k] * y[k]
    y[i] = s / L.data[i * p + i]
  }
  const x = new Float64Array(p)
  for (let i = p - 1; i >= 0; i--) {
    if (aliased[i]) continue
    let s = y[i]
    for (let k = i + 1; k < p; k++) s -= L.data[k * p + i] * x[k]
    x[i] = s / L.data[i * p + i]
  }
  return x
}

/** Inverse of the non-aliased block; rows and columns of aliased entries are NaN. */
export function choleskyInverse(c: Cholesky): Mat {
  const p = c.L.rows
  const inv = matrix(p, p)
  const e = new Float64Array(p)
  for (let j = 0; j < p; j++) {
    if (c.aliased[j]) {
      for (let i = 0; i < p; i++) {
        inv.data[i * p + j] = NaN
        inv.data[j * p + i] = NaN
      }
      continue
    }
    e.fill(0)
    e[j] = 1
    const col = choleskySolve(c, e)
    for (let i = 0; i < p; i++) {
      if (!c.aliased[i]) inv.data[i * p + j] = col[i]
    }
  }
  return inv
}
