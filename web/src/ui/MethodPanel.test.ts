import { fireEvent, render, screen, within } from '@testing-library/svelte'
import { describe, expect, it, vi } from 'vitest'
import { columnType } from '../api/columns'
import { defaultParams, type Bindings } from '../stats/method'
import { rate } from '../stats/rate'
import { fromRows } from '../stats/table'
import MethodPanel from './MethodPanel.svelte'

const cols = ['mode', 'year', 'vehicle_class', 'regulation_class', 'numerator', 'denominator', 'join_level', 'exposure_source', 'provenance']
const table = fromRows(cols, [], Object.fromEntries(cols.map((c) => [c, columnType(c)])))
const good: Bindings = { numerator: 'numerator', denominator: 'denominator' }

function setup(bindings = good, onchange = vi.fn()) {
  render(MethodPanel, { table, methodId: 'rate', bindings, params: defaultParams(rate), onchange })
  return onchange
}

describe('MethodPanel', () => {
  it('MethodPanel_offersOnlyMatchingColumnTypes', () => {
    setup({})
    const optionsOf = (el: HTMLElement) => within(el).getAllByRole('option').map((o) => o.textContent)
    const numerator = optionsOf(screen.getByLabelText('Events (numerator)'))
    expect(numerator).toContain('numerator')
    expect(numerator).not.toContain('mode')
    expect(numerator).not.toContain('denominator')
    const groups = within(screen.getByRole('group', { name: 'Group by' })).getAllByRole('checkbox')
      .map((c) => (c as HTMLInputElement).parentElement!.textContent!.trim())
    expect(groups).toEqual(['mode', 'year', 'vehicle_class', 'regulation_class', 'join_level', 'exposure_source', 'provenance'])
  })

  it('MethodPanel_staleBinding_disablesRun', () => {
    setup({ numerator: 'gone', denominator: 'denominator' })
    expect(screen.getByText('Events (numerator): column "gone" is not in the table')).toBeTruthy()
    expect((screen.getByRole('button', { name: 'Run method' }) as HTMLButtonElement).disabled).toBe(true)
  })

  it('MethodPanel_validBindings_enablesRun', () => {
    setup()
    expect((screen.getByRole('button', { name: 'Run method' }) as HTMLButtonElement).disabled).toBe(false)
  })

  it('MethodPanel_changingMethod_resetsBindingsAndParams', async () => {
    const onchange = setup()
    await fireEvent.change(screen.getByLabelText('Method'), { target: { value: 'crosstab' } })
    expect(onchange).toHaveBeenCalledWith({ methodId: 'crosstab', bindings: {}, params: { normalize: 'none', margins: true } })
  })

  it('MethodPanel_binding_emitsChange', async () => {
    const onchange = setup({})
    await fireEvent.change(screen.getByLabelText('Events (numerator)'), { target: { value: 'numerator' } })
    expect(onchange).toHaveBeenCalledWith(expect.objectContaining({ bindings: { numerator: 'numerator' } }))
    await fireEvent.click(within(screen.getByRole('group', { name: 'Group by' })).getByLabelText('mode'))
    expect(onchange).toHaveBeenLastCalledWith(expect.objectContaining({ bindings: { groups: ['mode'] } }))
  })
})
