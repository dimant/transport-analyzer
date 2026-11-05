import { fireEvent, render, screen, waitFor, within } from '@testing-library/svelte'
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
    // only a rendered rate table has a rate column, so the method ran with the preset's bindings
    const result = within(screen.getByRole('region', { name: 'Result' }))
    await waitFor(() => expect(result.getByRole('columnheader', { name: 'rate' })).toBeTruthy())
    expect(result.getByRole('columnheader', { name: 'exposure_source' })).toBeTruthy()
    expect(result.getByRole('columnheader', { name: 'provenance' })).toBeTruthy()
    expect((screen.getByLabelText('Method') as HTMLSelectElement).value).toBe('rate')
    expect((within(screen.getAllByRole('group', { name: 'Group by' }).at(-1)!).getByLabelText('vehicle_class') as HTMLInputElement).checked).toBe(true)
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
    let calls = 0
    const runQuery = vi.fn(async () => {
      if (++calls > 1) throw new Error('boom')
      return ratesResult()
    })
    render(App, { api: makeApi(runQuery) })
    await fireEvent.click(screen.getByRole('button', { name: 'Run' }))
    await waitFor(() => expect(screen.getByLabelText('Method')).toBeTruthy())
    await fireEvent.change(screen.getByLabelText('Method'), { target: { value: 'crosstab' } })
    await fireEvent.change(screen.getByLabelText('Preset'), { target: { value: 'Airlines vs road — 2019–2023 combined' } })
    await waitFor(() => expect(screen.getByText('boom')).toBeTruthy())
    expect((screen.getByLabelText('Method') as HTMLSelectElement).value).toBe('crosstab')
    expect(screen.queryByText(/Part 121 includes all-cargo/)).toBeNull()
  })

  it('App_note_clearedWhenAnalysisDiverges', async () => {
    const runQuery = vi.fn(async () => ratesResult())
    render(App, { api: makeApi(runQuery) })
    await fireEvent.change(screen.getByLabelText('Preset'), { target: { value: 'Airlines vs road — 2019–2023 combined' } })
    await waitFor(() => expect(screen.getByText(/Part 121 includes all-cargo/)).toBeTruthy())
    await fireEvent.change(screen.getByLabelText('Method'), { target: { value: 'crosstab' } })
    expect(screen.queryByText(/Part 121 includes all-cargo/)).toBeNull()
    await fireEvent.change(screen.getByLabelText('Preset'), { target: { value: 'Airlines vs road — 2019–2023 combined' } })
    await waitFor(() => expect(screen.getByText(/Part 121 includes all-cargo/)).toBeTruthy())
    await fireEvent.click(screen.getByRole('button', { name: 'Run' }))
    await waitFor(() => expect(screen.queryByText(/Part 121 includes all-cargo/)).toBeNull())
  })

  it('App_handRunRate_showsExposureSourceAndProvenance', async () => {
    render(App, { api: makeApi(async () => ratesResult()) })
    await fireEvent.click(screen.getByRole('button', { name: 'Run' }))
    await waitFor(() => expect(screen.getByLabelText('Events (numerator)')).toBeTruthy())
    await fireEvent.change(screen.getByLabelText('Events (numerator)'), { target: { value: 'numerator' } })
    await fireEvent.change(screen.getByLabelText('Exposure (denominator)'), { target: { value: 'denominator' } })
    await fireEvent.click(screen.getByRole('button', { name: 'Run method' }))
    const result = within(screen.getByRole('region', { name: 'Result' }))
    await waitFor(() => expect(result.getByRole('columnheader', { name: 'rate' })).toBeTruthy())
    expect(result.getByRole('columnheader', { name: 'exposure_source' })).toBeTruthy()
    expect(result.getByRole('columnheader', { name: 'provenance' })).toBeTruthy()
  })

  it('App_unknownMethodInExperiment_fallsBackToFirstMethodAndWarns', async () => {
    const e = {
      version: 1, name: 'ghosty', methodId: 'ghost',
      query: { endpoint: 'rates', measure: 'occupant_fatalities', metric: 'passenger_miles', fromYear: 2019, toYear: 2023, groupBy: ['regulation_class'] },
      bindings: { numerator: 'numerator', denominator: 'denominator' }, params: {},
    }
    localStorage.setItem('transport-analyzer.experiments', JSON.stringify([e]))
    try {
      render(App, { api: makeApi(async () => ratesResult()) })
      await fireEvent.change(screen.getByLabelText('Saved'), { target: { value: 'ghosty' } })
      await waitFor(() => expect(screen.getByText('Unknown method "ghost"; using Cross-tabulation.')).toBeTruthy())
      expect((screen.getByLabelText('Method') as HTMLSelectElement).value).toBe('crosstab')
    } finally {
      localStorage.clear()
    }
  })
})
