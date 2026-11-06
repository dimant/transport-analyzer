import { describe, expect, it } from 'vitest'
import { ApiError, buildUrl, runQuery } from './client'
import rates from './fixtures/rates-2022.json'
import type { Query } from './types'

const ratesQuery: Query = { endpoint: 'rates', measure: 'occupant_fatalities', metric: 'passenger_miles', fromYear: 2022, toYear: 2022, groupBy: [] }

function fake(status: number, body: unknown): typeof fetch {
  return (async () => new Response(typeof body === 'string' ? body : JSON.stringify(body), { status })) as typeof fetch
}

describe('buildUrl', () => {
  it('buildUrl_aggregate', () => {
    expect(buildUrl({ endpoint: 'aggregate', grain: 'vehicle', from: '2022-01-01', to: '2022-12-31', bucket: 'year', groupBy: ['vehicle_class'], filters: { mode: ['aviation'], country: ['US'] } }))
      .toBe('/api/events/aggregate?grain=vehicle&from=2022-01-01&to=2022-12-31&bucket=year&groupBy=vehicle_class&f.mode=aviation&f.country=US')
  })
  it('buildUrl_rates_omitsEmptyGroupBy', () => {
    expect(buildUrl(ratesQuery)).toBe('/api/rates?measure=occupant_fatalities&metric=passenger_miles&fromYear=2022&toYear=2022')
    expect(buildUrl({ ...ratesQuery, groupBy: ['vehicle_class', 'regulation_class'], exposureSource: 'x' }))
      .toBe('/api/rates?measure=occupant_fatalities&metric=passenger_miles&fromYear=2022&toYear=2022&groupBy=vehicle_class,regulation_class&exposureSource=x')
  })
  it('buildUrl_encodesValues', () => {
    const u = buildUrl({ endpoint: 'sample', from: '2022-01-01', to: '2022-12-31', n: 1, seed: 0, filters: { state: ['New Mexico'], make: ['A&B', 'C#'] } })
    expect(u).toContain('f.state=New%20Mexico')
    expect(u).toContain('f.make=A%26B,C%23')
  })
  it('buildUrl_sample', () => {
    expect(buildUrl({ endpoint: 'sample', from: '2022-01-01', to: '2022-12-31', n: 100, seed: 42, filters: { mode: ['aviation', 'highway'], state: [] } }))
      .toBe('/api/events/sample?from=2022-01-01&to=2022-12-31&n=100&seed=42&f.mode=aviation,highway')
  })
})

describe('runQuery', () => {
  it('runQuery_rates_buildsTableAndNotices', async () => {
    let url = ''
    const f = (async (u: string) => { url = u; return new Response(JSON.stringify(rates)) }) as unknown as typeof fetch
    const r = await runQuery(ratesQuery, f)
    expect(url).toBe(buildUrl(ratesQuery))
    expect(r.table.length).toBe(2)
    const type = (n: string) => r.table.columns.find((c) => c.name === n)!.type
    expect(type('numerator')).toBe('count')
    expect(type('denominator')).toBe('exposure')
    expect(type('year')).toBe('categorical')
    const year = r.table.columns.find((c) => c.name === 'year')!
    expect(year.type === 'categorical' && year.values[0]).toBe('2022')
    expect(r.notices.map((n) => n.kind)).toEqual(['scope', 'unmatched', 'unattributed'])
    expect(r.notices[0].text).toBe('US events only')
    const um = r.notices[1].table!
    expect(um.columns.map((c) => c.name)).toEqual(['mode', 'year', 'vehicle_class', 'regulation_class', 'numerator', 'reason'])
    const rc = um.columns.find((c) => c.name === 'regulation_class')!
    expect(rc.type === 'categorical' && rc.values[0]).toBe('part_91')
    expect(r.notices[2].table!.columns.map((c) => c.name)).toEqual(['mode', 'year', 'fatalities'])
    expect(r.table.meta.metric).toBe('passenger_miles')
    expect(r.table.meta.measure).toBe('occupant_fatalities')
  })
  it('rates_addsRateClass', async () => {
    const columns = ['mode', 'year', 'vehicle_class', 'regulation_class', 'numerator']
    const rows = [['highway', 2022, 'light_duty', null, 1], ['aviation', 2022, null, 'part_121', 2], ['highway', 2022, null, null, 3]]
    const r = await runQuery(ratesQuery, fake(200, { columns, rows, unmatched: [], unattributed: [] }))
    const names = r.table.columns.map((c) => c.name)
    expect(names.indexOf('rate_class')).toBe(names.indexOf('regulation_class') + 1)
    const rc = r.table.columns.find((c) => c.name === 'rate_class')!
    expect(rc.type).toBe('categorical')
    expect(rc.type === 'categorical' && rc.values).toEqual(['light_duty', 'part_121', 'highway'])
  })
  it('runQuery_sample_notice', async () => {
    const f = fake(200, { columns: ['source_id', 'occupant_fatalities'], rows: [['a', 1]], total: 50, n: 1, seed: 7 })
    const r = await runQuery({ endpoint: 'sample', from: 'a', to: 'b', n: 1, seed: 7, filters: {} }, f)
    expect(r.notices).toEqual([{ kind: 'sample', text: '1 of 50 matching rows, seed 7' }])
    expect(r.table.meta).toEqual({ endpoint: 'sample', grain: 'vehicle' })
    expect(r.table.columns[1].type).toBe('count')
  })
  it('runQuery_400_throwsApiErrorWithBody', async () => {
    const body = { error: "dimension 'make' needs grain=vehicle", parameter: 'groupBy' }
    const err = await runQuery(ratesQuery, fake(400, body)).catch((e) => e)
    expect(err).toBeInstanceOf(ApiError)
    expect(err.status).toBe(400)
    expect(err.message).toBe("dimension 'make' needs grain=vehicle")
  })
  it('runQuery_nonJsonError_usesText', async () => {
    const err = await runQuery(ratesQuery, fake(500, 'boom')).catch((e) => e)
    expect(err.message).toBe('boom')
    expect(err.body).toBe('boom')
  })
})
