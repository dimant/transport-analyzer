import { describe, expect, it } from 'vitest'
import { defaultParams, validateBindings, type Method } from './method'
import { fromRows } from './table'

const fake: Method = {
  id: 'fake',
  name: 'Fake',
  description: '',
  inputs: [
    { key: 'n', label: 'Numerator', types: ['count'] },
    { key: 'g', label: 'Groups', types: ['categorical'], multiple: true, optional: true },
  ],
  params: [
    { key: 'per', label: 'Per', kind: 'number', default: 100 },
    { key: 'on', label: 'On', kind: 'boolean', default: true },
    { key: 'how', label: 'How', kind: 'select', options: ['a', 'b'], default: 'b' },
  ],
  run: () => 0,
  render: () => [],
}
const table = fromRows(['n', 'g', 'c'], [[1, 'x', 2]], { n: 'count', c: 'continuous' })

describe('method', () => {
  it('validateBindings_missingColumn_reportsError', () => {
    expect(validateBindings(fake, table, { n: 'zzz' })).toEqual(['Numerator: column "zzz" is not in the table'])
  })
  it('validateBindings_wrongType_reportsError', () => {
    expect(validateBindings(fake, table, { n: 'c' })).toEqual(['Numerator: column "c" is continuous, needs one of count'])
  })
  it('validateBindings_requiredUnbound_reportsError', () => {
    expect(validateBindings(fake, table, {})).toEqual(['Numerator: choose a column'])
    expect(validateBindings(fake, table, { n: [] })).toEqual(['Numerator: choose a column'])
  })
  it('validateBindings_optionalUnbound_ok', () => {
    expect(validateBindings(fake, table, { n: 'n' })).toEqual([])
    expect(validateBindings(fake, table, { n: 'n', g: ['g'] })).toEqual([])
  })
  it('validateBindings_arrayForSingle_reportsError', () => {
    expect(validateBindings(fake, table, { n: ['n'] })).toEqual(['Numerator: takes one column'])
  })
  it('defaultParams_collectsDefaults', () => {
    expect(defaultParams(fake)).toEqual({ per: 100, on: true, how: 'b' })
  })
  it('concreteMethod_assignableToMethod', () => {
    const m: Method<{ per: number }, number> = {
      id: 'c', name: 'C', description: '', inputs: [], params: [],
      run(_t, _b, p) { return p.per },
      render(r) { return [{ kind: 'table', columns: [], rows: [{ v: r }] }] },
    }
    const general: Method = m
    expect(defaultParams(general)).toEqual({})
    expect(validateBindings(m, table, {})).toEqual([])
  })
})
