import { beforeAll, describe, expect, it, vi } from 'vitest'
import type { HeatmapSpec, IntervalSpec } from '../stats/method'
import { renderPlot } from './plot'

const heat: HeatmapSpec = {
  kind: 'heatmap', x: 'a', y: 'b', fill: 'value', fillLabel: 'count', logScale: true,
  data: [
    { a: 'p', b: 'u', value: 1 }, { a: 'q', b: 'u', value: 10 }, { a: 'p', b: 'v', value: 100 },
    { a: 'q', b: 'v', value: null }, { a: 'p', b: 'w', value: 5 }, { a: 'q', b: 'w', value: 7 },
  ],
}
const iv = (rows: [string, number | null, number | null, number | null][], logScale = true): IntervalSpec => ({
  kind: 'intervals', x: 'year', y: 'rate', lo: 'lo', hi: 'hi', yLabel: 'per', logScale,
  data: rows.map(([year, rate, lo, hi]) => ({ year, rate, lo, hi })),
})

describe('plot', () => {
  beforeAll(() => {
    // jsdom has no canvas; silence its not-implemented log (the ramp legend falls back)
    vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue(null)
  })
  it('heatmap_rendersOneCellPerDatum', () => {
    const el = renderPlot(heat, { width: 400 })
    expect(el.querySelectorAll('[aria-label="cell"] rect').length).toBe(6)
  })
  it('intervals_rendersRuleAndDotPerRow', () => {
    const el = renderPlot(iv([['2018', 0.5, 0.4, 0.6], ['2019', 0.7, 0.5, 0.9]], false), { width: 400 })
    expect(el.querySelectorAll('[aria-label="dot"] circle').length).toBe(2)
    expect(el.querySelectorAll('[aria-label="rule"] line').length).toBe(2)
    expect(el.textContent).not.toContain('drawn at the floor')
  })
  it('intervals_logScale_zeroRate_isClampedNotDropped', () => {
    const el = renderPlot(iv([['2018', 0, 0, 0.3], ['2019', 0.618, 0.5, 0.7]]), { width: 400 })
    expect(el.querySelectorAll('[aria-label="dot"] circle').length).toBe(2)
    expect(el.textContent).toContain('values of 0 drawn at the floor')
    expect(el.querySelectorAll('[aria-label="dot"][fill="none"] circle').length).toBe(1)
  })
  it('intervals_nullRate_notDrawnButNoted', () => {
    const el = renderPlot(iv([['2018', null, null, null], ['2019', 0.6, 0.5, 0.7]]), { width: 400 })
    expect(el.querySelectorAll('[aria-label="dot"] circle').length).toBe(1)
    expect(el.textContent).toContain('1 row without a rate (no exposure)')
  })
})
