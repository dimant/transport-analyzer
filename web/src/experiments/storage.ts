import type { Query } from '../api/types'
import type { Bindings } from '../stats/method'

export interface Experiment {
  version: 1
  name: string
  note?: string
  query: Query
  methodId: string
  bindings: Bindings
  params: Record<string, unknown>
}

export const STORAGE_KEY = 'transport-analyzer.experiments'

function defaultStorage(): Storage | undefined {
  try {
    return globalThis.localStorage
  } catch {
    return undefined
  }
}

export function loadExperiments(storage: Storage | undefined = defaultStorage()): Experiment[] {
  try {
    const raw = storage?.getItem(STORAGE_KEY)
    if (!raw) return []
    const parsed: unknown = JSON.parse(raw)
    if (!Array.isArray(parsed)) return []
    return parsed.filter((e): e is Experiment => typeof e === 'object' && e !== null && e.version === 1 && typeof e.name === 'string' && typeof e.query === 'object' && e.query !== null && typeof e.methodId === 'string')
  } catch {
    return []
  }
}

function write(list: Experiment[], storage: Storage | undefined): boolean {
  try {
    if (!storage) return false
    storage.setItem(STORAGE_KEY, JSON.stringify(list))
    return true
  } catch {
    return false
  }
}

export function saveExperiment(e: Experiment, storage: Storage | undefined = defaultStorage()): boolean {
  const list = loadExperiments(storage)
  const i = list.findIndex((x) => x.name === e.name)
  if (i >= 0) list[i] = e
  else list.push(e)
  return write(list, storage)
}

export function deleteExperiment(name: string, storage: Storage | undefined = defaultStorage()): boolean {
  return write(loadExperiments(storage).filter((x) => x.name !== name), storage)
}

export function experimentToJson(e: Experiment): string {
  return JSON.stringify(e, null, 2)
}
