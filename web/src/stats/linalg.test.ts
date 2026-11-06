import { describe, expect, it } from 'vitest'
import { cholesky, choleskyInverse, choleskySolve, matrix, weightedCrossprod, weightedXty } from './linalg'
import type { Mat } from './linalg'

function fromRows(rows: number[][]): Mat {
  const m = matrix(rows.length, rows[0].length)
  rows.forEach((r, i) => r.forEach((v, j) => (m.data[i * m.cols + j] = v)))
  return m
}
const at = (m: Mat, i: number, j: number) => m.data[i * m.cols + j]

describe('linalg', () => {
  it('cholesky_solvesSpd', () => {
    const A = fromRows([[4, 2, 0.4], [2, 5, 1], [0.4, 1, 3]])
    const b = Float64Array.from([1, 2, 3])
    const c = cholesky(A)
    expect(c.aliased).toEqual([false, false, false])
    const x = choleskySolve(c, b)
    for (let i = 0; i < 3; i++) {
      let s = 0
      for (let j = 0; j < 3; j++) s += at(A, i, j) * x[j]
      expect(Math.abs(s - b[i])).toBeLessThan(1e-12)
    }
  })

  it('cholesky_flagsDependentColumn', () => {
    const base = [[1, 2], [1, -1], [1, 0.5], [1, 3], [1, 1.5]]
    const X = fromRows(base.map(([a, b]) => [a, b, a + b]))
    const A = weightedCrossprod(X, new Float64Array(5).fill(1))
    const c = cholesky(A)
    expect(c.aliased).toEqual([false, false, true])
    const inv = choleskyInverse(c)
    for (let k = 0; k < 3; k++) {
      expect(at(inv, 2, k)).toBeNaN()
      expect(at(inv, k, 2)).toBeNaN()
    }
    for (let i = 0; i < 2; i++)
      for (let j = 0; j < 2; j++) {
        let s = 0
        for (let k = 0; k < 2; k++) s += at(inv, i, k) * at(A, k, j)
        expect(Math.abs(s - (i === j ? 1 : 0))).toBeLessThan(1e-10)
      }
    const x = choleskySolve(c, Float64Array.from([1, 2, 3]))
    expect(x[2]).toBe(0)
  })

  it('weightedCrossprod_matchesNaive', () => {
    const X = fromRows([[1, 2], [3, 4], [5, 6]])
    const w = Float64Array.from([1, 2, 3])
    const A = weightedCrossprod(X, w)
    expect(Array.from(A.data)).toEqual([1 + 18 + 75, 2 + 24 + 90, 2 + 24 + 90, 4 + 32 + 108])
    const z = Float64Array.from([1, 1, 2])
    expect(Array.from(weightedXty(X, w, z))).toEqual([1 + 6 + 30, 2 + 8 + 36])
  })
})
