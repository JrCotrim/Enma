import { render, screen } from '@testing-library/react'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { describe, expect, it } from 'vitest'
import { createEmailVerificationFlow } from '../features/email-verification/emailVerificationService'
import { createAppRoutes } from './router'

function renderRoute(path: string) {
  const testRouter = createMemoryRouter(
    createAppRoutes(createEmailVerificationFlow(undefined)),
    {
    initialEntries: [path],
    },
  )

  render(<RouterProvider router={testRouter} />)
}

describe('application router', () => {
  it('Render_HomeRoute_ShowsApplicationShell', async () => {
    renderRoute('/')

    expect(
      await screen.findByRole('heading', { name: 'Bem-vindo ao ENMA' }),
    ).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Página inicial do ENMA' })).toHaveAttribute(
      'href',
      '/',
    )
  })

  it('Render_UnknownRoute_ShowsNotFoundPage', async () => {
    renderRoute('/missing-page')

    expect(
      await screen.findByRole('heading', { name: 'Página não encontrada' }),
    ).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Voltar ao início' })).toHaveAttribute(
      'href',
      '/',
    )
  })
})
