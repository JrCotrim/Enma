import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createAppRoutes } from '../../app/router'
import { createEmailVerificationFlow } from '../email-verification/emailVerificationService'
import { clearCsrfToken } from './csrfClient'

function response(status: number, body?: unknown): Response {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
  })
}

function renderRoute(path: string) {
  const router = createMemoryRouter(
    createAppRoutes(createEmailVerificationFlow(undefined)),
    { initialEntries: [path] },
  )

  render(<RouterProvider router={router} />)
  return router
}

function fillAndSubmitLogin(email: string, password: string) {
  fireEvent.change(screen.getByLabelText('E-mail'), {
    target: { value: email },
  })
  fireEvent.change(screen.getByLabelText('Senha'), {
    target: { value: password },
  })
  fireEvent.submit(screen.getByRole('button', { name: 'Entrar' }).closest('form')!)
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
})

describe('authentication flow', () => {
  it('Login_ValidCredentials_SubmitsBackendContractAndShowsWorkspace', async () => {
    const localStorageSpy = vi.spyOn(Storage.prototype, 'setItem')
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(response(401))
      .mockResolvedValueOnce(response(204))
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute('/login')

    await screen.findByRole('heading', { name: 'Entrar no ENMA' })
    fillAndSubmitLogin('person@example.com', 'correct horse battery staple')

    expect(
      await screen.findByRole('heading', { name: 'Sua sessão está ativa' }),
    ).toBeInTheDocument()
    expect(router.state.location.pathname).toBe('/organizations')
    expect(fetchMock).toHaveBeenNthCalledWith(2, '/api/auth/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        email: 'person@example.com',
        password: 'correct horse battery staple',
      }),
      credentials: 'same-origin',
      cache: 'no-store',
      signal: expect.any(AbortSignal),
    })
    expect(router.state.location.pathname).not.toContain('correct')
    expect(localStorageSpy).not.toHaveBeenCalled()
  })

  it('Login_InvalidCredentials_ShowsOnlyGenericFailure', async () => {
    vi.stubGlobal(
      'fetch',
      vi
        .fn()
        .mockResolvedValueOnce(response(401))
        .mockResolvedValueOnce(response(401)),
    )
    renderRoute('/login')

    await screen.findByRole('heading', { name: 'Entrar no ENMA' })
    fillAndSubmitLogin('unknown@example.com', 'not-correct')

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent(
      'Não foi possível entrar com as credenciais informadas.',
    )
    expect(alert).not.toHaveTextContent(/inexistente|incorreta|não verificado|inativo/i)
  })

  it('Login_PendingRequest_PreventsDuplicateSubmission', async () => {
    let resolveLogin: ((value: Response) => void) | undefined
    const pendingLogin = new Promise<Response>((resolve) => {
      resolveLogin = resolve
    })
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(response(401))
      .mockReturnValueOnce(pendingLogin)
    vi.stubGlobal('fetch', fetchMock)
    renderRoute('/login')

    await screen.findByRole('heading', { name: 'Entrar no ENMA' })
    fireEvent.change(screen.getByLabelText('E-mail'), {
      target: { value: 'person@example.com' },
    })
    fireEvent.change(screen.getByLabelText('Senha'), {
      target: { value: 'pending-password' },
    })
    const form = screen.getByRole('button', { name: 'Entrar' }).closest('form')!
    fireEvent.submit(form)
    fireEvent.submit(form)

    expect(screen.getByRole('button', { name: 'Entrando...' })).toBeDisabled()
    expect(fetchMock).toHaveBeenCalledTimes(2)

    await act(async () => {
      resolveLogin?.(response(401))
      await pendingLogin
    })
  })

  it('Login_NetworkFailure_ShowsSafeGenericError', async () => {
    vi.stubGlobal(
      'fetch',
      vi
        .fn()
        .mockResolvedValueOnce(response(401))
        .mockRejectedValueOnce(new Error('private network detail')),
    )
    renderRoute('/login')

    await screen.findByRole('heading', { name: 'Entrar no ENMA' })
    fillAndSubmitLogin('person@example.com', 'secret-value')

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent(
      'Não foi possível entrar agora. Tente novamente mais tarde.',
    )
    expect(alert).not.toHaveTextContent('private network detail')
  })

  it('SessionBootstrap_SuccessWithOrganizations_RestoresAuthenticatedState', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() =>
        Promise.resolve(response(200, { items: [{ id: 'ignored' }] })),
      ),
    )
    renderRoute('/organizations')

    expect(
      await screen.findByRole('heading', { name: 'Sua sessão está ativa' }),
    ).toBeInTheDocument()
  })

  it('SessionBootstrap_SuccessWithNoOrganizations_RemainsAuthenticated', async () => {
    const localStorageSpy = vi.spyOn(window.localStorage, 'setItem')
    const sessionStorageSpy = vi.spyOn(window.sessionStorage, 'setItem')
    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.resolve(response(200, { items: [] }))),
    )
    renderRoute('/organizations')

    expect(
      await screen.findByRole('heading', { name: 'Sua sessão está ativa' }),
    ).toBeInTheDocument()
    expect(localStorageSpy).not.toHaveBeenCalled()
    expect(sessionStorageSpy).not.toHaveBeenCalled()
  })

  it('SessionBootstrap_Unauthorized_RedirectsProtectedRouteToLogin', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(response(401))))
    const router = renderRoute('/organizations')

    expect(
      await screen.findByRole('heading', { name: 'Entrar no ENMA' }),
    ).toBeInTheDocument()
    expect(router.state.location.pathname).toBe('/login')
  })

  it('SessionBootstrap_Pending_DoesNotRenderProtectedContent', async () => {
    let resolveSession: ((value: Response) => void) | undefined
    const pendingSession = new Promise<Response>((resolve) => {
      resolveSession = resolve
    })
    vi.stubGlobal('fetch', vi.fn(() => pendingSession))
    renderRoute('/organizations')

    expect(
      screen.getByRole('heading', { name: 'Verificando acesso...' }),
    ).toBeInTheDocument()
    expect(
      screen.queryByRole('heading', { name: 'Sua sessão está ativa' }),
    ).not.toBeInTheDocument()

    await act(async () => {
      resolveSession?.(response(200, { items: [] }))
      await pendingSession
    })

    expect(
      await screen.findByRole('heading', { name: 'Sua sessão está ativa' }),
    ).toBeInTheDocument()
  })

  it('Login_ExistingSession_RedirectsWithoutShowingLoginForm', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.resolve(response(200, { items: [] }))),
    )
    renderRoute('/login')

    expect(
      await screen.findByRole('heading', { name: 'Sua sessão está ativa' }),
    ).toBeInTheDocument()
    expect(screen.queryByLabelText('Senha')).not.toBeInTheDocument()
  })

  it('Logout_SuccessfulRequestUsesCsrfAndNavigatesToLogin', async () => {
    const localStorageSpy = vi.spyOn(window.localStorage, 'setItem')
    const sessionStorageSpy = vi.spyOn(window.sessionStorage, 'setItem')
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(response(200, { items: [] }))
      .mockResolvedValueOnce(response(200, { requestToken: 'transient-token' }))
      .mockResolvedValueOnce(response(204))
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute('/organizations')

    await screen.findByRole('heading', { name: 'Sua sessão está ativa' })
    fireEvent.click(screen.getByRole('button', { name: 'Sair' }))

    expect(
      await screen.findByRole('heading', { name: 'Entrar no ENMA' }),
    ).toBeInTheDocument()
    expect(router.state.location.pathname).toBe('/login')
    expect(fetchMock).toHaveBeenNthCalledWith(2, '/api/auth/csrf', {
      method: 'GET',
      credentials: 'same-origin',
      cache: 'no-store',
    })
    expect(fetchMock).toHaveBeenNthCalledWith(3, '/api/auth/logout', {
      method: 'POST',
      headers: { 'X-CSRF-TOKEN': 'transient-token' },
      cache: 'no-store',
      credentials: 'same-origin',
    })
    expect(localStorageSpy).not.toHaveBeenCalled()
    expect(sessionStorageSpy).not.toHaveBeenCalled()
  })

  it('Logout_NetworkFailureKeepsAuthenticatedStateAndShowsGenericError', async () => {
    vi.stubGlobal(
      'fetch',
      vi
        .fn()
        .mockResolvedValueOnce(response(200, { items: [] }))
        .mockResolvedValueOnce(response(200, { requestToken: 'transient-token' }))
        .mockRejectedValueOnce(new Error('private network detail')),
    )
    renderRoute('/organizations')

    await screen.findByRole('heading', { name: 'Sua sessão está ativa' })
    fireEvent.click(screen.getByRole('button', { name: 'Sair' }))

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Não foi possível sair agora. Tente novamente.')
    expect(alert).not.toHaveTextContent('private network detail')
    expect(
      screen.getByRole('heading', { name: 'Sua sessão está ativa' }),
    ).toBeInTheDocument()
  })

  it('SessionBootstrap_NetworkFailureOffersRetryWithoutShowingLogin', async () => {
    const fetchMock = vi
      .fn()
      .mockRejectedValueOnce(new Error('offline'))
      .mockResolvedValueOnce(response(401))
    vi.stubGlobal('fetch', fetchMock)
    renderRoute('/organizations')

    expect(
      await screen.findByRole('heading', {
        name: 'Não foi possível verificar seu acesso',
      }),
    ).toBeInTheDocument()
    expect(screen.queryByLabelText('Senha')).not.toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'Tentar novamente' }))

    await waitFor(() => {
      expect(screen.getByLabelText('Senha')).toBeInTheDocument()
    })
  })
})
