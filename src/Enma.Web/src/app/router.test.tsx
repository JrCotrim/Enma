import { render, screen } from '@testing-library/react'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { describe, expect, it } from 'vitest'
import { appRoutes } from './router'

function renderRoute(path: string) {
  const testRouter = createMemoryRouter(appRoutes, {
    initialEntries: [path],
  })

  render(<RouterProvider router={testRouter} />)
}

describe('application router', () => {
  it('Render_HomeRoute_ShowsApplicationShell', async () => {
    renderRoute('/')

    expect(
      await screen.findByRole('heading', { name: 'Welcome to ENMA' }),
    ).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'ENMA home' })).toHaveAttribute(
      'href',
      '/',
    )
  })

  it('Render_UnknownRoute_ShowsNotFoundPage', async () => {
    renderRoute('/missing-page')

    expect(
      await screen.findByRole('heading', { name: 'Page not found' }),
    ).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Return home' })).toHaveAttribute(
      'href',
      '/',
    )
  })
})
