import type { Cell, TableSpec } from './method'

export type Format = TableSpec['columns'][number]['format']

const GROUPED = (maxFrac: number) => new Intl.NumberFormat('en-US', { maximumFractionDigits: maxFrac })
const INT = GROUPED(0)
const ONE = GROUPED(1)
const SIGNIFICANT = GROUPED(20)
const PERCENT = new Intl.NumberFormat('en-US', { minimumFractionDigits: 1, maximumFractionDigits: 1 })
// p-values below this are past what a double resolves reliably and may have underflowed to 0
const P_FLOOR = 1e-300

function significant(n: number, digits: number): string {
  if (n === 0) return '0'
  if (Math.abs(n) < 1e-6) return n.toExponential(digits - 1)
  return SIGNIFICANT.format(Number(n.toPrecision(digits)))
}

/** The fewest significant figures, from 3 up to 6, at which the numbers among `peers` all read differently; 6 if none. */
export function ratioDigits(peers: Cell[]): number {
  const nums = peers.filter((v): v is number => typeof v === 'number' && Number.isFinite(v))
  for (let digits = 3; digits < 6; digits++) {
    if (new Set(nums.map((n) => significant(n, digits))).size === nums.length) return digits
  }
  return 6
}

/**
 * `peers` matters only for 'ratio': the values that must read differently from `v` (such as a ratio and its interval
 * ends), `v` included. Without it a ratio has 3 significant figures, like 'rate'.
 */
export function formatValue(v: Cell, format: Format, peers?: Cell[]): string {
  if (v === null || (typeof v === 'number' && Number.isNaN(v))) return '—'
  if (format === 'text') return String(v)
  const n = typeof v === 'number' ? v : Number(v)
  if (Number.isNaN(n)) return String(v)
  switch (format) {
    case 'integer':
      return INT.format(n)
    case 'number':
      return ONE.format(n)
    case 'percent':
      return `${PERCENT.format(n * 100)}%`
    case 'rate':
      return significant(n, 3)
    case 'ratio':
      return significant(n, ratioDigits(peers ?? [n]))
    case 'p':
      return n < P_FLOOR ? `< ${P_FLOOR}` : significant(n, 3)
  }
}
