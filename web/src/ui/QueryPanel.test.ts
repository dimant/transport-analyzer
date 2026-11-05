import { fireEvent, render, screen } from '@testing-library/svelte'
import { describe, expect, it, vi } from 'vitest'
import type { Query, Source, Vocabulary } from '../api/types'
import QueryPanel from './QueryPanel.svelte'

const vocabulary: Vocabulary = {
  mode: [
    { code: 'aviation', label: 'Aviation', description: 'Aircraft.', parentCode: null },
    { code: 'highway', label: 'Highway', description: 'Road vehicles.', parentCode: null },
  ],
}
const src = (sourceId: string, kind: string): Source => ({
  sourceId, name: sourceId, mode: 'all', kind, url: '', definitions: '', caveats: '', dataCurrentThrough: null, lastRun: null,
})
const sources = [src('ntsb', 'events'), src('bts-passenger-miles', 'exposure'), src('faa-ga-survey', 'exposure')]

const aggregate: Query = {
  endpoint: 'aggregate', from: '2019-01-01', to: '2023-12-31', grain: 'event', bucket: 'none', groupBy: [], filters: {},
}
const rates: Query = {
  endpoint: 'rates', measure: 'occupant_fatalities', metric: 'passenger_miles', fromYear: 2019, toYear: 2023, groupBy: ['regulation_class'],
}

function setup(query: Query) {
  const onrun = vi.fn()
  render(QueryPanel, { query, vocabulary, sources, onrun })
  return onrun
}

describe('QueryPanel', () => {
  it('QueryPanel_aggregate_emitsQuery', async () => {
    const onrun = setup(aggregate)
    await fireEvent.change(screen.getByLabelText('Grain'), { target: { value: 'vehicle' } })
    await fireEvent.click(screen.getByLabelText('vehicle_class'))
    await fireEvent.click(screen.getByRole('button', { name: 'Add filter' }))
    await fireEvent.change(screen.getByLabelText('Filter dimension'), { target: { value: 'mode' } })
    await fireEvent.click(screen.getByLabelText('aviation'))
    await fireEvent.click(screen.getByRole('button', { name: 'Run' }))
    expect(onrun).toHaveBeenCalledWith({
      endpoint: 'aggregate', from: '2019-01-01', to: '2023-12-31', grain: 'vehicle', bucket: 'none',
      groupBy: ['vehicle_class'], filters: { mode: ['aviation'] },
    })
  })

  it('QueryPanel_eventGrain_hidesVehicleDimensions', async () => {
    setup(aggregate)
    expect(screen.queryByLabelText('make')).toBeNull()
    expect(screen.getByLabelText('mode')).toBeTruthy()
    await fireEvent.change(screen.getByLabelText('Grain'), { target: { value: 'vehicle' } })
    expect(screen.getByLabelText('make')).toBeTruthy()
  })

  it('QueryPanel_eventGrain_dropsVehicleOnlyChoices', async () => {
    const onrun = setup({ ...aggregate, grain: 'vehicle', groupBy: ['make', 'mode'] })
    await fireEvent.change(screen.getByLabelText('Grain'), { target: { value: 'event' } })
    await fireEvent.click(screen.getByRole('button', { name: 'Run' }))
    expect(onrun).toHaveBeenCalledWith(expect.objectContaining({ grain: 'event', groupBy: ['mode'] }))
  })

  it('QueryPanel_rates_emitsQuery', async () => {
    const onrun = setup(rates)
    await fireEvent.change(screen.getByLabelText('Metric'), { target: { value: 'vehicle_miles' } })
    await fireEvent.click(screen.getByLabelText('vehicle_class'))
    await fireEvent.change(screen.getByLabelText('Exposure source'), { target: { value: 'bts-passenger-miles' } })
    await fireEvent.click(screen.getByRole('button', { name: 'Run' }))
    expect(onrun).toHaveBeenCalledWith({
      endpoint: 'rates', measure: 'occupant_fatalities', metric: 'vehicle_miles', fromYear: 2019, toYear: 2023,
      groupBy: ['regulation_class', 'vehicle_class'], exposureSource: 'bts-passenger-miles',
    })
  })

  it('QueryPanel_endpointSwitch_keepsYears', async () => {
    const onrun = setup(rates)
    await fireEvent.change(screen.getByLabelText('Endpoint'), { target: { value: 'sample' } })
    await fireEvent.click(screen.getByRole('button', { name: 'Run' }))
    expect(onrun).toHaveBeenCalledWith({
      endpoint: 'sample', from: '2019-01-01', to: '2023-12-31', n: 10000, seed: 0, filters: {},
    })
  })

  it('QueryPanel_textFilter_splitsOnComma', async () => {
    const onrun = setup({ endpoint: 'sample', from: '2022-01-01', to: '2022-12-31', n: 500, seed: 7, filters: {} })
    await fireEvent.click(screen.getByRole('button', { name: 'Add filter' }))
    await fireEvent.change(screen.getByLabelText('Filter dimension'), { target: { value: 'state' } })
    await fireEvent.input(screen.getByLabelText('Filter values'), { target: { value: 'CA, TX' } })
    await fireEvent.click(screen.getByRole('button', { name: 'Run' }))
    expect(onrun).toHaveBeenCalledWith(expect.objectContaining({ n: 500, seed: 7, filters: { state: ['CA', 'TX'] } }))
  })
})
