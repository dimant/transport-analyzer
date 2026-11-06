import { describe, expect, it } from 'vitest'
import {
  digamma,
  garwood,
  gammaQuantile,
  normalQuantile,
  normalTwoSidedP,
  regularizedGammaP,
  trigamma,
} from './distributions'

function close(actual: number, expected: number, rel = 1e-6) {
  expect(Math.abs(actual - expected)).toBeLessThanOrEqual(rel * Math.max(1, Math.abs(expected)))
}

describe('garwood', () => {
  it('garwood_95', () => {
    const cases: [number, number, number][] = [
      [0, 0, 3.688879],
      [1, 0.025318, 5.571643],
      [4, 1.089865, 10.241589],
      [10, 4.795389, 18.390356],
      [100, 81.363991, 121.626794],
    ]
    for (const [k, lo, hi] of cases) {
      const r = garwood(k)
      if (k === 0) expect(r.lo).toBe(0)
      else close(r.lo, lo)
      close(r.hi, hi)
    }
  })

  it('garwood_90', () => {
    const r = garwood(3, 0.9)
    close(r.lo, 0.817691)
    close(r.hi, 7.753657)
  })

  it('garwood_large_k', () => {
    const k = 33679
    const r = garwood(k)
    expect(r.lo).toBeLessThan(k)
    expect(r.hi).toBeGreaterThan(k)
    const expected = 2 * 1.959964 * Math.sqrt(k)
    expect(Math.abs(r.hi - r.lo - expected)).toBeLessThanOrEqual(0.01 * expected)
  })

  it('garwood_rejectsBadInput', () => {
    expect(() => garwood(-1)).toThrow(RangeError)
    expect(() => garwood(1.5)).toThrow(RangeError)
    expect(() => garwood(1, 1)).toThrow(RangeError)
  })
})

describe('gamma', () => {
  it('gammaQuantile_invertsP', () => {
    for (const shape of [0.5, 1, 7.5, 250]) {
      for (const p of [0.001, 0.5, 0.999]) {
        expect(Math.abs(regularizedGammaP(shape, gammaQuantile(p, shape)) - p)).toBeLessThan(1e-9)
      }
    }
  })

  it('gammaQuantile_largeShapeAndExtremeP', () => {
    for (const shape of [43000, 1e6]) {
      for (const p of [1e-10, 0.025, 0.975, 1 - 1e-10]) {
        expect(Math.abs(regularizedGammaP(shape, gammaQuantile(p, shape)) - p)).toBeLessThan(1e-9)
      }
    }
  })

  it('gammaQuantile_rejectsBadInput', () => {
    expect(() => gammaQuantile(0, 1)).toThrow(RangeError)
    expect(() => gammaQuantile(1, 1)).toThrow(RangeError)
    expect(() => gammaQuantile(0.5, 0)).toThrow(RangeError)
  })

  it('regularizedGammaP_exponential', () => {
    for (const x of [0.1, 1, 10]) close(regularizedGammaP(1, x), 1 - Math.exp(-x), 1e-12)
  })
})

describe('normal', () => {
  it('normalQuantile matches scipy', () => {
    close(normalQuantile(0.975), 1.959963984540054, 1e-12)
    close(normalQuantile(0.995), 2.5758293035489004, 1e-12)
    close(normalQuantile(1e-10), -6.361340902404056, 1e-9)
  })

  it('normalQuantile rejects p outside (0, 1)', () => {
    expect(() => normalQuantile(0)).toThrow(RangeError)
    expect(() => normalQuantile(1)).toThrow(RangeError)
  })

  it('normalTwoSidedP keeps tail precision', () => {
    close(normalTwoSidedP(1.959963984540054), 0.05, 1e-12)
    const expected = 1.5239706048320948e-23
    expect(Math.abs(normalTwoSidedP(10) - expected) / expected).toBeLessThan(1e-6)
    expect(normalTwoSidedP(0)).toBe(1)
  })
})

describe('digamma and trigamma', () => {
  it('digamma matches scipy', () => {
    close(digamma(1), -0.5772156649015329, 1e-12)
    close(digamma(0.5), -1.9635100260214235, 1e-12)
    close(digamma(7.3), 1.917820335637986, 1e-12)
  })

  it('trigamma matches scipy', () => {
    close(trigamma(1), 1.6449340668482266, 1e-10)
    close(trigamma(0.5), 4.93480220054468, 1e-10)
    close(trigamma(7.3), 0.14679576813142703, 1e-10)
  })
})
