import { StrictMode, useCallback, useState } from 'react'
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createAppRoutes } from '../../app/router'
import { clearCsrfToken } from '../authentication/csrfClient'
import { createEmailVerificationFlow } from '../email-verification/emailVerificationService'
import { InvitationResumeProvider } from './InvitationResumeContext'
import { captureInvitationRecipientHandoff } from './invitationRecipientHandoff'

const validToken = 'Abcdefghijklmnopqrstuvwxyz0123456789_-ABCDE'
const invitedOrganization = {
  id: '8d2d115d-2b50-49a4-afdf-43cc4a32b127',
  membershipId: '506d5664-cc19-4779-b9a9-c683196f1401',
  name: 'Almeida Advocacia',
  role: 'Member',
}
const ownerOrganization = {
  id: '2d09b885-3ae1-4dca-80bf-d1d114ea4aaf',
  membershipId: '18fb5903-dd38-49b3-8315-7ba2c1144158',
  name: 'Espaço inicial',
  role: 'Owner',
}

function response(status: number, body?: unknown): Response {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
  })
}

function usablePreview() {
  return {
    status: 'usable',
    organizationName: invitedOrganization.name,
    role: 'Member',
    invitedEmail: 'p***@example.com',
  }
}

function renderInvitation(fragment = `#token=${validToken}`) {
  window.history.replaceState(null, '', `/accept-invitation${fragment}`)
  const initialToken = captureInvitationRecipientHandoff(
    window.location,
    window.history,
  ).token
  const router = createMemoryRouter(
    createAppRoutes(createEmailVerificationFlow(undefined)),
    { initialEntries: ['/accept-invitation'] },
  )

  function TestRoot() {
    const [token, setToken] = useState(initialToken)
    const clearToken = useCallback(() => setToken(undefined), [])

    return (
      <InvitationResumeProvider token={token} onTokenConsumed={clearToken}>
        <StrictMode>
          <RouterProvider router={router} />
        </StrictMode>
      </InvitationResumeProvider>
    )
  }

  render(<TestRoot />)

  return router
}

function requestUrl(input: RequestInfo | URL): string {
  return typeof input === 'string' ? input : input.toString()
}

beforeEach(() => {
  clearCsrfToken()
  window.localStorage.clear()
  window.sessionStorage.clear()
})

afterEach(() => {
  clearCsrfToken()
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
  window.history.replaceState(null, '', '/')
})

describe('invitation recipient flow', () => {
  it('Preview_UsableAnonymous_ScrubsTokenAndOffersLoginOrRegistration', async () => {
    const localStorageSpy = vi.spyOn(window.localStorage, 'setItem')
    const sessionStorageSpy = vi.spyOn(window.sessionStorage, 'setItem')
    const fetchMock = vi.fn((input: RequestInfo | URL) => {
      const url = requestUrl(input)
      if (url === '/api/invitations/preview') {
        return Promise.resolve(response(200, usablePreview()))
      }
      if (url === '/api/me/organizations') return Promise.resolve(response(401))
      return Promise.resolve(response(500))
    })
    vi.stubGlobal('fetch', fetchMock)
    const router = renderInvitation()

    expect(window.location.hash).toBe('')
    expect(window.location.pathname).toBe('/accept-invitation')
    expect(window.location.search).toBe('')
    expect(
      await screen.findByRole('heading', { name: 'Você recebeu um convite' }),
    ).toBeInTheDocument()
    expect(screen.getByText(invitedOrganization.name)).toBeInTheDocument()
    expect(screen.getByText('Membro')).toBeInTheDocument()
    expect(screen.getByText('p***@example.com')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Entrar' })).toHaveAttribute(
      'href',
      '/login',
    )
    expect(screen.getByRole('link', { name: 'Criar conta' })).toHaveAttribute(
      'href',
      '/register',
    )
    expect(document.body).not.toHaveTextContent(validToken)
    expect(router.state.location.pathname).not.toContain(validToken)
    expect(router.state.location.search).not.toContain(validToken)
    expect(localStorageSpy).not.toHaveBeenCalled()
    expect(sessionStorageSpy).not.toHaveBeenCalled()
  })

  it.each([
    ['expired', 'Convite expirado'],
    ['invalid', 'Convite inválido'],
  ])('Preview_%s_ShowsClosedState', async (status, heading) => {
    vi.stubGlobal(
      'fetch',
      vi.fn((input: RequestInfo | URL) =>
        Promise.resolve(
          requestUrl(input) === '/api/invitations/preview'
            ? response(200, { status })
            : response(401),
        ),
      ),
    )
    renderInvitation()

    expect(await screen.findByRole('heading', { name: heading })).toBeInTheDocument()
    expect(document.body).not.toHaveTextContent(validToken)
  })

  it('Authenticated_AcceptsOnceWithCsrfRefreshesAndEntersOrganization', async () => {
    let accepted = false
    let resolveAccept: ((value: Response) => void) | undefined
    const pendingAccept = new Promise<Response>((resolve) => {
      resolveAccept = resolve
    })
    const fetchMock = vi.fn((input: RequestInfo | URL) => {
      const url = requestUrl(input)
      if (url === '/api/invitations/preview') {
        return Promise.resolve(response(200, usablePreview()))
      }
      if (url === '/api/auth/csrf') {
        return Promise.resolve(response(200, { requestToken: 'csrf-token' }))
      }
      if (url === '/api/invitations/accept') return pendingAccept
      if (url === '/api/me/organizations') {
        return Promise.resolve(
          response(200, { items: accepted ? [invitedOrganization] : [] }),
        )
      }
      return Promise.resolve(response(500))
    })
    vi.stubGlobal('fetch', fetchMock)
    const router = renderInvitation()

    expect(
      await screen.findByRole('heading', { name: 'Aceitando convite...' }),
    ).toBeInTheDocument()
    await waitFor(() => {
      expect(
        fetchMock.mock.calls.filter(
          ([input]) => requestUrl(input) === '/api/invitations/accept',
        ),
      ).toHaveLength(1)
    })

    accepted = true
    await act(async () => {
      resolveAccept?.(response(204))
      await pendingAccept
    })

    await waitFor(() => {
      expect(router.state.location.pathname).toBe(
        `/organizations/${invitedOrganization.id}`,
      )
    })
    expect(fetchMock).toHaveBeenCalledWith('/api/invitations/accept', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'X-CSRF-TOKEN': 'csrf-token',
      },
      body: JSON.stringify({ token: validToken }),
      cache: 'no-store',
      credentials: 'same-origin',
    })
  })

  it.each([
    [400, 'Não foi possível aceitar este convite.'],
    [429, 'Muitas tentativas foram feitas.'],
  ])('Accept_%i_ShowsSafeFeedback', async (status, message) => {
    const fetchMock = vi.fn((input: RequestInfo | URL) => {
      const url = requestUrl(input)
      if (url === '/api/invitations/preview') {
        return Promise.resolve(response(200, usablePreview()))
      }
      if (url === '/api/me/organizations') {
        return Promise.resolve(response(200, { items: [] }))
      }
      if (url === '/api/auth/csrf') {
        return Promise.resolve(response(200, { requestToken: 'csrf-token' }))
      }
      if (url === '/api/invitations/accept') {
        return Promise.resolve(response(status))
      }
      return Promise.resolve(response(500))
    })
    vi.stubGlobal('fetch', fetchMock)
    renderInvitation()

    expect(await screen.findByRole('alert')).toHaveTextContent(message)
    expect(document.body).not.toHaveTextContent(validToken)
  })

  it('Accept_Unauthorized_ReturnsToCurrentLoginFlow', async () => {
    let discoveryCount = 0
    const fetchMock = vi.fn((input: RequestInfo | URL) => {
      const url = requestUrl(input)
      if (url === '/api/invitations/preview') {
        return Promise.resolve(response(200, usablePreview()))
      }
      if (url === '/api/me/organizations') {
        discoveryCount += 1
        return Promise.resolve(
          discoveryCount === 1
            ? response(200, { items: [] })
            : response(200, { items: [] }),
        )
      }
      if (url === '/api/auth/csrf') {
        return Promise.resolve(response(200, { requestToken: 'csrf-token' }))
      }
      if (url === '/api/invitations/accept') return Promise.resolve(response(401))
      return Promise.resolve(response(500))
    })
    vi.stubGlobal('fetch', fetchMock)
    renderInvitation()

    expect(await screen.findByRole('link', { name: 'Entrar' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Criar conta' })).toBeInTheDocument()
  })

  it('Registration_PreservesInviteThroughVerificationAndLoginResume', async () => {
    let loggedIn = false
    let accepted = false
    const fetchMock = vi.fn((input: RequestInfo | URL) => {
      const url = requestUrl(input)
      if (url === '/api/invitations/preview') {
        return Promise.resolve(response(200, usablePreview()))
      }
      if (url === '/api/onboarding/register') return Promise.resolve(response(201, {}))
      if (url === '/api/auth/login') {
        loggedIn = true
        return Promise.resolve(response(204))
      }
      if (url === '/api/auth/csrf') {
        return Promise.resolve(response(200, { requestToken: 'csrf-token' }))
      }
      if (url === '/api/invitations/accept') {
        accepted = true
        return Promise.resolve(response(204))
      }
      if (url === '/api/me/organizations') {
        if (!loggedIn) return Promise.resolve(response(401))
        return Promise.resolve(
          response(200, {
            items: accepted
              ? [ownerOrganization, invitedOrganization]
              : [ownerOrganization],
          }),
        )
      }
      return Promise.resolve(response(500))
    })
    vi.stubGlobal('fetch', fetchMock)
    const router = renderInvitation()

    fireEvent.click(await screen.findByRole('link', { name: 'Criar conta' }))
    expect(await screen.findByRole('heading', { name: 'Criar conta' })).toBeInTheDocument()

    fireEvent.change(screen.getByLabelText('Nome da organização'), {
      target: { value: ownerOrganization.name },
    })
    fireEvent.change(screen.getByLabelText('Identificador da organização'), {
      target: { value: 'espaco-inicial' },
    })
    fireEvent.change(screen.getByLabelText('Seu nome'), {
      target: { value: 'Pessoa Convidada' },
    })
    fireEvent.change(screen.getByLabelText('E-mail'), {
      target: { value: 'person@example.com' },
    })
    fireEvent.change(screen.getByLabelText('Senha'), {
      target: { value: 'Synthetic!Password42' },
    })
    fireEvent.submit(
      screen.getByRole('button', { name: 'Criar conta' }).closest('form')!,
    )

    expect(
      await screen.findByRole('heading', { name: 'Verifique seu e-mail' }),
    ).toBeInTheDocument()
    expect(document.body).not.toHaveTextContent(validToken)
    fireEvent.click(screen.getByRole('link', { name: 'Já verifiquei, entrar' }))

    await screen.findByRole('heading', { name: 'Entrar no ENMA' })
    fireEvent.change(screen.getByLabelText('E-mail'), {
      target: { value: 'person@example.com' },
    })
    fireEvent.change(screen.getByLabelText('Senha'), {
      target: { value: 'Synthetic!Password42' },
    })
    fireEvent.submit(screen.getByRole('button', { name: 'Entrar' }).closest('form')!)

    await waitFor(() => {
      expect(router.state.location.pathname).toBe(
        `/organizations/${invitedOrganization.id}`,
      )
    })
    expect(fetchMock).toHaveBeenCalledWith('/api/onboarding/register', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        organizationName: ownerOrganization.name,
        organizationSlug: 'espaco-inicial',
        ownerName: 'Pessoa Convidada',
        ownerEmail: 'person@example.com',
        password: 'Synthetic!Password42',
      }),
      credentials: 'same-origin',
      cache: 'no-store',
      signal: expect.any(AbortSignal),
    })
  })

  it('ReloadWithoutFragment_FailsClosedAndDoesNotCallRecipientApis', () => {
    const fetchMock = vi.fn((input: RequestInfo | URL) =>
      Promise.resolve(
        requestUrl(input) === '/api/me/organizations'
          ? response(401)
          : response(500),
      ),
    )
    vi.stubGlobal('fetch', fetchMock)
    renderInvitation('')

    expect(
      screen.getByRole('heading', { name: 'Abra o link original' }),
    ).toBeInTheDocument()
    expect(
      fetchMock.mock.calls.some(
        ([input]) => requestUrl(input) === '/api/invitations/preview',
      ),
    ).toBe(false)
    expect(
      fetchMock.mock.calls.some(
        ([input]) => requestUrl(input) === '/api/invitations/accept',
      ),
    ).toBe(false)
  })
})
