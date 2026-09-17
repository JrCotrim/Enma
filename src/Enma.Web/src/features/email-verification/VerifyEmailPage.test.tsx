import { StrictMode } from 'react'
import { act, fireEvent, render, screen } from '@testing-library/react'
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
    expect(
      screen.getByRole('button', { name: 'Reenviar e-mail' }),
    ).toBeInTheDocument()
  })

  it('Resend_Accepted_ShowsGenericConfirmationAndExactPublicContract', async () => {
    const fetchMock = vi.fn(() => Promise.resolve(response(202)))
    vi.stubGlobal('fetch', fetchMock)
    renderVerificationPage()

    fireEvent.change(screen.getByLabelText('E-mail'), {
      target: { value: 'person@example.com' },
    })
    fireEvent.submit(
      screen.getByRole('button', { name: 'Reenviar e-mail' }).closest('form')!,
    )

    expect(await screen.findByRole('status')).toHaveTextContent(
      'Se houver uma conta pendente de verificação para este e-mail, enviaremos um novo link.',
    )
    expect(document.body).not.toHaveTextContent(
      /conta encontrada|conta não encontrada|já verificada/i,
    )
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/auth/email-verification/resend',
      {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ email: 'person@example.com' }),
        credentials: 'same-origin',
        cache: 'no-store',
        signal: expect.any(AbortSignal),
      },
    )
  })

  it('Resend_PendingRequest_PreventsImmediateDoubleSubmit', async () => {
    let resolveRequest: ((value: Response) => void) | undefined
    const pendingRequest = new Promise<Response>((resolve) => {
      resolveRequest = resolve
    })
    const fetchMock = vi.fn(() => pendingRequest)
    vi.stubGlobal('fetch', fetchMock)
    renderVerificationPage()

    fireEvent.change(screen.getByLabelText('E-mail'), {
      target: { value: 'person@example.com' },
    })
    const form = screen
      .getByRole('button', { name: 'Reenviar e-mail' })
      .closest('form')!
    fireEvent.submit(form)
    fireEvent.submit(form)

    expect(screen.getByRole('button', { name: 'Reenviando…' })).toBeDisabled()
    expect(fetchMock).toHaveBeenCalledTimes(1)

    await act(async () => {
      resolveRequest?.(response(202))
      await pendingRequest
    })
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
