import { COLUMN_HELP, VOCAB_ATTRIBUTE } from './columns'
import type { Source, Vocabulary } from './types'

export interface Glossary {
  describe(column: string, value: string | null): string | undefined
  help(column: string): string | undefined
}

export function buildGlossary(vocab: Vocabulary, sources: Source[]): Glossary {
  const bySource = new Map(sources.map((s) => [s.sourceId, s]))
  return {
    describe(column, value) {
      if (value === null) return undefined
      if (column === 'source_id' || column === 'exposure_source') {
        const s = bySource.get(value)
        if (!s) return undefined
        const through = s.dataCurrentThrough ? ` Data current through ${s.dataCurrentThrough}.` : ''
        return `${s.name}. ${s.definitions} Caveats: ${s.caveats}${through}`
      }
      const attr = VOCAB_ATTRIBUTE[column]
      if (!attr) return undefined
      const e = vocab[attr]?.find((x) => x.code === value)
      return e ? `${e.label}: ${e.description}` : undefined
    },
    help(column) {
      return Object.hasOwn(COLUMN_HELP, column) ? COLUMN_HELP[column] : undefined
    },
  }
}
