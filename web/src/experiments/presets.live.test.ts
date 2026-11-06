import { describe, expect, test } from 'vitest'
import { runQuery } from '../api/client'
import type { RateResult } from '../stats/rate'
import { methodById } from '../stats/registry'
import { PRESETS } from './presets'

// Runs every preset against a live API: TA_API=http://localhost:8080 npm test -- presets.live
const api = process.env.TA_API
const fetchLive = ((url: string, init?: RequestInit) => fetch(api + url, init)) as typeof fetch

const sig3 = (n: number | null) => (n === null ? 'null' : Number(n.toPrecision(3)).toString())

describe.skipIf(!api)('presets against the live API', () => {
  test.each(PRESETS.map((p, i) => [i + 1, p.name, p] as const))('preset %i: %s', async (_i, name, p) => {
    const method = methodById(p.methodId)!
    const { table } = await runQuery(p.query, fetchLive)
    expect(table.length).toBeGreaterThan(0)
    const result = method.run(table, p.bindings, p.params)
    if (Array.isArray(result.warnings)) expect(result.warnings).toEqual([])
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
})
