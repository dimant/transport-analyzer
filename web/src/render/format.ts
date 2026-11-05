import type { Cell, TableSpec } from '../stats/method'

type Format = TableSpec['columns'][number]['format']

const GROUPED = (maxFrac: number) => new Intl.NumberFormat('en-US', { maximumFractionDigits: maxFrac })
const INT = GROUPED(0)
const ONE = GROUPED(1)
const RATE = GROUPED(20)

export function formatValue(v: Cell, format: Format): string {
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
      return `${ONE.format(n)}%`
    case 'rate':
      if (n === 0) return '0'
      if (Math.abs(n) < 1e-6) return n.toExponential(2)
      return RATE.format(Number(n.toPrecision(3)))
  }
}
