<script lang="ts">
  import { onMount } from 'svelte'
  import { getSources, getVocabulary, runQuery } from './api/client'
  import { buildGlossary } from './api/glossary'
  import type { Query, QueryResult, Source, Vocabulary } from './api/types'
  import DataPreview from './ui/DataPreview.svelte'
  import QueryPanel from './ui/QueryPanel.svelte'

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
  let sources = $state<Source[]>([])
  let vocabulary = $state<Vocabulary>({})

  const glossary = $derived(buildGlossary(vocabulary, sources))
  const message = (e: unknown) => (e instanceof Error ? e.message : String(e))

  onMount(() => {
    api.getSources().then((s) => (sources = s), (e) => errors.push(`Could not load sources: ${message(e)}`))
    api.getVocabulary().then((v) => (vocabulary = v), (e) => errors.push(`Could not load vocabulary: ${message(e)}`))
  })

  async function run(q: Query) {
    query = q
    running = true
    errors = errors.filter((e) => e.startsWith('Could not load'))
    try {
      result = await api.runQuery(q)
    } catch (e) {
      errors.push(message(e))
    } finally {
      running = false
    }
  }
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
      <QueryPanel {query} {vocabulary} {sources} onrun={run} />
      {#if running}<p>Running…</p>{/if}
      {#if result}<DataPreview {result} {glossary} />{/if}
    </div>
    <!-- Task 10: method panel, result panel and saved experiments go here; bind them to result?.table -->
    <section class="right" aria-label="Analysis"></section>
  </div>
</main>
