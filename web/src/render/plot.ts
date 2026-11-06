import * as Plot from '@observablehq/plot'
import type { Cell, ForestSpec, HeatmapSpec, IntervalSpec } from '../stats/method'
import { formatValue } from './format'

type Row = Record<string, Cell>

const num = (c: Cell | undefined): number | null => (typeof c === 'number' && Number.isFinite(c) ? c : null)
const label = (c: Cell | undefined) => (c === null || c === undefined ? '(none)' : String(c))

function wrap(plot: Element | SVGSVGElement | HTMLElement, notes: string[]): Element {
  const fig = plot.tagName.toLowerCase() === 'figure' ? plot : document.createElement('figure')
  if (fig !== plot) fig.appendChild(plot)
  if (notes.length) {
    const cap = document.createElement('figcaption')
    cap.textContent = notes.join('; ')
    fig.appendChild(cap)
  }
  return fig
}

export function renderPlot(spec: HeatmapSpec | IntervalSpec | ForestSpec, opts: { width: number }): Element {
  if (spec.kind === 'heatmap') return heatmap(spec, opts.width)
  return spec.kind === 'forest' ? forest(spec, opts.width) : intervals(spec, opts.width)
}

function heatmap(spec: HeatmapSpec, width: number): Element {
  const val = (d: Row) => num(d[spec.fill])
  const usable = (d: Row) => {
    const v = val(d)
    return v !== null && (!spec.logScale || v > 0)
  }
  const text = (d: Row) => formatValue(val(d), spec.format)
  const title = (d: Row) => {
    const head = `${label(d[spec.x])} / ${label(d[spec.y])}: ${text(d)}`
    const lo = num(d.lo)
    const hi = num(d.hi)
    return spec.format === 'rate' && lo !== null && hi !== null ? `${head} [${formatValue(lo, 'rate')}, ${formatValue(hi, 'rate')}]` : head
  }
  const usableValues = spec.data.filter(usable).map((d) => val(d)!)
  const scaled = (v: number) => (spec.logScale ? Math.log(v) : v)
  const lo = usableValues.length ? scaled(Math.min(...usableValues)) : 0
  const hi = usableValues.length ? scaled(Math.max(...usableValues)) : 0
  // the blues ramp darkens with value: light text on the upper part, dark text elsewhere
  const textFill = (d: Row) => {
    const v = val(d)
    if (v === null || !usable(d) || hi === lo) return 'black'
    return (scaled(v) - lo) / (hi - lo) > 0.5 ? 'white' : 'black'
  }
  const build = (legend: boolean) => Plot.plot({
    width,
    marginLeft: 100,
    x: { type: 'band', label: spec.x },
    y: { type: 'band', label: spec.y },
    color: { type: spec.logScale ? 'log' : 'linear', legend, label: spec.fillLabel, scheme: 'blues' },
    marks: [
      Plot.cell(spec.data.filter((d) => !usable(d)), { x: spec.x, y: spec.y, fill: '#d0d0d0', title }),
      Plot.cell(spec.data.filter(usable), { x: spec.x, y: spec.y, fill: (d: Row) => val(d), title }),
      Plot.text(spec.data, { x: spec.x, y: spec.y, text, title, fontSize: 11, fill: textFill }),
    ],
  })
  let plot: Element
  try {
    plot = build(true)
  } catch {
    // the ramp legend is drawn on a canvas; environments without one (jsdom) get the plot without it
    plot = build(false)
  }
  return wrap(plot, spec.caption ? [spec.caption] : [])
}

function intervals(spec: IntervalSpec, width: number): Element {
  const notes: string[] = []
  const rows = spec.data.filter((d) => num(d[spec.y]) !== null)
  const missing = spec.data.length - rows.length
  if (missing > 0) notes.push(`${missing} ${missing === 1 ? 'row' : 'rows'} without a rate (no exposure)`)

  const positives: number[] = []
  for (const d of rows) for (const k of [spec.lo, spec.y, spec.hi]) {
    const v = num(d[k])
    if (v !== null && v > 0) positives.push(v)
  }
  const floor = positives.length ? Math.min(...positives) / 10 : 0.1
  const clampOn = spec.logScale
  const clamp = (v: number | null) => (v === null ? null : clampOn && v <= 0 ? floor : v)
  const isClamped = (d: Row) => clampOn && [spec.lo, spec.y, spec.hi].some((k) => {
    const v = num(d[k])
    return v !== null && v <= 0
  })
  if (rows.some(isClamped)) notes.push('values of 0 drawn at the floor')

  const title = (d: Row) =>
    `${label(d[spec.x])}${spec.series ? ` · ${label(d[spec.series])}` : ''}\n` +
    `${spec.yLabel}: ${formatValue(num(d[spec.y]), 'rate')} (${formatValue(num(d[spec.lo]), 'rate')} – ${formatValue(num(d[spec.hi]), 'rate')})`
  const color = spec.series ? (d: Row) => label(d[spec.series!]) : 'steelblue'
  const x = (d: Row) => label(d[spec.x])
  const y = (d: Row) => clamp(num(d[spec.y]))
  const solid = rows.filter((d) => !(num(d[spec.y])! <= 0 && clampOn))
  const hollow = rows.filter((d) => num(d[spec.y])! <= 0 && clampOn)

  const plot = Plot.plot({
    width,
    x: { type: 'point', label: spec.x },
    y: {
      type: spec.logScale ? 'log' : 'linear',
      label: spec.yLabel,
      grid: true,
      ticks: 6,
      tickFormat: (v: number) => formatValue(v, 'rate'),
    },
    color: { legend: Boolean(spec.series) },
    marks: [
      Plot.ruleX(rows, { x, y1: (d: Row) => clamp(num(d[spec.lo])), y2: (d: Row) => clamp(num(d[spec.hi])), stroke: color, title }),
      Plot.dot(solid, { x, y, fill: color, stroke: color, title }),
      Plot.dot(hollow, { x, y, fill: 'none', stroke: color, title }),
    ],
  })
  return wrap(plot, notes)
}

function forest(spec: ForestSpec, width: number): Element {
  const notes: string[] = []
  if (spec.caption) notes.push(spec.caption)
  const rows: Row[] = []
  const skipped: Row[] = []
  for (const d of spec.data) {
    const v = num(d[spec.estimate])
    ;(v !== null && v > 0 ? rows : skipped).push(d)
  }
  if (skipped.length) notes.push(`not estimated: ${skipped.map((d) => label(d[spec.label])).join(', ')}`)

  const est = (d: Row) => num(d[spec.estimate])
  const y = (d: Row) => label(d[spec.label])
  // an interval needs both ends; a row with only an estimate keeps its dot
  const hasInterval = (d: Row) => (num(d[spec.lo]) ?? 0) > 0 && (num(d[spec.hi]) ?? 0) > 0
  const ruled = rows.filter(hasInterval)
  const unbounded = rows.filter((d) => !hasInterval(d))
  if (unbounded.length) notes.push(`no interval: ${unbounded.map(y).join(', ')}`)
  const title = (d: Row) => {
    if (!hasInterval(d)) return `${y(d)}: ${formatValue(est(d), 'ratio')} (no interval)`
    const peers = [est(d), num(d[spec.lo]), num(d[spec.hi])]
    const [e, lo, hi] = peers.map((v) => formatValue(v, 'ratio', peers))
    return `${y(d)}: ${e} (${lo} – ${hi})`
  }
  // tick labels get the digits that tell apart the ticks of a linear scale over the plotted extent: a log axis spanning
  // less than a decade uses those ticks, and wider ones read apart at 3 figures anyway
  const extent = [1, ...rows.map(est), ...ruled.flatMap((d) => [num(d[spec.lo]), num(d[spec.hi])])] as number[]
  const tickPeers = linearTicks(Math.min(...extent), Math.max(...extent), 6)
  // room for the longest label, capped at half the plot; longer labels are cut with an ellipsis
  const maxLen = Math.max(0, ...rows.map((d) => y(d).length))
  const marginLeft = Math.max(120, Math.min(0.5 * width, 7 * maxLen + 16))
  const fit = Math.max(1, Math.floor((marginLeft - 16) / 7))
  const shorten = (t: string) => (t.length > fit ? `${t.slice(0, fit - 1)}…` : t)

  const plot = Plot.plot({
    width,
    marginLeft,
    x: { type: 'log', label: spec.xLabel, grid: true, ticks: 6, tickFormat: (v: number) => formatValue(v, 'ratio', tickPeers) },
    y: { type: 'band', label: null, domain: rows.map(y), tickFormat: shorten },
    marks: [
      Plot.ruleX([1], { strokeDasharray: '4 3', stroke: 'gray' }),
      Plot.ruleY(ruled, { y, x1: (d: Row) => num(d[spec.lo]), x2: (d: Row) => num(d[spec.hi]), stroke: 'steelblue', title }),
      Plot.dot(rows, { x: est, y, fill: 'steelblue', stroke: 'steelblue', title }),
    ],
  })
  return wrap(plot, notes)
}

/** The ticks of a linear scale over [lo, hi], at the 1, 2 or 5 × 10^k step D3 picks for about `count` ticks. */
function linearTicks(lo: number, hi: number, count: number): number[] {
  if (!(hi > lo)) return [lo]
  const raw = (hi - lo) / count
  const power = Math.floor(Math.log10(raw))
  const error = raw / 10 ** power
  const step = (error >= Math.sqrt(50) ? 10 : error >= Math.sqrt(10) ? 5 : error >= Math.sqrt(2) ? 2 : 1) * 10 ** power
  const ticks: number[] = []
  for (let i = Math.ceil(lo / step); i * step <= hi; i++) ticks.push(Number((i * step).toPrecision(12)))
  return ticks
}
