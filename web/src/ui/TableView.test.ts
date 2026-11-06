import { render } from '@testing-library/svelte'
import { describe, expect, it } from 'vitest'
import { buildGlossary } from '../api/glossary'
import type { TableSpec } from '../stats/method'
import { crosstab } from '../stats/crosstab'
import { fromRows } from '../stats/table'
import TableView from './TableView.svelte'

const glossary = buildGlossary(
  { vehicle_class: [{ code: 'light_duty', label: 'Light duty', description: 'Cars and light trucks.', parentCode: null }] },
  [],
)
const spec: TableSpec = {
  kind: 'table',
  columns: [
    { key: 'vehicle_class', label: 'vehicle_class', format: 'text' },
    { key: 'numerator', label: 'deaths', format: 'integer' },
  ],
  rows: [{ vehicle_class: 'light_duty', numerator: 1234 }, { vehicle_class: null, numerator: 2 }],
}

describe('TableView', () => {
  it('TableView_showsHelpAndDescriptions', () => {
    const { container } = render(TableView, { spec, glossary })
    const heads = [...container.querySelectorAll('th')]
    expect(heads[1].getAttribute('title')).toBeTruthy()
    const cells = container.querySelectorAll('tbody tr:first-child td')
    expect(cells[0].getAttribute('title')).toBe('Light duty: Cars and light trucks.')
    expect(cells[1].textContent).toBe('1,234')
    expect(cells[1].classList.contains('num')).toBe(true)
    expect(container.querySelectorAll('tbody tr:nth-child(2) td')[0].textContent).toBe('(none)')
  })

  it('TableView_rowNormalizedCrosstab_showsPercentages', () => {
    const t = fromRows(['a', 'b'], [['x', 'p'], ['x', 'p'], ['x', 'p'], ['x', 'q']], {})
    const [s] = crosstab.render(crosstab.run(t, { row: 'a', col: 'b' }, { normalize: 'row', margins: true })) as TableSpec[]
    const { container } = render(TableView, { spec: s })
    const text = container.textContent ?? ''
    expect(text).toContain('75.0%')
    expect(text).toContain('25.0%')
    expect(text).toContain('100.0%')
  })

  it('TableView_ratioColumns_readApartWithinARow', () => {
    const ratios: TableSpec = {
      kind: 'table',
      columns: [
        { key: 'estimate', label: 'odds ratio', format: 'ratio' },
        { key: 'lo', label: 'lo', format: 'ratio' },
        { key: 'hi', label: 'hi', format: 'ratio' },
        { key: 'p', label: 'p', format: 'p' },
      ],
      rows: [{ estimate: 1.0563, lo: 1.0551, hi: 1.0575, p: 0 }, { estimate: 0.5, lo: 0.24443036, hi: 1.02284, p: 0.06 }],
    }
    const { container } = render(TableView, { spec: ratios })
    const row = (i: number) => [...container.querySelectorAll(`tbody tr:nth-child(${i}) td`)].map((td) => td.textContent)
    expect(row(1)).toEqual(['1.056', '1.055', '1.058', '< 1e-300'])
    expect(row(2)).toEqual(['0.5', '0.244', '1.02', '0.06'])
  })
})
