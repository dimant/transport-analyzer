<script lang="ts">
  import type { Glossary } from '../api/glossary'
  import { renderPlot } from '../render/plot'
  import type { Bindings, ForestSpec, HeatmapSpec, IntervalSpec, Method, RenderSpec } from '../stats/method'
  import type { Table } from '../stats/table'
  import TableView from './TableView.svelte'

  export interface MethodRun {
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    method: Method<any, any>
    table: Table
    bindings: Bindings
    params: Record<string, unknown>
  }

  let { run, note, glossary }: { run: MethodRun | null; note?: string; glossary?: Glossary } = $props()

  const outcome = $derived.by((): { specs: RenderSpec[]; warnings: string[]; error?: string } | null => {
    if (!run) return null
    try {
      const result = run.method.run(run.table, run.bindings, run.params)
      const w = (result as { warnings?: unknown } | null)?.warnings
      const warnings = Array.isArray(w) ? w.filter((x): x is string => typeof x === 'string') : []
      return { specs: run.method.render(result), warnings }
    } catch (e) {
      return { specs: [], warnings: [], error: e instanceof Error ? e.message : String(e) }
    }
  })

  function plot(node: HTMLElement, spec: HeatmapSpec | IntervalSpec | ForestSpec) {
    let current = spec
    let observer: ResizeObserver | undefined
    let lastWidth = 0
    const draw = () => {
      lastWidth = node.clientWidth || 640
      try {
        node.replaceChildren(renderPlot(current, { width: lastWidth }))
      } catch (e) {
        node.textContent = `Could not draw plot: ${e instanceof Error ? e.message : String(e)}`
      }
    }
    draw()
    if (typeof ResizeObserver !== 'undefined') {
      observer = new ResizeObserver(() => {
        if (node.clientWidth && node.clientWidth !== lastWidth) draw()
      })
      observer.observe(node)
    }
    return {
      update(next: HeatmapSpec | IntervalSpec | ForestSpec) {
        current = next
        draw()
      },
      destroy() {
        observer?.disconnect()
      },
    }
  }
</script>

<section class="result-panel" aria-label="Result">
  {#if note}<p class="note">{note}</p>{/if}
  {#if outcome}
    {#if outcome.error}<div class="banner" role="alert">{outcome.error}</div>{/if}
    {#each outcome.warnings as w, i (i)}
      <p class="warning">{w}</p>
    {/each}
    {#each outcome.specs as spec, i (i)}
      {#if spec.kind === 'table'}
        <div class="scroll"><TableView {spec} {glossary} /></div>
      {:else}
        <div class="plot" use:plot={spec}></div>
      {/if}
    {/each}
  {/if}
</section>

<style>
  .note {
    margin: 0;
    font-size: 0.9rem;
    background: #f4f4f4;
    padding: 0.5rem 0.7rem;
    border-radius: 4px;
  }
  .warning {
    margin: 0;
    font-size: 0.85rem;
    color: #7a5200;
  }
  .result-panel {
    display: flex;
    flex-direction: column;
    gap: 0.75rem;
    min-width: 0;
  }
  .scroll {
    overflow-x: auto;
    max-width: 100%;
  }
  .plot {
    overflow-x: auto;
  }
</style>
