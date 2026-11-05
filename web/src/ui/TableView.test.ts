import { render } from '@testing-library/svelte'
import { describe, expect, it } from 'vitest'
import { buildGlossary } from '../api/glossary'
import type { TableSpec } from '../stats/method'
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
})
