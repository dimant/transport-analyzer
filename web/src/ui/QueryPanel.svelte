<script lang="ts">
  import { EVENT_DIMENSIONS, VEHICLE_DIMENSIONS, VOCAB_ATTRIBUTE } from '../api/columns'
  import type { Filters, Query, Source, Vocabulary } from '../api/types'

  type Endpoint = Query['endpoint']
  type RatesQuery = Extract<Query, { endpoint: 'rates' }>
  type AggregateQuery = Extract<Query, { endpoint: 'aggregate' }>
  interface FilterRow {
    id: number
    dimension: string
    values: string[]
    text: string
  }

  let { query, vocabulary, sources, onrun, busy = false }: {
    query: Query
    vocabulary: Vocabulary
    sources: Source[]
    onrun: (q: Query) => void
    busy?: boolean
  } = $props()

  const yearOf = (d: string) => Number(d.slice(0, 4))
  // read once: the parent re-creates the panel (keyed) when an experiment replaces the query
  const first = (() => query)()
  let nextId = 0
  const rowsOf = (f: Filters): FilterRow[] =>
    Object.entries(f).map(([dimension, values]) => ({ id: nextId++, dimension, values: [...values], text: values.join(',') }))

  let endpoint = $state<Endpoint>(first.endpoint)
  let from = $state(first.endpoint === 'rates' ? `${first.fromYear}-01-01` : first.from)
  let to = $state(first.endpoint === 'rates' ? `${first.toYear}-12-31` : first.to)
  let fromYear = $state(first.endpoint === 'rates' ? first.fromYear : yearOf(first.from))
  let toYear = $state(first.endpoint === 'rates' ? first.toYear : yearOf(first.to))
  let grain = $state<AggregateQuery['grain']>(first.endpoint === 'aggregate' ? first.grain : 'event')
  let bucket = $state<AggregateQuery['bucket']>(first.endpoint === 'aggregate' ? first.bucket : 'none')
  let groupBy = $state<string[]>(first.endpoint === 'aggregate' ? [...first.groupBy] : [])
  let n = $state(first.endpoint === 'sample' ? first.n : 10000)
  let seed = $state(first.endpoint === 'sample' ? first.seed : 0)
  let filterRows = $state<FilterRow[]>(first.endpoint === 'rates' ? [] : rowsOf(first.filters))
  let measure = $state<RatesQuery['measure']>(first.endpoint === 'rates' ? first.measure : 'occupant_fatalities')
  let metric = $state<RatesQuery['metric']>(first.endpoint === 'rates' ? first.metric : 'passenger_miles')
  let rateGroupBy = $state<string[]>(first.endpoint === 'rates' ? [...first.groupBy] : ['regulation_class'])
  let exposureSource = $state(first.endpoint === 'rates' ? (first.exposureSource ?? '') : '')

  const RATE_DIMENSIONS = ['vehicle_class', 'regulation_class']
  const exposureSources = $derived(sources.filter((s) => s.kind === 'exposure'))
  const aggregateDimensions = $derived(grain === 'vehicle' ? VEHICLE_DIMENSIONS : EVENT_DIMENSIONS)
  const filterDimensions = $derived(endpoint === 'sample' || grain === 'vehicle' ? VEHICLE_DIMENSIONS : EVENT_DIMENSIONS)

  function changeEndpoint(next: Endpoint) {
    // dates and years describe the same range; carry it across
    if (next === 'rates') {
      fromYear = yearOf(from) || fromYear
      toYear = yearOf(to) || toYear
    } else if (endpoint === 'rates') {
      from = `${fromYear}-01-01`
      to = `${toYear}-12-31`
    }
    endpoint = next
    dropUnavailable()
  }

  function dropUnavailable() {
    // sample rows are vehicle rows, so every dimension is available there
    const allowed = endpoint === 'sample' || grain === 'vehicle' ? VEHICLE_DIMENSIONS : EVENT_DIMENSIONS
    groupBy = groupBy.filter((d) => allowed.includes(d))
    filterRows = filterRows.filter((r) => allowed.includes(r.dimension))
  }

  function toggle(list: string[], value: string, on: boolean): string[] {
    return on ? [...list.filter((v) => v !== value), value] : list.filter((v) => v !== value)
  }

  function vocabValues(dimension: string): { code: string; label: string }[] | undefined {
    const attr = VOCAB_ATTRIBUTE[dimension]
    return attr ? vocabulary[attr] : undefined
  }

  function addFilter() {
    filterRows.push({ id: nextId++, dimension: filterDimensions[0], values: [], text: '' })
  }

  function changeFilterDimension(row: FilterRow, dimension: string) {
    row.dimension = dimension
    row.values = []
    row.text = ''
  }

  function buildFilters(): Filters {
    const out: Filters = {}
    for (const r of filterRows) {
      const values = vocabValues(r.dimension) ? r.values : r.text.split(',').map((v) => v.trim()).filter((v) => v !== '')
      if (values.length > 0) out[r.dimension] = [...(out[r.dimension] ?? []), ...values]
    }
    return out
  }

  function run(e: Event) {
    e.preventDefault()
    if (endpoint === 'aggregate') {
      onrun({ endpoint, from, to, grain, bucket, groupBy: [...groupBy], filters: buildFilters() })
    } else if (endpoint === 'sample') {
      const rows = Math.min(100000, Math.max(1, Math.trunc(Number(n)) || 1))
      onrun({ endpoint, from, to, n: rows, seed: Math.trunc(Number(seed)) || 0, filters: buildFilters() })
    } else {
      const q: RatesQuery = { endpoint, measure, metric, fromYear: Number(fromYear), toYear: Number(toYear), groupBy: [...rateGroupBy] as RatesQuery['groupBy'] }
      if (exposureSource) q.exposureSource = exposureSource
      onrun(q)
    }
  }
</script>

{#snippet checks(legend: string, options: string[], selected: string[], set: (v: string, on: boolean) => void)}
  <fieldset>
    <legend>{legend}</legend>
    <div class="checks">
      {#each options as d (d)}
        <label class="inline"><input type="checkbox" checked={selected.includes(d)} onchange={(e) => set(d, e.currentTarget.checked)} /> {d}</label>
      {/each}
    </div>
  </fieldset>
{/snippet}

{#snippet filters()}
  <fieldset>
    <legend>Filters</legend>
    {#each filterRows as row (row.id)}
      <div class="filter">
        <label>
          Filter dimension
          <select value={row.dimension} onchange={(e) => changeFilterDimension(row, e.currentTarget.value)}>
            {#each filterDimensions as d (d)}
              <option value={d}>{d}</option>
            {/each}
          </select>
        </label>
        {#if vocabValues(row.dimension)}
          <div class="checks" role="group" aria-label="Filter values">
            {#each vocabValues(row.dimension) ?? [] as v (v.code)}
              <label class="inline"><input type="checkbox" checked={row.values.includes(v.code)} onchange={(e) => (row.values = toggle(row.values, v.code, e.currentTarget.checked))} /> {v.code}</label>
            {/each}
          </div>
        {:else}
          <label>
            Filter values
            <input type="text" placeholder="comma-separated" bind:value={row.text} />
          </label>
        {/if}
        <button type="button" onclick={() => (filterRows = filterRows.filter((r) => r.id !== row.id))}>Remove filter</button>
      </div>
    {/each}
    <button type="button" onclick={addFilter}>Add filter</button>
  </fieldset>
{/snippet}

<form class="query-panel" onsubmit={run}>
  <h2>Query</h2>
  <label>
    Endpoint
    <select value={endpoint} onchange={(e) => changeEndpoint(e.currentTarget.value as Endpoint)}>
      <option value="aggregate">aggregate</option>
      <option value="rates">rates</option>
      <option value="sample">sample</option>
    </select>
  </label>

  {#if endpoint === 'rates'}
    <label>
      Measure
      <select bind:value={measure}>
        <option value="occupant_fatalities">occupant_fatalities</option>
        <option value="societal_fatalities">societal_fatalities</option>
      </select>
    </label>
    <label>
      Metric
      <select bind:value={metric}>
        <option value="passenger_miles">passenger_miles</option>
        <option value="vehicle_miles">vehicle_miles</option>
        <option value="flight_hours">flight_hours</option>
      </select>
    </label>
    <div class="row">
      <label>From year <input type="number" bind:value={fromYear} /></label>
      <label>To year <input type="number" bind:value={toYear} /></label>
    </div>
    {@render checks('Group by', RATE_DIMENSIONS, rateGroupBy, (v, on) => (rateGroupBy = toggle(rateGroupBy, v, on)))}
    <label>
      Exposure source
      <select bind:value={exposureSource}>
        <option value="">(server default)</option>
        {#each exposureSources as s (s.sourceId)}
          <option value={s.sourceId}>{s.sourceId}</option>
        {/each}
      </select>
    </label>
  {:else}
    <div class="row">
      <label>From <input type="date" bind:value={from} /></label>
      <label>To <input type="date" bind:value={to} /></label>
    </div>
    {#if endpoint === 'aggregate'}
      <div class="row">
        <label>
          Grain
          <select value={grain} onchange={(e) => { grain = e.currentTarget.value as typeof grain; dropUnavailable() }}>
            <option value="event">event</option>
            <option value="vehicle">vehicle</option>
          </select>
        </label>
        <label>
          Bucket
          <select bind:value={bucket}>
            <option value="none">none</option>
            <option value="year">year</option>
            <option value="quarter">quarter</option>
            <option value="month">month</option>
          </select>
        </label>
      </div>
      {@render checks('Group by', aggregateDimensions, groupBy, (v, on) => (groupBy = toggle(groupBy, v, on)))}
    {:else}
      <div class="row">
        <label>Rows (n) <input type="number" min="1" max="100000" bind:value={n} /></label>
        <label>Seed <input type="number" bind:value={seed} /></label>
      </div>
    {/if}
    {@render filters()}
  {/if}

  <button type="submit" class="run" disabled={busy}>Run</button>
</form>

<style>
  .query-panel {
    display: flex;
    flex-direction: column;
    gap: 0.75rem;
  }
  h2 {
    margin: 0;
    font-size: 1.1rem;
  }
  label {
    display: flex;
    flex-direction: column;
    gap: 0.2rem;
    font-size: 0.9rem;
  }
  label.inline {
    flex-direction: row;
    align-items: center;
    gap: 0.3rem;
  }
  .row {
    display: flex;
    gap: 0.75rem;
    flex-wrap: wrap;
  }
  .row > label {
    flex: 1 1 8rem;
  }
  fieldset {
    border: 1px solid #ccc;
    padding: 0.5rem;
    min-width: 0;
  }
  .checks {
    display: flex;
    flex-wrap: wrap;
    gap: 0.25rem 0.9rem;
  }
  .filter {
    display: flex;
    flex-direction: column;
    gap: 0.4rem;
    margin-bottom: 0.6rem;
  }
  .run {
    align-self: flex-start;
    padding: 0.4rem 1.2rem;
  }
</style>
