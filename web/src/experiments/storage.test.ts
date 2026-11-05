import { describe, expect, it } from 'vitest'
import { deleteExperiment, loadExperiments, saveExperiment, STORAGE_KEY, type Experiment } from './storage'

class MemoryStorage implements Storage {
  private map = new Map<string, string>()
  get length() { return this.map.size }
  clear() { this.map.clear() }
  getItem(k: string) { return this.map.get(k) ?? null }
  key(i: number) { return [...this.map.keys()][i] ?? null }
  removeItem(k: string) { this.map.delete(k) }
  setItem(k: string, v: string) { this.map.set(k, v) }
}
const throwing = (): Storage => {
  const boom = () => { throw new Error('denied') }
  return { getItem: boom, setItem: boom, removeItem: boom, clear: boom, key: boom, length: 0 } as unknown as Storage
}
const exp = (name: string, methodId = 'rate'): Experiment => ({
  version: 1, name, methodId, bindings: {}, params: {},
  query: { endpoint: 'rates', measure: 'occupant_fatalities', metric: 'passenger_miles', fromYear: 2019, toYear: 2023, groupBy: [] },
})

describe('experiment storage', () => {
  it('loadExperiments_corruptJson_returnsEmpty', () => {
    const s = new MemoryStorage()
    s.setItem(STORAGE_KEY, '{not json')
    expect(loadExperiments(s)).toEqual([])
    s.setItem(STORAGE_KEY, '{"a":1}')
    expect(loadExperiments(s)).toEqual([])
  })

  it('loadExperiments_dropsEntriesWithoutVersion1', () => {
    const s = new MemoryStorage()
    s.setItem(STORAGE_KEY, JSON.stringify([exp('a'), { ...exp('b'), version: 2 }, { name: 'c' }, null]))
    expect(loadExperiments(s).map((e) => e.name)).toEqual(['a'])
  })

  it('loadExperiments_throwingStorage_returnsEmpty', () => {
    expect(loadExperiments(throwing())).toEqual([])
  })

  it('saveExperiment_replacesSameName', () => {
    const s = new MemoryStorage()
    expect(saveExperiment(exp('a', 'rate'), s)).toBe(true)
    expect(saveExperiment(exp('b'), s)).toBe(true)
    expect(saveExperiment(exp('a', 'heatmap'), s)).toBe(true)
    const list = loadExperiments(s)
    expect(list.map((e) => e.name)).toEqual(['a', 'b'])
    expect(list[0].methodId).toBe('heatmap')
  })

  it('deleteExperiment_removes', () => {
    const s = new MemoryStorage()
    saveExperiment(exp('a'), s)
    saveExperiment(exp('b'), s)
    expect(deleteExperiment('a', s)).toBe(true)
    expect(loadExperiments(s).map((e) => e.name)).toEqual(['b'])
  })

  it('saveExperiment_throwingStorage_returnsFalse', () => {
    expect(saveExperiment(exp('a'), throwing())).toBe(false)
    expect(deleteExperiment('a', throwing())).toBe(false)
  })
})
