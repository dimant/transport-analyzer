import { describe, expect, it } from 'vitest'
import { columnType } from '../api/columns'
import { defaultParams, validateBindings } from '../stats/method'
import { methodById } from '../stats/registry'
import { fromRows, type Table } from '../stats/table'
import { PRESETS } from './presets'
import type { Experiment } from './storage'

const RATES_COLUMNS = ['mode', 'year', 'vehicle_class', 'regulation_class', 'numerator', 'denominator', 'join_level', 'exposure_source', 'provenance']
const VEHICLE_COUNTS = [
  'vehicles', 'events', 'occupant_fatalities', 'occupant_serious_injuries', 'occupant_minor_injuries',
  'nonoccupant_fatalities', 'nonoccupant_serious_injuries', 'nonoccupant_minor_injuries',
]

function columnsOf(q: Experiment['query']): string[] {
  if (q.endpoint === 'rates') return RATES_COLUMNS
  if (q.endpoint === 'aggregate' && q.grain === 'vehicle') return [...q.groupBy, ...(q.bucket !== 'none' ? ['period'] : []), ...VEHICLE_COUNTS]
  throw new Error('no preset uses this query')
}
const tableFor = (q: Experiment['query']): Table => {
  const cols = columnsOf(q)
  return fromRows(cols, [], Object.fromEntries(cols.map((c) => [c, columnType(c)])))
}

describe('PRESETS', () => {
  it('has the six presets with unique names', () => {
    expect(PRESETS).toHaveLength(6)
    expect(new Set(PRESETS.map((p) => p.name)).size).toBe(6)
  })

  it.each(PRESETS.map((p) => [p.name, p] as const))('presets_bindToColumnsTheirQueryReturns: %s', (_name, p) => {
    const method = methodById(p.methodId)
    expect(method).toBeDefined()
    expect(validateBindings(method!, tableFor(p.query), p.bindings)).toEqual([])
    for (const k of Object.keys(defaultParams(method!))) expect(p.params).toHaveProperty(k)
  })

  it('rate presets carry exposure source and provenance', () => {
    for (const p of PRESETS.slice(0, 4)) expect(p.bindings.carry).toEqual(['join_level', 'exposure_source', 'provenance'])
  })
})
