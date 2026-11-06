import { render } from '@testing-library/svelte'
import { describe, expect, it } from 'vitest'
import type { ForestSpec, Method, TableSpec } from '../stats/method'
import type { Table } from '../stats/table'
import ResultPanel from './ResultPanel.svelte'

const spec: TableSpec = {
  kind: 'table',
  columns: [{ key: 'a', label: 'a', format: 'text' }],
  rows: [{ a: 'x' }],
}
const method: Method = {
  id: 'fake', name: 'Fake', description: '', inputs: [], params: [],
  run: () => null,
  render: () => [spec],
}
const table: Table = { columns: [], length: 0, meta: {} }

describe('ResultPanel', () => {
  it('resultTable_sitsInScrollContainer', () => {
    const { container } = render(ResultPanel, { run: { method, table, bindings: {}, params: {} } })
    const parent = container.querySelector('table')!.parentElement!
    expect(parent.classList.contains('scroll')).toBe(true)
  })
  it('forestSpec_mountsFigure', () => {
    const forest: ForestSpec = {
      kind: 'forest', label: 'term', estimate: 'est', lo: 'lo', hi: 'hi', xLabel: 'rate ratio',
      data: [{ term: 'a', est: 1.5, lo: 1.1, hi: 2 }],
    }
    const m: Method = { ...method, render: () => [forest] }
    const { container } = render(ResultPanel, { run: { method: m, table, bindings: {}, params: {} } })
    expect(container.querySelector('figure')).not.toBeNull()
  })
})
