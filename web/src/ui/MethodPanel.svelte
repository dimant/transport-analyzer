<script lang="ts">
  import { methodById, methods } from '../stats/registry'
  import { defaultParams, validateBindings, type Bindings, type InputSpec } from '../stats/method'
  import type { Table } from '../stats/table'

  export interface MethodChange { methodId: string; bindings: Bindings; params: Record<string, unknown> }

  let { table, methodId, bindings, params, onchange, onrun }: {
    table: Table
    methodId: string
    bindings: Bindings
    params: Record<string, unknown>
    onchange: (c: MethodChange) => void
    onrun?: () => void
  } = $props()

  const method = $derived(methodById(methodId) ?? methods[0])
  const messages = $derived(validateBindings(method, table, bindings))
  const offered = (input: InputSpec) => table.columns.filter((c) => input.types.includes(c.type)).map((c) => c.name)
  const listOf = (v: string | string[] | undefined): string[] => (v === undefined || v === '' ? [] : Array.isArray(v) ? v : [v])

  function changeMethod(id: string) {
    const m = methodById(id)
    if (m) onchange({ methodId: id, bindings: {}, params: defaultParams(m) })
  }
  function bind(key: string, value: string | string[] | undefined) {
    const next: Bindings = { ...bindings }
    if (value === undefined || value === '' || (Array.isArray(value) && value.length === 0)) delete next[key]
    else next[key] = value
    onchange({ methodId: method.id, bindings: next, params })
  }
  function toggle(input: InputSpec, name: string, on: boolean) {
    const cur = listOf(bindings[input.key]).filter((n) => n !== name)
    bind(input.key, on ? [...cur, name] : cur)
  }
  function setParam(key: string, value: unknown) {
    onchange({ methodId: method.id, bindings, params: { ...params, [key]: value } })
  }
</script>

<section class="method-panel">
  <h2>Method</h2>
  <label>
    Method
    <select value={method.id} onchange={(e) => changeMethod(e.currentTarget.value)}>
      {#each methods as m (m.id)}
        <option value={m.id}>{m.name}</option>
      {/each}
    </select>
  </label>
  <p class="desc">{method.description}</p>

  {#each method.inputs as input (input.key)}
    {#if input.multiple}
      <fieldset>
        <legend>{input.label}</legend>
        <div class="checks">
          {#each offered(input) as name (name)}
            <label class="inline">
              <input type="checkbox" checked={listOf(bindings[input.key]).includes(name)} onchange={(e) => toggle(input, name, e.currentTarget.checked)} />
              {name}
            </label>
          {/each}
        </div>
      </fieldset>
    {:else}
      {@const current = typeof bindings[input.key] === 'string' ? (bindings[input.key] as string) : ''}
      <label>
        {input.label}
        <select value={current} onchange={(e) => bind(input.key, e.currentTarget.value)}>
          {#if input.optional || current === '' || !offered(input).includes(current)}
            <option value="">—</option>
          {/if}
          {#each offered(input) as name (name)}
            <option value={name}>{name}</option>
          {/each}
        </select>
      </label>
    {/if}
  {/each}

  {#each method.params as p (p.key)}
    {#if p.kind === 'number'}
      <label>
        {p.label}
        <input type="number" step="any" value={Number(params[p.key] ?? p.default)} onchange={(e) => setParam(p.key, Number(e.currentTarget.value))} />
      </label>
    {:else if p.kind === 'boolean'}
      <label class="inline">
        <input type="checkbox" checked={Boolean(params[p.key] ?? p.default)} onchange={(e) => setParam(p.key, e.currentTarget.checked)} />
        {p.label}
      </label>
    {:else}
      <label>
        {p.label}
        <select value={String(params[p.key] ?? p.default)} onchange={(e) => setParam(p.key, e.currentTarget.value)}>
          {#each p.options as o (o)}
            <option value={o}>{o}</option>
          {/each}
        </select>
      </label>
    {/if}
  {/each}

  {#if messages.length > 0}
    <ul class="messages" role="alert">
      {#each messages as m (m)}
        <li>{m}</li>
      {/each}
    </ul>
  {/if}
  <button type="button" class="run" disabled={messages.length > 0} onclick={() => onrun?.()}>Run method</button>
</section>

<style>
  .method-panel {
    display: flex;
    flex-direction: column;
    gap: 0.6rem;
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
  .desc {
    margin: 0;
    font-size: 0.85rem;
    color: #555;
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
  .messages {
    margin: 0;
    padding-left: 1.2rem;
    color: #7a1610;
    font-size: 0.85rem;
  }
  .run {
    align-self: flex-start;
    padding: 0.4rem 1.2rem;
  }
</style>
