/// <reference types="node" />
import { readdirSync, readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { expect, it } from 'vitest'

it('stats_hasNoFrameworkImports', () => {
  const dir = dirname(fileURLToPath(import.meta.url))
  const files = readdirSync(dir).filter((f) => f.endsWith('.ts') && !f.endsWith('.test.ts'))
  expect(files.length).toBeGreaterThan(0)
  const bad: string[] = []
  const re = /(?:^|\n)\s*(?:import|export)\b[^;'"]*?from\s*['"]([^'"]+)['"]|(?:^|\n)\s*import\s*['"]([^'"]+)['"]/g
  for (const f of files) {
    const src = readFileSync(join(dir, f), 'utf8')
    for (const m of src.matchAll(re)) {
      const spec = m[1] ?? m[2]
      if (!spec.startsWith('./')) bad.push(`${f}: ${spec}`)
    }
  }
  expect(bad).toEqual([])
})
