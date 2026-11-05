<script lang="ts">
  import { PRESETS } from '../experiments/presets'
  import { deleteExperiment, experimentToJson, loadExperiments, saveExperiment, type Experiment } from '../experiments/storage'

  let { current, onload }: { current: Experiment; onload: (e: Experiment) => void } = $props()

  let saved = $state<Experiment[]>(loadExperiments())
  let name = $state('')
  let status = $state('')
  const chosen = $state({ preset: '', saved: '' })

  function loadFrom(list: Experiment[], which: 'preset' | 'saved', value: string) {
    chosen[which] = value
    const e = list.find((x) => x.name === value)
    if (e) {
      name = e.name
      onload(e)
    }
  }

  function save() {
    const n = name.trim()
    if (!n) {
      status = 'Enter a name to save.'
      return
    }
    const ok = saveExperiment({ ...current, name: n })
    saved = loadExperiments()
    chosen.saved = n
    status = ok ? `Saved "${n}".` : 'Could not save: browser storage is unavailable.'
  }

  function remove() {
    if (!chosen.saved) return
    const ok = deleteExperiment(chosen.saved)
    saved = loadExperiments()
    chosen.saved = ''
    status = ok ? 'Deleted.' : 'Could not delete: browser storage is unavailable.'
  }

  function exportJson() {
    const n = name.trim() || current.name || 'experiment'
    const blob = new Blob([experimentToJson({ ...current, name: n })], { type: 'application/json' })
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = `${n}.json`
    document.body.appendChild(a)
    a.click()
    a.remove()
    URL.revokeObjectURL(url)
  }
</script>

<section class="experiment-bar">
  <h2>Experiments</h2>
  <label>
    Preset
    <select value={chosen.preset} onchange={(e) => loadFrom(PRESETS, 'preset', e.currentTarget.value)}>
      <option value="">—</option>
      {#each PRESETS as p (p.name)}
        <option value={p.name}>{p.name}</option>
      {/each}
    </select>
  </label>
  <label>
    Saved
    <select value={chosen.saved} onchange={(e) => loadFrom(saved, 'saved', e.currentTarget.value)}>
      <option value="">—</option>
      {#each saved as s (s.name)}
        <option value={s.name}>{s.name}</option>
      {/each}
    </select>
  </label>
  <label>
    Name
    <input type="text" bind:value={name} />
  </label>
  <div class="buttons">
    <button type="button" onclick={save}>Save as</button>
    <button type="button" onclick={remove} disabled={!chosen.saved}>Delete</button>
    <button type="button" onclick={exportJson}>Export JSON</button>
  </div>
  {#if status}<p class="status" role="status">{status}</p>{/if}
</section>

<style>
  .experiment-bar {
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
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
  .buttons {
    display: flex;
    gap: 0.5rem;
    flex-wrap: wrap;
  }
  .status {
    margin: 0;
    font-size: 0.85rem;
  }
</style>
