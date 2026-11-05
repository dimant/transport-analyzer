import { render, screen } from '@testing-library/svelte'
import { expect, test } from 'vitest'
import App from './App.svelte'

test('App_rendersTitle', () => {
  render(App)
  expect(screen.getByRole('heading', { name: 'Transport Analyzer' })).toBeTruthy()
})
