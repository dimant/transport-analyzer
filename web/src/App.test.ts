import { fireEvent, render, screen, waitFor } from '@testing-library/svelte'
import { describe, expect, it, vi } from 'vitest'
import { columnType } from './api/columns'
import type { Query, QueryResult } from './api/types'
import { fromRows } from './stats/table'
import App from './App.svelte'

const cols = ['mode', 'year', 'vehicle_class', 'regulation_class', 'numerator', 'denominator', 'join_level', 'exposure_source', 'provenance']
const types = Object.fromEntries(cols.map((c) => [c, columnType(c)]))
const ratesResult = (): QueryResult => ({
  table: fromRows(cols, [['aviation', 2022, 'airplane', '121', 4, 1e9, 'mode', 'bts', 'measured']], types),
  notices: [],
})
const deferred = <T,>() => {
  let resolve!: (v: T) => void
  const promise = new Promise<T>((r) => (resolve = r))
  return { promise, resolve }
}
const makeApi = (runQuery: (q: Query) => Promise<QueryResult>) => ({
  getSources: async () => [],
  getVocabulary: async () => ({}),
  runQuery,
})

describe('App', () => {
  it('App_loadingPreset_updatesQueryForm', async () => {
    const runQuery = vi.fn(async () => ratesResult())
    render(App, { api: makeApi(runQuery) })
    expect((screen.getByLabelText('Metric') as HTMLSelectElement).value).toBe('passenger_miles')
    await fireEvent.change(screen.getByLabelText('Preset'), {
      target: { value: 'General aviation by aircraft class — deaths per 100k flight hours, 2022' },
    })
    await waitFor(() => expect((screen.getByLabelText('Metric') as HTMLSelectElement).value).toBe('flight_hours'))
    expect(runQuery).toHaveBeenCalledWith(expect.objectContaining({ metric: 'flight_hours', fromYear: 2022 }))
    // the method ran with the preset's bindings and shows its note
    await waitFor(() => expect(screen.getByText(/FAA survey hours cover Part 91/)).toBeTruthy())
    expect(screen.getAllByText('provenance').length).toBeGreaterThan(0)
  })

  it('App_runInFlight_disablesRunAndIgnoresStaleResponse', async () => {
    const first = deferred<QueryResult>()
    const second = deferred<QueryResult>()
    const pending = [first, second]
    const runQuery = vi.fn(() => pending.shift()!.promise)
    render(App, { api: makeApi(runQuery) })
    await fireEvent.click(screen.getByRole('button', { name: 'Run' }))
    expect((screen.getByRole('button', { name: 'Run' }) as HTMLButtonElement).disabled).toBe(true)
    // a preset load supersedes the in-flight query
    await fireEvent.change(screen.getByLabelText('Preset'), { target: { value: 'Airlines vs road — 2019–2023 combined' } })
    second.resolve(ratesResult())
    await waitFor(() => expect(screen.getByText('Method', { selector: 'h2' })).toBeTruthy())
    const newer = ratesResult()
    first.resolve({ ...newer, table: fromRows(['x'], [['stale']], {}) })
    await new Promise((r) => setTimeout(r, 0))
    // the stale single-column table must not have replaced the newer one
    expect(screen.queryByRole('columnheader', { name: 'x' })).toBeNull()
    expect((screen.getByRole('button', { name: 'Run' }) as HTMLButtonElement).disabled).toBe(false)
  })

  it('App_failedQuery_showsErrorAndKeepsMethod', async () => {
    const runQuery = vi.fn(async () => { throw new Error('boom') })
    render(App, { api: makeApi(runQuery) })
    await fireEvent.change(screen.getByLabelText('Preset'), { target: { value: 'Airlines vs road — 2019–2023 combined' } })
    await waitFor(() => expect(screen.getByText('boom')).toBeTruthy())
  })
})
