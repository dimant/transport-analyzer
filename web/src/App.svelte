<script lang="ts">
  import { onMount } from 'svelte'
  import { getSources, getVocabulary, runQuery } from './api/client'
  import { buildGlossary } from './api/glossary'
  import type { Query, QueryResult, Source, Vocabulary } from './api/types'
  import { methodById, methods } from './stats/registry'
  import { defaultParams, type Bindings } from './stats/method'
  import type { Experiment } from './experiments/storage'
  import DataPreview from './ui/DataPreview.svelte'
  import ExperimentBar from './ui/ExperimentBar.svelte'
  import MethodPanel, { type MethodChange } from './ui/MethodPanel.svelte'
  import QueryPanel from './ui/QueryPanel.svelte'
  import ResultPanel, { type MethodRun } from './ui/ResultPanel.svelte'

  interface Api {
    getSources: () => Promise<Source[]>
    getVocabulary: () => Promise<Vocabulary>
    runQuery: (q: Query) => Promise<QueryResult>
  }

  let { api = { getSources, getVocabulary, runQuery } }: { api?: Api } = $props()

  // the first-question query: occupant deaths per passenger-mile by regulation class
  let query = $state<Query>({
    endpoint: 'rates', measure: 'occupant_fatalities', metric: 'passenger_miles', fromYear: 2019, toYear: 2023, groupBy: ['regulation_class'],
  })
  let result = $state.raw<QueryResult | null>(null)
  let errors = $state<string[]>([])
  let running = $state(false)
  let methodId = $state('rate')
  let bindings = $state.raw<Bindings>({})
  let params = $state.raw<Record<string, unknown>>(defaultParams(methodById('rate')!))
  let methodRun = $state.raw<MethodRun | null>(null)
  let note = $state<string | undefined>(undefined)
  let methodWarning = $state<string | undefined>(undefined)
  let loadId = $state(0)
  let token = 0
  let sources = $state<Source[]>([])
  let vocabulary = $state<Vocabulary>({})

  const glossary = $derived(buildGlossary(vocabulary, sources))
  const message = (e: unknown) => (e instanceof Error ? e.message : String(e))

  onMount(() => {
    api.getSources().then((s) => (sources = s), (e) => errors.push(`Could not load sources: ${message(e)}`))
    api.getVocabulary().then((v) => (vocabulary = v), (e) => errors.push(`Could not load vocabulary: ${message(e)}`))
  })

  // resolves true when this query's result was applied; false on failure or when a newer run superseded it
  async function run(q: Query): Promise<boolean> {
    const mine = ++token
    query = q
    running = true
    errors = errors.filter((e) => e.startsWith('Could not load'))
    try {
      const r = await api.runQuery(q)
      if (mine !== token) return false
      result = r
      methodRun = null
      if (Object.keys(bindings).length === 0) bindings = methodById(methodId)?.defaultBindings?.(r.table) ?? {}
      return true
    } catch (e) {
      if (mine !== token) return false
      errors.push(message(e))
      return false
    } finally {
      if (mine === token) running = false
    }
  }

  function runMethod() {
    const method = methodById(methodId)
    if (result && method) methodRun = { method, table: result.table, bindings, params }
  }

  function runManual(q: Query) {
    note = undefined
    return run(q)
  }

  function changeMethod(c: MethodChange) {
    if (c.methodId !== methodId) {
      methodRun = null
      note = undefined
    }
    methodWarning = undefined
    methodId = c.methodId
    bindings = c.bindings
    params = c.params
  }

  async function loadExperiment(e: Experiment) {
    query = e.query
    loadId++
    if (!(await run(e.query))) return
    note = e.note
    const known = methodById(e.methodId)
    const method = known ?? methods[0]
    methodWarning = known ? undefined : `Unknown method "${e.methodId}"; using ${method.name}.`
    methodId = method.id
    if (!known) {
      // the stored bindings belong to a method we do not have: start the fallback from its defaults, without running it
      bindings = method.defaultBindings?.(result!.table) ?? {}
      params = defaultParams(method)
      return
    }
    bindings = e.bindings
    params = e.params
    runMethod()
  }

  const current = $derived<Experiment>({ version: 1, name: '', ...(note ? { note } : {}), query, methodId, bindings, params })
</script>

<main id="app-root">
  <header>
    <h1>Transport Analyzer</h1>
    {#if sources.length > 0}
      <ul class="sources">
        {#each sources as s (s.sourceId)}
          <li>{s.name} — data current through {s.dataCurrentThrough ?? 'unknown'}</li>
        {/each}
      </ul>
    {/if}
  </header>

  {#each errors as e, i (i)}
    <div class="banner" role="alert">{e}</div>
  {/each}

  <div class="layout">
    <div class="left">
      {#key loadId}
        <QueryPanel {query} {vocabulary} {sources} busy={running} onrun={runManual} />
      {/key}
      {#if running}<p>Running…</p>{/if}
      {#if result}<DataPreview {result} {glossary} />{/if}
    </div>
    <section class="right" aria-label="Analysis">
      <ExperimentBar {current} onload={loadExperiment} />
      {#if result}
        <MethodPanel table={result.table} {methodId} {bindings} {params} warning={methodWarning} onchange={changeMethod} onrun={runMethod} />
        <ResultPanel run={methodRun} {note} {glossary} />
      {/if}
    </section>
  </div>
</main>
