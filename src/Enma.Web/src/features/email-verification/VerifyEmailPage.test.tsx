import { StrictMode } from 'react'
import { render, screen } from '@testing-library/react'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createAppRoutes } from '../../app/router'
import { captureEmailVerificationHandoff } from './emailVerificationHandoff'
import { createEmailVerificationFlow } from './emailVerificationService'

const validToken = 'Abcdefghijklmnopqrstuvwxyz0123456789_-ABCDE'

function response(status: number): Response {
  return new Response(null, { status })
}

function renderVerificationPage(fragment = '') {
  window.history.replaceState(null, '', `/verify-email${fragment}`)
  const handoff = captureEmailVerificationHandoff(
    window.location,
    window.history,
  )
  const flow = createEmailVerificationFlow(handoff.token)
  const router = createMemoryRouter(createAppRoutes(flow), {
    initialEntries: ['/verify-email'],
  })

  render(
    <StrictMode>
      <RouterProvider router={router} />
    </StrictMode>,
  )
}

afterEach(() => {
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
  window.history.replaceState(null, '', '/')
})

describe('verify email page', () => {
  it('Render_ValidToken_ScrubsBeforeOneExactPostAndShowsVerified', async () => {
    const events: string[] = []
    const originalReplaceState = window.history.replaceState.bind(window.history)
    vi.spyOn(window.history, 'replaceState').mockImplementation(
      (data, unused, url) => {
        events.push('scrub')
        originalReplaceState(data, unused, url)
      },
    )
    const fetchMock = vi.fn(() => {
      events.push('fetch')
      return Promise.resolve(response(204))
    })
    vi.stubGlobal('fetch', fetchMock)

    renderVerificationPage(`#token=${validToken}`)

    expect(events.slice(-2)).toEqual(['scrub', 'fetch'])
    expect(window.location.hash).toBe('')
    expect(fetchMock).toHaveBeenCalledTimes(1)
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/auth/email-verification/verify',
      {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ token: validToken }),
        cache: 'no-store',
      },
    )
    expect(
      await screen.findByRole('heading', { name: 'E-mail verificado' }),
    ).toBeInTheDocument()
  })

  it('Render_MissingToken_DoesNotPostAndShowsInvalid', () => {
    const fetchMock = vi.fn()
    vi.stubGlobal('fetch', fetchMock)

    renderVerificationPage()

    expect(fetchMock).not.toHaveBeenCalled()
    expect(
      screen.getByRole('heading', { name: 'Link inválido ou expirado' }),
    ).toBeInTheDocument()
  })

  it('Render_MalformedToken_ScrubsWithoutPostAndShowsInvalid', () => {
    const fetchMock = vi.fn()
    vi.stubGlobal('fetch', fetchMock)

    renderVerificationPage('#token=abc')

    expect(window.location.hash).toBe('')
    expect(fetchMock).not.toHaveBeenCalled()
    expect(
      screen.getByRole('heading', { name: 'Link inválido ou expirado' }),
    ).toBeInTheDocument()
  })

  it('Render_InvalidResponse_ShowsGenericInvalidWithoutToken', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(response(400))))

    renderVerificationPage(`#token=${validToken}`)

    expect(
      await screen.findByRole('heading', { name: 'Link inválido ou expirado' }),
    ).toBeInTheDocument()
    expect(document.body).not.toHaveTextContent(validToken)
  })

  it('Render_RateLimitedResponse_ShowsTryLaterWithoutToken', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(response(429))))

    renderVerificationPage(`#token=${validToken}`)

    expect(
      await screen.findByRole('heading', { name: 'Muitas tentativas' }),
    ).toBeInTheDocument()
    expect(document.body).not.toHaveTextContent(validToken)
  })

  it.each([
    ['server failure', () => Promise.resolve(response(500))],
    ['network failure', () => Promise.reject(new Error('Network failure'))],
  ])(
    'Render_%s_ShowsTemporaryFailureWithoutToken',
    async (_scenario, fetchResult) => {
      vi.stubGlobal('fetch', vi.fn(fetchResult))

      renderVerificationPage(`#token=${validToken}`)

      expect(
        await screen.findByRole('heading', {
          name: 'Não foi possível verificar seu e-mail',
        }),
      ).toBeInTheDocument()
      expect(document.body).not.toHaveTextContent(validToken)
    },
  )
})
