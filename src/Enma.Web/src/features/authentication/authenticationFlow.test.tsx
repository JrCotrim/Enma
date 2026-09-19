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

function fillRegistration(
  password = 'Synthetic!Password42',
  confirmation = password,
) {
  fireEvent.change(screen.getByLabelText('Nome da organização'), {
    target: { value: 'Enma Legal' },
  })
  fireEvent.change(screen.getByLabelText('Nome curto da organização'), {
    target: { value: 'enma-legal' },
  })
  fireEvent.change(screen.getByLabelText('Seu nome'), {
    target: { value: 'Ana Silva' },
  })
  fireEvent.change(screen.getByLabelText('E-mail'), {
    target: { value: 'owner@example.com' },
  })
  fireEvent.change(screen.getByLabelText('Senha'), {
    target: { value: password },
  })
  fireEvent.change(screen.getByLabelText('Confirmar senha'), {
    target: { value: confirmation },
  })
}

function fillAndSubmitRegistration() {
  fillRegistration()
  fireEvent.submit(
    screen.getByRole('button', { name: 'Criar conta' }).closest('form')!,
  )
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
  it('Root_Unauthenticated_RedirectsToLogin', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(response(401))))
    const router = renderRoute('/')

    expect(
      await screen.findByRole('heading', { name: 'Entrar no ENMA' }),
    ).toBeInTheDocument()
    expect(router.state.location.pathname).toBe('/login')
  })

  it('Root_Authenticated_PreservesOrganizationsEntrypoint', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.resolve(response(200, { items: [] }))),
    )
    const router = renderRoute('/')

    expect(
      await screen.findByRole('heading', { name: 'Suas organizações' }),
    ).toBeInTheDocument()
    expect(router.state.location.pathname).toBe('/organizations')
  })

  it('Login_ShowsFocusedEntryActionsWithoutVerificationResend', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValueOnce(response(401)))
    renderRoute('/login')

    expect(
      await screen.findByRole('heading', { name: 'Entrar no ENMA' }),
    ).toBeInTheDocument()
    expect(screen.getByText('Acesse seu espaço de trabalho.')).toBeInTheDocument()
    expect(screen.getByLabelText('E-mail')).toBeInTheDocument()
    expect(screen.getByLabelText('Senha')).toHaveAttribute('type', 'password')
    expect(
      screen.getByRole('link', { name: 'Esqueci minha senha' }),
    ).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Criar conta' })).toBeInTheDocument()
    expect(
      screen.queryByRole('link', { name: 'Reenviar e-mail de verificação' }),
    ).not.toBeInTheDocument()
  })

  it('Login_GoogleProviderEnabled_ShowsRealGoogleAction', async () => {
    vi.stubGlobal(
      'fetch',
      vi
        .fn()
        .mockResolvedValueOnce(response(401))
        .mockResolvedValueOnce(response(200, { google: true })),
    )
    renderRoute('/login')

    const googleButton = await screen.findByRole('button', {
      name: 'Continuar com o Google',
    })
    expect(googleButton).toBeInTheDocument()
    expect(googleButton.querySelector('.google-auth-icon')).toHaveAttribute(
      'aria-hidden',
      'true',
    )
  })

  it('Login_GoogleProviderDisabled_DoesNotOfferBrokenAction', async () => {
    vi.stubGlobal(
      'fetch',
      vi
        .fn()
        .mockResolvedValueOnce(response(401))
        .mockResolvedValueOnce(response(200, { google: false })),
    )
    renderRoute('/login')

    await screen.findByRole('heading', { name: 'Entrar no ENMA' })
    await waitFor(() => {
      expect(
        screen.queryByRole('button', { name: 'Continuar com o Google' }),
      ).not.toBeInTheDocument()
    })
  })

  it('Registration_ExplainsOrganizationShortName', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValueOnce(response(401)))
    renderRoute('/register')

    const shortName = await screen.findByLabelText('Nome curto da organização')
    const helper = screen.getByText(
      'Use letras, números e hífens. Ex.: escritorio-teste',
    )

    expect(shortName).toHaveAttribute('aria-describedby', helper.id)
  })

  it('Registration_UsesNativeRequiredAndEmailValidation', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValueOnce(response(401)))
    renderRoute('/register')

    const organizationName = await screen.findByLabelText('Nome da organização')
    const email = screen.getByLabelText('E-mail')

    expect(organizationName).toBeRequired()
    expect(email).toBeRequired()
    expect(email).toHaveAttribute('type', 'email')
    fireEvent.change(email, { target: { value: 'email-inválido' } })
    expect(email).toBeInvalid()
  })

  it('Registration_ShowsRealPasswordRequirementsAndBlocksInvalidPassword', async () => {
    const fetchMock = vi.fn().mockResolvedValueOnce(response(401))
    vi.stubGlobal('fetch', fetchMock)
    renderRoute('/register')

    await screen.findByRole('heading', { name: 'Criar conta' })
    fillRegistration('curta', 'curta')
    const password = screen.getByLabelText('Senha')

    expect(password).toHaveAttribute('minlength', '8')
    expect(password).toHaveAttribute('maxlength', '128')
    expect(screen.getByText('Sua senha deve ter:').parentElement).toHaveTextContent(
      '8 caracteres ou mais',
    )
    expect(screen.getByRole('progressbar')).toHaveAttribute('aria-valuenow', '0')
    expect(screen.getByRole('status')).toHaveTextContent('Nenhum requisito atendido')
    expect(screen.getByText(/verificada contra vazamentos conhecidos/)).toBeInTheDocument()

    fireEvent.submit(
      screen.getByRole('button', { name: 'Criar conta' }).closest('form')!,
    )

    const error = screen.getByRole('alert')
    expect(error).toHaveTextContent('Use pelo menos 8 caracteres.')
    expect(password).toHaveFocus()
    expect(password).toHaveAttribute('aria-invalid', 'true')
    expect(password.getAttribute('aria-describedby')).toContain(error.id)
    expect(
      fetchMock.mock.calls.some(([input]) => input === '/api/onboarding/register'),
    ).toBe(false)

    fireEvent.change(password, { target: { value: 'Synthetic!Password42' } })
    fireEvent.change(screen.getByLabelText('Confirmar senha'), {
      target: { value: 'Synthetic!Password42' },
    })

    expect(screen.queryByText('Use pelo menos 8 caracteres.')).not.toBeInTheDocument()
    expect(screen.getByRole('progressbar')).toHaveAttribute('aria-valuenow', '3')
    expect(screen.getByRole('status')).toHaveTextContent(
      'Todos os requisitos atendidos',
    )
    expect(screen.getAllByRole('checkbox', { checked: true })).toHaveLength(3)
  })

  it('Registration_MismatchIsInlineAndClearsWithoutLosingValues', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(response(401))
      .mockResolvedValueOnce(response(200, { google: false }))
      .mockResolvedValueOnce(response(201, { verificationEmailSent: true }))
    vi.stubGlobal('fetch', fetchMock)
    renderRoute('/register')

    await screen.findByRole('heading', { name: 'Criar conta' })
    fillRegistration('Synthetic!Password42', 'outra senha segura')
    const confirmation = screen.getByLabelText('Confirmar senha')
    const form = screen.getByRole('button', { name: 'Criar conta' }).closest('form')!
    fireEvent.submit(form)

    const error = screen.getByText('As senhas não coincidem.')
    expect(confirmation).toHaveFocus()
    expect(confirmation).toHaveAttribute('aria-invalid', 'true')
    expect(confirmation.getAttribute('aria-describedby')).toContain(error.id)
    expect(screen.getByLabelText('Nome da organização')).toHaveValue('Enma Legal')
    expect(screen.getByLabelText('E-mail')).toHaveValue('owner@example.com')

    fireEvent.change(confirmation, {
      target: { value: 'Synthetic!Password42' },
    })
    expect(screen.queryByText('As senhas não coincidem.')).not.toBeInTheDocument()
    fireEvent.submit(form)

    expect(
      await screen.findByText('Enviamos um link de verificação para o seu e-mail.'),
    ).toBeInTheDocument()
  })

  it('Registration_CompromisedPasswordUsesSafeFieldCode', async () => {
    vi.stubGlobal(
      'fetch',
      vi
        .fn()
        .mockResolvedValueOnce(response(401))
        .mockResolvedValueOnce(response(200, { google: false }))
        .mockResolvedValueOnce(response(400, { code: 'password_compromised' })),
    )
    renderRoute('/register')

    await screen.findByRole('heading', { name: 'Criar conta' })
    fillAndSubmitRegistration()

    const password = screen.getByLabelText('Senha')
    const error = await screen.findByText(
      'Essa senha já foi identificada como comprometida. Escolha uma senha diferente.',
    )
    expect(password).toHaveFocus()
    expect(password).toHaveAttribute('aria-invalid', 'true')
    expect(password.getAttribute('aria-describedby')).toContain(error.id)
    expect(screen.getByLabelText('E-mail')).toHaveValue('owner@example.com')
  })

  it('Registration_DuplicateShortName_AssociatesErrorPreservesFieldsAndRetries', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(response(401))
      .mockResolvedValueOnce(response(200, { google: false }))
      .mockResolvedValueOnce(
        response(409, { code: 'organization_slug_conflict' }),
      )
      .mockResolvedValueOnce(
        response(201, { verificationEmailSent: true }),
      )
    vi.stubGlobal('fetch', fetchMock)
    renderRoute('/register')

    await screen.findByRole('heading', { name: 'Criar conta' })
    fillAndSubmitRegistration()

    const shortName = await screen.findByLabelText('Nome curto da organização')
    const fieldError = await screen.findByText(
      'Este nome curto já está em uso. Escolha outro.',
    )
    expect(shortName).toHaveFocus()
    expect(shortName).toHaveAttribute('aria-invalid', 'true')
    expect(shortName.getAttribute('aria-describedby')).toContain(fieldError.id)
    expect(screen.getByLabelText('Nome da organização')).toHaveValue('Enma Legal')
    expect(screen.getByLabelText('Seu nome')).toHaveValue('Ana Silva')
    expect(screen.getByLabelText('E-mail')).toHaveValue('owner@example.com')
    expect(screen.getByLabelText('Senha')).toHaveValue('Synthetic!Password42')
    expect(screen.getByLabelText('Confirmar senha')).toHaveValue(
      'Synthetic!Password42',
    )

    fireEvent.change(shortName, { target: { value: 'enma-legal-novo' } })
    expect(shortName).not.toHaveAttribute('aria-invalid')
    expect(fieldError).not.toBeInTheDocument()
    fireEvent.submit(
      screen.getByRole('button', { name: 'Criar conta' }).closest('form')!,
    )

    expect(
      await screen.findByText('Enviamos um link de verificação para o seu e-mail.'),
    ).toBeInTheDocument()
    expect(fetchMock).toHaveBeenNthCalledWith(
      4,
      '/api/onboarding/register',
      expect.objectContaining({
        body: expect.stringContaining('"organizationSlug":"enma-legal-novo"'),
      }),
    )
  })

  it('Registration_UnknownConflict_RemainsGeneralAndDoesNotMarkShortName', async () => {
    vi.stubGlobal(
      'fetch',
      vi
        .fn()
        .mockResolvedValueOnce(response(401))
        .mockResolvedValueOnce(response(200, { google: false }))
        .mockResolvedValueOnce(response(409, { code: 'different_conflict' })),
    )
    renderRoute('/register')

    await screen.findByRole('heading', { name: 'Criar conta' })
    fillAndSubmitRegistration()

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Não foi possível criar a conta com os dados informados.',
    )
    expect(screen.getByLabelText('Nome curto da organização')).not.toHaveAttribute(
      'aria-invalid',
    )
  })

  it('Registration_GoogleProfileRequired_CollectsOnlyMissingName', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(response(401))
      .mockResolvedValueOnce(response(200, { requestToken: 'csrf-token' }))
      .mockResolvedValueOnce(response(422))
    vi.stubGlobal('fetch', fetchMock)
    renderRoute('/register?google=profile-required')

    expect(
      await screen.findByRole('heading', { name: 'Concluir cadastro' }),
    ).toBeInTheDocument()
    expect(screen.getByLabelText('Seu nome')).toBeInTheDocument()
    expect(screen.queryByLabelText('E-mail')).not.toBeInTheDocument()
    expect(screen.queryByLabelText('Senha')).not.toBeInTheDocument()
    expect(screen.queryByLabelText('Nome da organização')).not.toBeInTheDocument()

    fireEvent.change(screen.getByLabelText('Seu nome'), {
      target: { value: 'Nome informado' },
    })
    fireEvent.submit(
      screen.getByRole('button', { name: 'Concluir cadastro' }).closest('form')!,
    )

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Informe um nome válido para continuar.',
    )
    expect(fetchMock).toHaveBeenNthCalledWith(
      3,
      '/api/auth/google/complete-profile',
      expect.objectContaining({
        body: JSON.stringify({ name: 'Nome informado' }),
      }),
    )
  })

  it('Login_PasswordVisibility_TogglesWithoutSubmittingOrClearing', async () => {
    const fetchMock = vi.fn().mockResolvedValueOnce(response(401))
    vi.stubGlobal('fetch', fetchMock)
    renderRoute('/login')

    const password = await screen.findByLabelText('Senha')
    fireEvent.change(password, { target: { value: 'secret-value' } })

    expect(password).toHaveAttribute('type', 'password')
    const showPassword = screen.getByRole('button', { name: 'Mostrar senha' })
    expect(showPassword).toHaveAttribute('type', 'button')

    fireEvent.click(showPassword)

    expect(password).toHaveAttribute('type', 'text')
    expect(password).toHaveValue('secret-value')
    expect(screen.getByRole('button', { name: 'Ocultar senha' })).toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(2)

    fireEvent.click(screen.getByRole('button', { name: 'Ocultar senha' }))

    expect(password).toHaveAttribute('type', 'password')
    expect(password).toHaveValue('secret-value')
    expect(screen.getByRole('button', { name: 'Mostrar senha' })).toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(2)
  })

  it('Registration_PasswordVisibility_IsAvailable', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValueOnce(response(401)))
    renderRoute('/register')

    const password = await screen.findByLabelText('Senha')
    expect(password).toHaveAttribute('type', 'password')

    fireEvent.click(screen.getAllByRole('button', { name: 'Mostrar senha' })[0])

    expect(password).toHaveAttribute('type', 'text')
    expect(
      screen.getByRole('button', { name: 'Ocultar senha' }),
    ).toHaveAttribute('aria-controls', 'register-password')
  })

  it('Registration_DeliverySucceeded_ShowsTruthfulVerificationState', async () => {
    vi.stubGlobal(
      'fetch',
      vi
        .fn()
        .mockResolvedValueOnce(response(401))
        .mockResolvedValueOnce(response(200, { google: false }))
        .mockResolvedValueOnce(
          response(201, { verificationEmailSent: true }),
        ),
    )
    renderRoute('/register')

    await screen.findByRole('heading', { name: 'Criar conta' })
    fillAndSubmitRegistration()

    expect(
      await screen.findByText(
        'Enviamos um link de verificação para o seu e-mail.',
      ),
    ).toBeInTheDocument()
    expect(screen.getByLabelText('E-mail')).toHaveValue('owner@example.com')
    expect(
      screen.getByRole('button', { name: 'Reenviar e-mail' }),
    ).toBeInTheDocument()
  })

  it('Registration_DeliveryFailed_ShowsRecoveryAndCanResend', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(response(401))
      .mockResolvedValueOnce(response(200, { google: false }))
      .mockResolvedValueOnce(response(201, { verificationEmailSent: false }))
      .mockResolvedValueOnce(response(202))
    vi.stubGlobal('fetch', fetchMock)
    renderRoute('/register')

    await screen.findByRole('heading', { name: 'Criar conta' })
    fillAndSubmitRegistration()

    expect(
      await screen.findByText(
        'Não foi possível enviar o e-mail de verificação agora.',
      ),
    ).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Reenviar e-mail' }))

    expect(await screen.findByRole('status')).toHaveTextContent(
      'Se houver uma conta pendente de verificação para este e-mail, enviaremos um novo link.',
    )
    expect(fetchMock).toHaveBeenLastCalledWith(
      '/api/auth/email-verification/resend',
      expect.objectContaining({
        body: JSON.stringify({ email: 'owner@example.com' }),
      }),
    )
  })

  it('Login_ValidCredentials_SubmitsBackendContractAndShowsWorkspace', async () => {
    const localStorageSpy = vi.spyOn(Storage.prototype, 'setItem')
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(response(401))
      .mockResolvedValueOnce(response(200, { google: false }))
      .mockResolvedValueOnce(response(204))
      .mockResolvedValueOnce(response(200, { items: [] }))
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute('/login')

    await screen.findByRole('heading', { name: 'Entrar no ENMA' })
    fillAndSubmitLogin('person@example.com', 'correct horse battery staple')

    expect(
      await screen.findByRole('heading', { name: 'Suas organizações' }),
    ).toBeInTheDocument()
    expect(router.state.location.pathname).toBe('/organizations')
    expect(fetchMock).toHaveBeenNthCalledWith(3, '/api/auth/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        email: 'person@example.com',
        password: 'correct horse battery staple',
        completeGoogleLink: false,
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
        .mockResolvedValueOnce(response(200, { google: false }))
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

  it('Login_GoogleLinkRequired_RequiresDualProofAndReportsLinkFailure', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(response(401))
      .mockResolvedValueOnce(response(200, { google: false }))
      .mockResolvedValueOnce(response(409))
    vi.stubGlobal('fetch', fetchMock)
    renderRoute('/login?google=link-required')

    expect(await screen.findByRole('status')).toHaveTextContent(
      'Entre com sua senha para vincular o Google.',
    )
    fillAndSubmitLogin('person@example.com', 'correct-password')

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Não foi possível concluir o vínculo com o Google.',
    )
    expect(fetchMock).toHaveBeenNthCalledWith(
      3,
      '/api/auth/login',
      expect.objectContaining({
        body: JSON.stringify({
          email: 'person@example.com',
          password: 'correct-password',
          completeGoogleLink: true,
        }),
      }),
    )
  })

  it('Registration_WrongGoogleRecipient_ShowsSafeRecoveryCopy', async () => {
    vi.stubGlobal(
      'fetch',
      vi
        .fn()
        .mockResolvedValueOnce(response(401))
        .mockResolvedValueOnce(response(200, { google: true })),
    )
    renderRoute('/register?google=wrong-invitation')

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Use a conta Google correspondente ao e-mail que recebeu este convite.',
    )
    expect(
      await screen.findByRole('button', { name: 'Continuar com o Google' }),
    ).toBeInTheDocument()
  })

  it('Login_PendingRequest_PreventsDuplicateSubmission', async () => {
    let resolveLogin: ((value: Response) => void) | undefined
    const pendingLogin = new Promise<Response>((resolve) => {
      resolveLogin = resolve
    })
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(response(401))
      .mockResolvedValueOnce(response(200, { google: false }))
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

    expect(screen.getByRole('button', { name: 'Entrando…' })).toBeDisabled()
    expect(fetchMock).toHaveBeenCalledTimes(3)

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
        .mockResolvedValueOnce(response(200, { google: false }))
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
        Promise.resolve(
          response(200, {
            items: [
              {
                id: '2a6a0642-8e1d-47dd-bb54-3da70a4c638c',
                membershipId: '832d6000-3d51-4bf7-a09d-e78e98c5ab9a',
                name: 'Organização Alfa',
                role: 'Member',
              },
            ],
          }),
        ),
      ),
    )
    renderRoute('/organizations')

    expect(
      await screen.findByRole('heading', { name: 'Suas organizações' }),
    ).toBeInTheDocument()
    expect(screen.getByText('Organização Alfa')).toBeInTheDocument()
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
      await screen.findByRole('heading', { name: 'Suas organizações' }),
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
      screen.getByRole('heading', { name: 'Verificando acesso…' }),
    ).toBeInTheDocument()
    expect(
      screen.queryByRole('heading', { name: 'Suas organizações' }),
    ).not.toBeInTheDocument()

    await act(async () => {
      resolveSession?.(response(200, { items: [] }))
      await pendingSession
    })

    expect(
      await screen.findByRole('heading', { name: 'Suas organizações' }),
    ).toBeInTheDocument()
  })

  it('Login_ExistingSession_RedirectsWithoutShowingLoginForm', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.resolve(response(200, { items: [] }))),
    )
    renderRoute('/login')

    expect(
      await screen.findByRole('heading', { name: 'Suas organizações' }),
    ).toBeInTheDocument()
    expect(screen.queryByLabelText('Senha')).not.toBeInTheDocument()
  })

  it('Logout_SuccessfulRequestUsesCsrfAndNavigatesToLogin', async () => {
    const localStorageSpy = vi.spyOn(window.localStorage, 'setItem')
    const sessionStorageSpy = vi.spyOn(window.sessionStorage, 'setItem')
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(response(200, { items: [] }))
      .mockResolvedValueOnce(response(200, { items: [] }))
      .mockResolvedValueOnce(response(200, { requestToken: 'transient-token' }))
      .mockResolvedValueOnce(response(204))
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute('/organizations')

    await screen.findByRole('heading', { name: 'Suas organizações' })
    fireEvent.click(screen.getByRole('button', { name: 'Sair' }))

    expect(
      await screen.findByRole('heading', { name: 'Entrar no ENMA' }),
    ).toBeInTheDocument()
    expect(router.state.location.pathname).toBe('/login')
    expect(fetchMock).toHaveBeenNthCalledWith(3, '/api/auth/csrf', {
      method: 'GET',
      credentials: 'same-origin',
      cache: 'no-store',
    })
    expect(fetchMock).toHaveBeenNthCalledWith(4, '/api/auth/logout', {
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
        .mockResolvedValueOnce(response(200, { items: [] }))
        .mockResolvedValueOnce(response(200, { requestToken: 'transient-token' }))
        .mockRejectedValueOnce(new Error('private network detail')),
    )
    renderRoute('/organizations')

    await screen.findByRole('heading', { name: 'Suas organizações' })
    fireEvent.click(screen.getByRole('button', { name: 'Sair' }))

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Não foi possível sair agora. Tente novamente.')
    expect(alert).not.toHaveTextContent('private network detail')
    expect(
      screen.getByRole('heading', { name: 'Suas organizações' }),
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
