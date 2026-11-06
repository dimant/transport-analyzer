<script lang="ts">
  import type { Glossary } from '../api/glossary'
  import { formatValue } from '../render/format'
  import type { Cell, TableSpec } from '../stats/method'

  let { spec, glossary }: { spec: TableSpec; glossary?: Glossary } = $props()

  // a row's ratio columns (an estimate and its interval) get enough digits to read differently from each other
  const ratioKeys = $derived(spec.columns.filter((c) => c.format === 'ratio').map((c) => c.key))
  const show = (v: Cell, format: TableSpec['columns'][number]['format'], row: Record<string, Cell>) =>
    v === null && format === 'text' ? '(none)' : formatValue(v, format, ratioKeys.map((k) => row[k]))
  const describe = (key: string, v: Cell) => (typeof v === 'string' ? glossary?.describe(key, v) : undefined)
</script>

<table>
  <thead>
    <tr>
      {#each spec.columns as c (c.key)}
        <th title={glossary?.help(c.key)} class:num={c.format !== 'text'}>{c.label}</th>
      {/each}
    </tr>
  </thead>
  <tbody>
    {#each spec.rows as row, i (i)}
      <tr>
        {#each spec.columns as c (c.key)}
          <td title={describe(c.key, row[c.key])} class:num={c.format !== 'text'}>{show(row[c.key], c.format, row)}</td>
        {/each}
      </tr>
    {/each}
  </tbody>
</table>

<style>
  .num {
    text-align: right;
  }
</style>
