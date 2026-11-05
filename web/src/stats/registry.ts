import { crosstab } from './crosstab'
import { heatmap } from './heatmap'
import type { Method } from './method'
import { rate } from './rate'

// heterogeneous list: each method has its own params/result types
// eslint-disable-next-line @typescript-eslint/no-explicit-any
export const methods: Method<any, any>[] = [crosstab, rate, heatmap]

// eslint-disable-next-line @typescript-eslint/no-explicit-any
export function methodById(id: string): Method<any, any> | undefined {
  return methods.find((m) => m.id === id)
}
