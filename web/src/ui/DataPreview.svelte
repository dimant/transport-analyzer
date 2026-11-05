<script lang="ts">
  import type { Glossary } from '../api/glossary'
  import type { QueryResult } from '../api/types'
  import type { Cell, TableSpec } from '../stats/method'
  import type { Table } from '../stats/table'
  import TableView from './TableView.svelte'

  let { result, glossary }: { result: QueryResult; glossary?: Glossary } = $props()

  const LIMIT = 100

  function format(c: Table['columns'][number]): TableSpec['columns'][number]['format'] {
    if (c.type === 'categorical' || c.name === 'year_built') return 'text'
    return c.type === 'count' || c.type === 'exposure' ? 'integer' : 'number'
  }

  function specOf(t: Table, limit: number): TableSpec {
    const rows: Record<string, Cell>[] = []
    for (let i = 0; i < Math.min(t.length, limit); i++) {
      const row: Record<string, Cell> = {}
      for (const c of t.columns) {
        if (c.type === 'categorical') row[c.name] = c.values[i]
        else row[c.name] = Number.isNaN(c.values[i]) ? null : c.values[i]
      }
      rows.push(row)
    }
    return { kind: 'table', columns: t.columns.map((c) => ({ key: c.name, label: c.name, format: format(c) })), rows }
  }

  const spec = $derived(specOf(result.table, LIMIT))
</script>

<section class="preview">
  <h2>Data</h2>
  <p>{result.table.length.toLocaleString('en-US')} {result.table.length === 1 ? 'row' : 'rows'}{result.table.length > LIMIT ? `, showing the first ${LIMIT}` : ''}</p>
  {#each result.notices as notice, i (i)}
    {#if notice.table}
      <details>
        <summary>{notice.text} ({notice.table.length})</summary>
        <div class="scroll"><TableView spec={specOf(notice.table, notice.table.length)} {glossary} /></div>
      </details>
    {:else}
      <p class="notice">{notice.text}</p>
    {/if}
  {/each}
  <div class="scroll"><TableView {spec} {glossary} /></div>
</section>

<style>
  h2 {
    font-size: 1.1rem;
    margin: 0 0 0.5rem;
  }
  .scroll {
    overflow-x: auto;
    max-width: 100%;
  }
  .notice {
    color: #555;
    font-size: 0.9rem;
  }
  details {
    margin: 0.5rem 0;
  }
</style>
