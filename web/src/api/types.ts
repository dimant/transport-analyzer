import type { Table } from '../stats/table'

export type Filters = Record<string, string[]>
export type Query =
  | { endpoint: 'aggregate'; from: string; to: string; grain: 'event' | 'vehicle'; bucket: 'none' | 'year' | 'quarter' | 'month'; groupBy: string[]; filters: Filters }
  | { endpoint: 'sample'; from: string; to: string; n: number; seed: number; filters: Filters }
  | { endpoint: 'rates'; measure: 'occupant_fatalities' | 'societal_fatalities'; metric: 'passenger_miles' | 'vehicle_miles' | 'flight_hours'; fromYear: number; toYear: number; groupBy: ('vehicle_class' | 'regulation_class')[]; exposureSource?: string }
export interface Source { sourceId: string; name: string; mode: string; kind: string; url: string; definitions: string; caveats: string; dataCurrentThrough: string | null; lastRun: { status: string; finishedAt: string | null } | null }
export interface VocabEntry { code: string; label: string; description: string; parentCode: string | null }
export type Vocabulary = Record<string, VocabEntry[]>
export interface Notice { kind: 'scope' | 'unmatched' | 'unattributed' | 'sample'; text: string; table?: Table }
export interface QueryResult { table: Table; notices: Notice[] }
