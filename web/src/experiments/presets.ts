import { defaultParams } from '../stats/method'
import { crosstab } from '../stats/crosstab'
import { heatmap } from '../stats/heatmap'
import { rate } from '../stats/rate'
import type { Experiment } from './storage'

const CARRY = ['join_level', 'exposure_source', 'provenance']
const PART_121_NOTE =
  'Part 121 includes all-cargo flights (3 of the 4 deaths in 2019 were on Atlas Air 3591, a cargo flight). ' +
  'Airline passenger-miles are domestic. Road is every highway vehicle occupant.'

const airlinesVsRoadQuery: Experiment['query'] = {
  endpoint: 'rates', measure: 'occupant_fatalities', metric: 'passenger_miles', fromYear: 2019, toYear: 2023, groupBy: ['regulation_class'],
}
const ratePreset = (name: string, query: Experiment['query'], groups: string[], per: number, note?: string): Experiment => ({
  version: 1,
  name,
  ...(note ? { note } : {}),
  query,
  methodId: 'rate',
  bindings: { numerator: 'numerator', denominator: 'denominator', groups, carry: [...CARRY] },
  params: { ...defaultParams(rate), per },
})

export const PRESETS: Experiment[] = [
  ratePreset('Airlines vs road — deaths per 100M passenger-miles, by year', airlinesVsRoadQuery, ['mode', 'regulation_class', 'year'], 1e8, PART_121_NOTE),
  ratePreset('Airlines vs road — 2019–2023 combined', airlinesVsRoadQuery, ['mode', 'regulation_class'], 1e8, PART_121_NOTE),
  ratePreset(
    'General aviation by aircraft class — deaths per 100k flight hours, 2022',
    { endpoint: 'rates', measure: 'occupant_fatalities', metric: 'flight_hours', fromYear: 2022, toYear: 2022, groupBy: ['vehicle_class', 'regulation_class'] },
    ['vehicle_class'],
    1e5,
    'FAA survey hours cover Part 91, on-demand Part 135, Part 137 and Part 133 together (general aviation). Aircraft of unknown class are listed as unmatched.',
  ),
  ratePreset(
    'Road by vehicle class — deaths per 100M vehicle-miles, by year',
    { endpoint: 'rates', measure: 'occupant_fatalities', metric: 'vehicle_miles', fromYear: 2019, toYear: 2023, groupBy: ['vehicle_class'] },
    ['vehicle_class', 'year'],
    1e8,
  ),
  {
    version: 1,
    name: 'US aviation deaths by aircraft class and year (heatmap)',
    query: {
      endpoint: 'aggregate', from: '2019-01-01', to: '2023-12-31', grain: 'vehicle', bucket: 'year', groupBy: ['vehicle_class'],
      filters: { mode: ['aviation'], country: ['US'] },
    },
    methodId: 'heatmap',
    bindings: { x: 'period', y: 'vehicle_class', value: 'occupant_fatalities' },
    params: defaultParams(heatmap),
  },
  {
    version: 1,
    name: 'Road deaths by vehicle class and operator type (crosstab)',
    query: {
      endpoint: 'aggregate', from: '2022-01-01', to: '2022-12-31', grain: 'vehicle', bucket: 'none', groupBy: ['vehicle_class', 'operator_type'],
      filters: { mode: ['highway'] },
    },
    methodId: 'crosstab',
    bindings: { row: 'vehicle_class', col: 'operator_type', value: 'occupant_fatalities' },
    params: defaultParams(crosstab),
  },
]
