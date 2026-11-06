import type { ColumnType } from '../stats/table'

const COUNTS = ['vehicles', 'events', 'numerator', 'fatalities', 'vehicles_with_occupant_fatality']
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
  rate_class: 'The class the exposure row is defined for: the vehicle class, the regulation class, both, or the mode total when neither applies.',
  vehicles_with_occupant_fatality: 'Vehicles in which at least one occupant died.',
  numerator: 'The number of events or fatalities counted for this row, before any division by exposure.',
  denominator: 'The exposure the numerator is divided by, in raw units (passenger-miles, vehicle-miles or flight hours).',
  join_level: 'The exposure key the numerator was matched to: mode, vehicle_class, vehicle_class_parent, regulation_class, regulation_class_parent, or a combination joined with + such as vehicle_class+regulation_class.',
  exposure_source: 'The data source the denominator came from.',
  provenance: 'How the denominator was obtained, for example measured or a survey estimate.',
  period: 'The time bucket the row covers (year, quarter or month, by the chosen bucket).',
  rate: 'Numerator divided by denominator, scaled to the stated unit; the denominator is treated as fixed.',
  lo: 'Lower bound of the confidence interval: exact (Garwood) Poisson for a rate, Wald for a rate ratio or odds ratio.',
  hi: 'Upper bound of the confidence interval: exact (Garwood) Poisson for a rate, Wald for a rate ratio or odds ratio.',
  estimate:
    'The rate ratio or odds ratio for this level against the reference level, with the other covariates held fixed; for a numeric covariate, the ratio per unit. 1 means no difference.',
  p: 'Two-sided Wald p-value for the ratio being 1.',
  note: 'Why a row has no estimate: reference marks the level the others are compared with; not estimable means the level had no events (or all or none had the outcome); aliased means its column duplicates other covariates, so the data cannot separate them.',
}
