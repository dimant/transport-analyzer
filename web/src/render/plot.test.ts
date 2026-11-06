import { beforeAll, describe, expect, it, vi } from 'vitest'
import type { ForestSpec, HeatmapSpec, IntervalSpec } from '../stats/method'
import { renderPlot } from './plot'

const heat: HeatmapSpec = {
  kind: 'heatmap', x: 'a', y: 'b', fill: 'value', fillLabel: 'count', logScale: true, format: 'integer',
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
  it('intervals_logScale_yTicksArePlainDecimals', () => {
    const el = renderPlot(iv([['2018', 0.0002, 0.0001, 0.0005], ['2019', 0.3, 0.1, 0.6]]), { width: 400 })
    const ticks = [...el.querySelectorAll('[aria-label="y-axis tick label"] text')].map((t) => t.textContent ?? '')
    expect(ticks.length).toBeGreaterThan(0)
    for (const t of ticks) {
      expect(t).not.toMatch(/[mµ]$/)
      expect(Number.isNaN(Number(t.replace(/,/g, '')))).toBe(false)
    }
  })
  it('heatmap_textContrastsWithCell', () => {
    const el = renderPlot(heat, { width: 400 })
    const texts = [...el.querySelectorAll('[aria-label="text"] text')]
    const fillOf = (s: string) => texts.find((t) => t.firstChild?.textContent === s)!.getAttribute('fill') ?? ''
    expect(fillOf('100')).toBe('white')
    expect(fillOf('1')).not.toBe('white')
  })
  it('plots_yearLikeCategories_emitNoWarning', () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {})
    const years = ['2019', '2020', '2021', '2022', '2023']
    const h: HeatmapSpec = {
      ...heat, x: 'year', y: 'b', logScale: false,
      data: years.map((year, i) => ({ year, b: 'u', value: i + 1 })),
    }
    const el = renderPlot(h, { width: 400 })
    const el2 = renderPlot(iv(years.map((y) => [y, 0.5, 0.4, 0.6])), { width: 400 })
    expect(warn).not.toHaveBeenCalled()
    expect(el.textContent).not.toContain('warning')
    expect(el2.textContent).not.toContain('warning')
    warn.mockRestore()
  })
  it('heatmap_formatDrivesTextAndTitle', () => {
    const h = (format: HeatmapSpec['format'], value: number, extra: Record<string, number> = {}): HeatmapSpec => ({
      ...heat, logScale: false, format, data: [{ a: 'p', b: 'u', value, ...extra }],
    })
    const rate = renderPlot({ ...h('rate', 0.000128, { lo: 0.0001, hi: 0.00016 }), data: [{ a: 'p', b: 'u', value: 0.000128, lo: 0.0001, hi: 0.00016 }] }, { width: 400 })
    expect(rate.querySelector('[aria-label="text"] text')!.firstChild!.textContent).toBe('0.000128')
    expect(rate.querySelector('[aria-label="cell"] title, [aria-label="text"] title')!.textContent).toBe('p / u: 0.000128 [0.0001, 0.00016]')
    const pct = renderPlot(h('percent', 0.25), { width: 400 })
    expect(pct.querySelector('[aria-label="text"] text')!.firstChild!.textContent).toBe('25.0%')
  })
  it('heatmap_captionShownInFigcaption', () => {
    const el = renderPlot({ ...heat, caption: 'Exposure: faa-ga-survey · Provenance: survey_estimate' }, { width: 400 })
    expect(el.querySelector('figcaption')!.textContent).toContain('Exposure: faa-ga-survey · Provenance: survey_estimate')
  })
  it('forest_drawsEstimableRowsAndReferenceRule', () => {
    const spec: ForestSpec = {
      kind: 'forest', label: 'term', estimate: 'est', lo: 'lo', hi: 'hi', xLabel: 'rate ratio (95% CI)',
      caption: 'relative to year=2019',
      data: [
        { term: 'a', est: 1.5, lo: 1.1, hi: 2.0 },
        { term: 'b', est: 0.7, lo: null, hi: null },
        { term: 'c', est: null, lo: null, hi: null },
      ],
    }
    const el = renderPlot(spec, { width: 400 })
    expect(el.querySelectorAll('[aria-label="dot"] circle').length).toBe(2)
    expect(el.querySelectorAll('[stroke-dasharray]').length).toBe(1)
    expect(el.textContent).toContain('not estimated: c')
    expect(el.querySelector('figcaption')!.textContent).toContain('relative to year=2019')
    const labels = [...el.querySelectorAll('[aria-label="y-axis tick label"] text')].map((t) => t.textContent)
    expect(labels).toEqual(['a', 'b'])
  })
})
