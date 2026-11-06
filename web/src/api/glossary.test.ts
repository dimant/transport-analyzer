import { describe, expect, it } from 'vitest'
import { renderRegression } from '../stats/regression'
import { buildGlossary } from './glossary'
import type { Source, Vocabulary } from './types'

const vocab: Vocabulary = {
  vehicle_class: [{ code: 'light_duty', label: 'Light duty', description: 'Cars and light trucks.', parentCode: null }],
}
const sources: Source[] = [{ sourceId: 'bts-passenger-miles', name: 'BTS Passenger Miles', mode: 'aviation', kind: 'exposure', url: 'u', definitions: 'Miles flown.', caveats: 'Estimated.', dataCurrentThrough: '2023-12-31', lastRun: null }]

describe('glossary', () => {
  it('glossary_describesVocabAndSources', () => {
    const g = buildGlossary(vocab, sources)
    expect(g.describe('vehicle_class_parent', 'light_duty')).toBe('Light duty: Cars and light trucks.')
    const s = g.describe('exposure_source', 'bts-passenger-miles')!
    expect(s).toBe('BTS Passenger Miles. Miles flown. Caveats: Estimated. Data current through 2023-12-31.')
    expect(g.describe('source_id', 'bts-passenger-miles')).toBe(s)
    expect(g.describe('make', 'Subaru')).toBeUndefined()
    expect(g.describe('vehicle_class', null)).toBeUndefined()
    expect(g.describe('vehicle_class', 'nope')).toBeUndefined()
  })
  it('glossary_help', () => {
    const g = buildGlossary(vocab, sources)
    expect(g.help('join_level')).toContain('regulation_class_parent')
    expect(g.help('make')).toBeUndefined()
  })
  it('glossary_help_coversRegressionColumns', () => {
    const g = buildGlossary(vocab, sources)
    const result = { ratioName: 'rate ratio' as const, eventsLabel: 'fatalities', sizeLabel: 'passenger_miles', confidence: 0.95, terms: [], reference: '', summary: [], warnings: [] }
    const [terms, , summary] = renderRegression(result)
    for (const c of [...terms.columns, ...summary.columns]) expect(g.help(c.key), c.key).toBeTruthy()
  })
})
