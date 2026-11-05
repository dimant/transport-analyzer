import type { ColumnType } from '../stats/table'

const COUNTS = ['vehicles', 'events', 'numerator', 'fatalities']
const EXPOSURE = ['denominator', 'value']
const CONTINUOUS = ['occupants', 'crew_count', 'vehicle_age_years', 'year_built', 'standard_error']

export const COLUMN_TYPES: Record<string, ColumnType> = {
  ...Object.fromEntries(COUNTS.map((n) => [n, 'count' as const])),
  ...Object.fromEntries(EXPOSURE.map((n) => [n, 'exposure' as const])),
  ...Object.fromEntries(CONTINUOUS.map((n) => [n, 'continuous' as const])),
}

export function columnType(name: string): ColumnType {
  if (name in COLUMN_TYPES) return COLUMN_TYPES[name]
  if (name.endsWith('_fatalities') || name.endsWith('_injuries')) return 'count'
  return 'categorical'
}

export const EVENT_DIMENSIONS = ['source_id', 'mode', 'event_type', 'severity', 'state', 'country', 'operation_class', 'phase']
export const VEHICLE_DIMENSIONS = [
  ...EVENT_DIMENSIONS,
  'vehicle_class', 'vehicle_class_parent', 'operator_type', 'regulation_class', 'maintenance_regime',
  'occupants', 'crew_count', 'vehicle_age_years', 'year_built', 'make',
]

export const VOCAB_ATTRIBUTE: Record<string, string> = {
  mode: 'mode',
  vehicle_class: 'vehicle_class',
  operator_type: 'operator_type',
  regulation_class: 'regulation_class',
  maintenance_regime: 'maintenance_regime',
  vehicle_class_parent: 'vehicle_class',
}

export const COLUMN_HELP: Record<string, string> = {
  numerator: 'The number of events or fatalities counted for this row, before any division by exposure.',
  denominator: 'The exposure the numerator is divided by, in raw units (passenger-miles, vehicle-miles or flight hours).',
  join_level: 'The exposure key the numerator was matched to: mode, vehicle_class, vehicle_class_parent, regulation_class, regulation_class_parent, or a combination joined with + such as vehicle_class+regulation_class.',
  exposure_source: 'The data source the denominator came from.',
  provenance: 'How the denominator was obtained, for example measured or a survey estimate.',
  period: 'The time bucket the row covers (year, quarter or month, by the chosen bucket).',
  rate: 'Numerator divided by denominator, scaled to the stated unit; the denominator is treated as fixed.',
  lo: 'Lower bound of the exact (Garwood) Poisson confidence interval for the rate.',
  hi: 'Upper bound of the exact (Garwood) Poisson confidence interval for the rate.',
}
