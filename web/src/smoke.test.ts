import { fireEvent, render, screen } from '@testing-library/svelte'
import { expect, test, vi } from 'vitest'
import App from './App.svelte'
import { ApiError } from './api/client'

const idle = {
  getSources: vi.fn().mockResolvedValue([]),
  getVocabulary: vi.fn().mockResolvedValue({}),
  runQuery: vi.fn(),
}

test('App_rendersTitle', () => {
  render(App, { api: idle })
  expect(screen.getByRole('heading', { name: 'Transport Analyzer' })).toBeTruthy()
})

test('App_queryError_showsServerMessage', async () => {
  const api = {
    ...idle,
    runQuery: vi.fn().mockRejectedValue(new ApiError(400, "dimension 'make' needs grain=vehicle", '')),
  }
  render(App, { api })
  await screen.findByRole('button', { name: 'Run' })
  await fireEvent.click(screen.getByRole('button', { name: 'Run' }))
  const alert = await screen.findByRole('alert')
  expect(alert.textContent).toContain("dimension 'make' needs grain=vehicle")
})

test('App_loadError_showsBannerAndStillRenders', async () => {
  const api = { ...idle, getSources: vi.fn().mockRejectedValue(new Error('sources down')) }
  render(App, { api })
  const alert = await screen.findByRole('alert')
  expect(alert.textContent).toContain('sources down')
  expect(screen.getByRole('heading', { name: 'Transport Analyzer' })).toBeTruthy()
})
