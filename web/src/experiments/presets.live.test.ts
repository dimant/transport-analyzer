import { describe, expect, test } from 'vitest'
import { runQuery } from '../api/client'
import { defaultParams } from '../stats/method'
import type { RateResult } from '../stats/rate'
import type { RegressionResult } from '../stats/regression'
import { methodById } from '../stats/registry'
import { PRESETS } from './presets'

// Runs every preset against a live API: TA_API=http://localhost:8080 npm test -- presets.live
const api = process.env.TA_API
const fetchLive = ((url: string, init?: RequestInit) => fetch(api + url, init)) as typeof fetch

const sig3 = (n: number | null) => (n === null ? 'null' : Number(n.toPrecision(3)).toString())
const isRegression = (methodId: string) => methodId === 'poisson-regression' || methodId === 'logistic-regression'

function logRegression(name: string, r: RegressionResult) {
  const terms = r.terms.map((t) => [t.term, t.level, sig3(t.estimate), sig3(t.lo), sig3(t.hi), sig3(t.p), t.events, t.size, t.note].join('\t'))
  const summary = r.summary.map((s) => `${s.label}: ${typeof s.value === 'number' ? Number(s.value.toPrecision(6)) : s.value}`)
  console.log(`${name}\nreference: ${r.reference}\n${terms.join('\n')}\n${summary.join('\n')}\nwarnings: ${JSON.stringify(r.warnings)}`)
}

describe.skipIf(!api)('presets against the live API', () => {
  test.each(PRESETS.map((p, i) => [i + 1, p.name, p] as const))('preset %i: %s', async (_i, name, p) => {
    const method = methodById(p.methodId)!
    const { table } = await runQuery(p.query, fetchLive)
    expect(table.length).toBeGreaterThan(0)
    const result = method.run(table, p.bindings, p.params)
    if (isRegression(p.methodId)) {
      const r = result as RegressionResult
      expect(r.terms.some((t) => t.note === '' && t.estimate !== null)).toBe(true)
      expect(r.summary.find((s) => s.label === 'converged')?.value).toBe('yes')
      expect(r.warnings).not.toContain('No rows left to fit.')
      logRegression(name, r)
    } else if (Array.isArray(result.warnings)) expect(result.warnings).toEqual([])
    const specs = method.render(result)
    expect(specs.length).toBeGreaterThan(0)
    for (const s of specs) {
      expect(s.kind !== 'table' ? s.data.length : s.rows.length).toBeGreaterThan(0)
    }
    if (p.methodId === 'rate') {
      const r = result as RateResult
      for (const row of r.rows) {
        expect(row.rate).not.toBeNull()
        expect(row.hi!).toBeGreaterThanOrEqual(row.rate!)
      }
      const lines = r.rows.map((row) =>
        [row.key.replace(/\u001f/g, ' | '), row.numerator, row.denominator, sig3(row.rate), sig3(row.lo), sig3(row.hi), Object.values(row.carry).join(' ; ')].join('\t'),
      )
      console.log(`${name}\n${lines.join('\n')}`)
    }
  })

  test('preset 2: Part 121 and road pooled rates match plan-2 verification', async () => {
    const p = PRESETS[1]
    const { table } = await runQuery(p.query, fetchLive)
    const r = methodById('rate')!.run(table, p.bindings, p.params) as RateResult
    const byMode = (mode: string) => r.rows.find((row) => row.groups.mode === mode)!
    const air = byMode('aviation')
    const road = byMode('highway')
    expect(air.groups.regulation_class).toBe('part_121')
    expect(Math.abs(air.rate! / 0.000128 - 1)).toBeLessThan(0.01)
    expect(Math.abs(road.rate! / 0.618 - 1)).toBeLessThan(0.01)
    expect(air.hi!).toBeGreaterThan(air.rate!)
    expect(road.hi!).toBeGreaterThan(road.rate!)
  })

  test.each([7, 8])('preset %i under poisson: Pearson dispersion', async (n) => {
    const p = PRESETS[n - 1]
    const { table } = await runQuery(p.query, fetchLive)
    const r = methodById('poisson-regression')!.run(table, p.bindings, { ...p.params, family: 'poisson' }) as RegressionResult
    expect(r.summary.find((s) => s.label === 'converged')?.value).toBe('yes')
    logRegression(`${p.name} (poisson)`, r)
  })

  test('preset 7 with rate_class only: part_121 rate ratio equals the ratio of pooled rates', async () => {
    const p = PRESETS[6]
    const { table } = await runQuery(p.query, fetchLive)
    const reg = methodById('poisson-regression')!.run(
      table, { ...p.bindings, covariates: ['rate_class'] }, { ...p.params, family: 'poisson' },
    ) as RegressionResult
    const rates = methodById('rate')!.run(
      table, { numerator: 'numerator', denominator: 'denominator', groups: ['rate_class'], carry: [] }, { ...defaultParams(methodById('rate')!), per: p.params.per },
    ) as RateResult
    const pooled = (cls: string) => rates.rows.find((row) => row.groups.rate_class === cls)!.rate!
    const expected = pooled('part_121') / pooled('light_duty')
    const term = reg.terms.find((t) => t.term === 'rate_class' && t.level === 'part_121')!
    expect(reg.reference).toBe('rate_class=light_duty')
    console.log(`cross-check: part_121 rate ratio ${term.estimate} vs pooled ${pooled('part_121')} / ${pooled('light_duty')} = ${expected}`)
    expect(Math.abs(term.estimate! / expected - 1)).toBeLessThan(1e-9)
  })
})
