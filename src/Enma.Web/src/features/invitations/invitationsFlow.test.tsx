import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from '@testing-library/react'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import {
  AuthContext,
  type AuthContextValue,
} from '../authentication/AuthContext'
import { clearCsrfToken } from '../authentication/csrfClient'
import {
  CurrentOrganizationContext,
  OrganizationDiscoveryContext,
} from '../organizations/OrganizationContext'
import type { OrganizationNavigationItem } from '../organizations/organizationTypes'
import { InvitationsPage } from './InvitationsPage'

const organizationId = '11111111-1111-4111-8111-111111111111'
const memberInvitationId = '22222222-2222-4222-8222-222222222222'
const administratorInvitationId = '33333333-3333-4333-8333-333333333333'

function organization(
  role: OrganizationNavigationItem['role'] = 'Owner',
): OrganizationNavigationItem {
  return {
    id: organizationId,
    membershipId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
    name: 'Organização Alfa',
    role,
  }
}

function invitation(overrides: Record<string, unknown> = {}) {
  return {
    id: memberInvitationId,
    invitedEmail: 'membro@example.test',
    role: 'Member',
    status: 'Pending',
    createdAt: '2026-08-30T14:30:00Z',
    expiresAt: '2026-09-06T14:30:00Z',
    ...overrides,
  }
}

function response(
  status: number,
  body?: unknown,
  headers?: Record<string, string>,
): Response {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: {
      ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
      ...headers,
    },
  })
}

function invitationPage(
  items: readonly unknown[],
  pageNumber = 1,
  totalCount = items.length,
): Response {
  return response(200, { items, pageNumber, pageSize: 20, totalCount })
}

interface RenderOptions {
  readonly role?: OrganizationNavigationItem['role']
  readonly initialEntry?: string
  readonly refreshOrganizations?: ReturnType<typeof vi.fn<() => void>>
  readonly handleUnauthorized?: ReturnType<typeof vi.fn<() => void>>
}

function renderInvitations({
  role = 'Owner',
  initialEntry = `/organizations/${organizationId}/invitations`,
  refreshOrganizations = vi.fn<() => void>(),
  handleUnauthorized = vi.fn<() => void>(),
}: RenderOptions = {}) {
  const currentOrganization = organization(role)
  const authContext: AuthContextValue = {
    state: 'authenticated',
    login: async () => 'failure',
    logout: async () => undefined,
    retrySessionCheck: () => undefined,
    handleUnauthorized,
  }
  const router = createMemoryRouter(
    [
      {
        path: '/organizations/:organizationId/invitations',
        element: (
          <AuthContext.Provider value={authContext}>
            <OrganizationDiscoveryContext.Provider
              value={{
                state: {
                  status: 'success',
                  organizations: [currentOrganization],
                },
                refreshOrganizations,
              }}
            >
              <CurrentOrganizationContext.Provider
                value={{
                  currentOrganization,
                  organizations: [currentOrganization],
                }}
              >
                <InvitationsPage />
              </CurrentOrganizationContext.Provider>
            </OrganizationDiscoveryContext.Provider>
          </AuthContext.Provider>
        ),
      },
    ],
    { initialEntries: [initialEntry] },
  )

  render(<RouterProvider router={router} />)
  return { router, refreshOrganizations, handleUnauthorized }
}

beforeEach(() => {
  clearCsrfToken()
})

afterEach(() => {
  clearCsrfToken()
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

describe('organization invitations administration flow', () => {
  it('Owner gerencia convites de Member e Administrator', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() =>
        Promise.resolve(
          invitationPage([
            invitation(),
            invitation({
              id: administratorInvitationId,
              invitedEmail: 'admin@example.test',
              role: 'Administrator',
            }),
          ]),
        ),
      ),
    )

    renderInvitations()

    expect(await screen.findByRole('option', { name: 'Administrador' })).toBeInTheDocument()
    const memberRow = screen.getByText('membro@example.test').closest('tr')
    const administratorRow = screen.getByText('admin@example.test').closest('tr')
    expect(within(memberRow!).getByRole('button', { name: 'Reenviar' })).toBeInTheDocument()
    expect(within(administratorRow!).getByRole('button', { name: 'Revogar' })).toBeInTheDocument()
    expect(document.body.innerHTML).not.toContain(memberInvitationId)
    expect(document.body.innerHTML).not.toContain(administratorInvitationId)
  })

  it('Administrator cria e gerencia somente convites de Member', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() =>
        Promise.resolve(
          invitationPage([
            invitation(),
            invitation({
              id: administratorInvitationId,
              invitedEmail: 'admin@example.test',
              role: 'Administrator',
            }),
          ]),
        ),
      ),
    )

    renderInvitations({ role: 'Administrator' })

    expect(await screen.findByText('membro@example.test')).toBeInTheDocument()
    expect(screen.queryByRole('option', { name: 'Administrador' })).not.toBeInTheDocument()
    const memberRow = screen.getByText('membro@example.test').closest('tr')
    const administratorRow = screen.getByText('admin@example.test').closest('tr')
    expect(within(memberRow!).getByRole('button', { name: 'Reenviar' })).toBeInTheDocument()
    expect(administratorRow).toHaveTextContent(
      'Seu papel não permite gerenciar este convite.',
    )
    expect(within(administratorRow!).queryByRole('button')).not.toBeInTheDocument()
  })

  it('Member não acessa a superfície nem dispara request de lista', async () => {
    const fetchMock = vi.fn()
    vi.stubGlobal('fetch', fetchMock)

    renderInvitations({ role: 'Member' })

    expect(screen.getByRole('heading', { name: 'Acesso negado' })).toBeInTheDocument()
    expect(screen.queryByRole('form')).not.toBeInTheDocument()
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('mostra loading inicial e estado vazio com paginação real zerada', async () => {
    let resolveList!: (value: Response) => void
    const pending = new Promise<Response>((resolve) => {
      resolveList = resolve
    })
    vi.stubGlobal('fetch', vi.fn(() => pending))

    renderInvitations()

    expect(screen.getByText('Carregando convites…')).toBeInTheDocument()
    resolveList(invitationPage([]))
    expect(await screen.findByRole('heading', { name: 'Nenhum convite enviado' })).toBeInTheDocument()
    expect(screen.getByText('Página 1 de 1')).toBeInTheDocument()
    expect(screen.getByText('0 convites no total')).toBeInTheDocument()
  })

  it('trata erro de lista, 401 e retry sem expor detalhes do servidor', async () => {
    const handleUnauthorized = vi.fn<() => void>()
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(response(500, { detail: 'private database detail' }))
      .mockResolvedValueOnce(invitationPage([]))
      .mockResolvedValueOnce(response(401))
    vi.stubGlobal('fetch', fetchMock)

    const { router } = renderInvitations({ handleUnauthorized })

    expect(
      await screen.findByRole('heading', {
        name: 'Não foi possível carregar os convites',
      }),
    ).toBeInTheDocument()
    expect(screen.queryByText('private database detail')).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Tentar novamente' }))
    expect(await screen.findByRole('heading', { name: 'Nenhum convite enviado' })).toBeInTheDocument()

    await router.navigate(`/organizations/${organizationId}/invitations?page=2`)
    await waitFor(() => expect(handleUnauthorized).toHaveBeenCalledOnce())
  })

  it('lista 403 falha fechado, atualiza acesso e remove controles', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(invitationPage([invitation()], 1, 21))
      .mockResolvedValueOnce(
        response(403, { detail: 'private policy detail' }),
      )
    vi.stubGlobal('fetch', fetchMock)
    const refreshOrganizations = vi.fn<() => void>()
    renderInvitations({ refreshOrganizations })

    expect(await screen.findByRole('button', { name: 'Reenviar' })).toBeEnabled()
    fireEvent.click(screen.getByRole('button', { name: 'Próxima página' }))

    expect(
      await screen.findByRole('heading', {
        name: 'Acesso administrativo indisponível',
      }),
    ).toBeInTheDocument()
    expect(screen.queryByText('private policy detail')).not.toBeInTheDocument()
    expect(refreshOrganizations).toHaveBeenCalledOnce()
    expect(screen.queryByLabelText('E-mail')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Enviar convite' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Reenviar' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Revogar' })).not.toBeInTheDocument()
  })

  it.each([
    ['accepted', 'O serviço de entrega aceitou o envio.'],
    ['failed', 'Convite criado, mas o envio falhou.'],
  ] as const)(
    'create 201 %s atualiza a lista e informa delivery',
    async (deliveryStatus, message) => {
      const fetchMock = vi
        .fn()
        .mockResolvedValueOnce(invitationPage([]))
        .mockResolvedValueOnce(response(200, { requestToken: 'csrf-token' }))
        .mockResolvedValueOnce(
          response(201, { invitationId: memberInvitationId, deliveryStatus }),
        )
        .mockResolvedValueOnce(invitationPage([invitation()]))
      vi.stubGlobal('fetch', fetchMock)
      renderInvitations()

      await screen.findByRole('heading', { name: 'Nenhum convite enviado' })
      fireEvent.change(screen.getByLabelText('E-mail'), {
        target: { value: 'nova@example.test' },
      })
      fireEvent.click(screen.getByRole('button', { name: 'Enviar convite' }))

      expect(await screen.findByText(new RegExp(message, 'i'))).toBeInTheDocument()
      expect(fetchMock).toHaveBeenNthCalledWith(
        3,
        `/api/organizations/${organizationId}/invitations`,
        expect.objectContaining({
          method: 'POST',
          body: JSON.stringify({ email: 'nova@example.test', role: 'Member' }),
        }),
      )
      expect(await screen.findByText('membro@example.test')).toBeInTheDocument()
    },
  )

  it.each([
    [400, 'Revise o e-mail e o papel informados.'],
    [409, 'Já existe um vínculo ou convite incompatível para este e-mail.'],
  ] as const)('create trata %s com feedback seguro', async (status, message) => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(invitationPage([]))
      .mockResolvedValueOnce(response(200, { requestToken: 'csrf-token' }))
      .mockResolvedValueOnce(response(status, { detail: 'private detail' }))
    vi.stubGlobal('fetch', fetchMock)
    renderInvitations()

    await screen.findByRole('heading', { name: 'Nenhum convite enviado' })
    fireEvent.change(screen.getByLabelText('E-mail'), {
      target: { value: 'nova@example.test' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Enviar convite' }))

    expect(await screen.findByText(message)).toBeInTheDocument()
    expect(screen.queryByText('private detail')).not.toBeInTheDocument()
  })

  it('create 429 encerra o countdown após o último cooldown expirar', async () => {
    let now = 1_000
    let tick: (() => void) | undefined
    const originalSetInterval = window.setInterval
    vi.spyOn(Date, 'now').mockImplementation(() => now)
    const clearIntervalSpy = vi.spyOn(window, 'clearInterval')
    const setIntervalSpy = vi
      .spyOn(window, 'setInterval')
      .mockImplementation((handler, timeout, ...args) => {
        if (timeout === 1_000 && typeof handler === 'function') {
          tick = () => handler(...args)
          return 41
        }
        return originalSetInterval(handler, timeout, ...args)
      })
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(invitationPage([]))
      .mockResolvedValueOnce(response(200, { requestToken: 'csrf-token' }))
      .mockResolvedValueOnce(
        response(429, undefined, { 'Retry-After': '17' }),
      )
    vi.stubGlobal('fetch', fetchMock)
    renderInvitations()

    await screen.findByRole('heading', { name: 'Nenhum convite enviado' })
    fireEvent.change(screen.getByLabelText('E-mail'), {
      target: { value: 'nova@example.test' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Enviar convite' }))

    expect(await screen.findByText(/tente novamente em 17 segundos/i)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Aguarde 17s' })).toBeDisabled()
    fireEvent.click(screen.getByRole('button', { name: 'Aguarde 17s' }))
    expect(fetchMock).toHaveBeenCalledTimes(3)

    now = 18_001
    act(() => tick?.())

    expect(clearIntervalSpy).toHaveBeenCalledWith(41)
    expect(
      setIntervalSpy.mock.calls.filter(([, timeout]) => timeout === 1_000),
    ).toHaveLength(1)
    expect(screen.getByRole('button', { name: 'Enviar convite' })).toBeEnabled()
  })

  it('revoke exige confirmação, impede duplicidade e atualiza a lista', async () => {
    let resolveRevoke!: (value: Response) => void
    const revokePending = new Promise<Response>((resolve) => {
      resolveRevoke = resolve
    })
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(invitationPage([invitation()]))
      .mockResolvedValueOnce(response(200, { requestToken: 'csrf-token' }))
      .mockReturnValueOnce(revokePending)
      .mockResolvedValueOnce(
        invitationPage([invitation({ status: 'Revoked' })]),
      )
    vi.stubGlobal('fetch', fetchMock)
    renderInvitations()

    fireEvent.click(await screen.findByRole('button', { name: 'Revogar' }))
    const dialog = screen.getByRole('alertdialog')
    expect(dialog).toHaveTextContent('Este convite não poderá mais ser aceito.')
    const cancelButton = screen.getByRole('button', { name: 'Cancelar' })
    const confirmButton = screen.getByRole('button', {
      name: 'Confirmar revogação',
    })
    expect(cancelButton).toHaveFocus()
    fireEvent.keyDown(dialog, { key: 'Tab', shiftKey: true })
    expect(confirmButton).toHaveFocus()
    fireEvent.keyDown(dialog, { key: 'Tab' })
    expect(cancelButton).toHaveFocus()
    fireEvent.click(screen.getByRole('button', { name: 'Confirmar revogação' }))
    expect(screen.getByRole('button', { name: 'Revogando…' })).toBeDisabled()
    fireEvent.click(screen.getByRole('button', { name: 'Revogando…' }))
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(3))

    resolveRevoke(response(204))
    expect(await screen.findByText(/convite para membro@example.test revogado/i)).toBeInTheDocument()
    expect(await screen.findByText('Revogado')).toBeInTheDocument()
  })

  it.each([
    ['accepted', 'aceitou o reenvio'],
    ['failed', 'reenvio falhou'],
  ] as const)('resend %s informa delivery e atualiza a lista', async (deliveryStatus, message) => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(invitationPage([invitation()]))
      .mockResolvedValueOnce(response(200, { requestToken: 'csrf-token' }))
      .mockResolvedValueOnce(
        response(200, { invitationId: memberInvitationId, deliveryStatus }),
      )
      .mockResolvedValueOnce(invitationPage([invitation()]))
    vi.stubGlobal('fetch', fetchMock)
    renderInvitations()

    fireEvent.click(await screen.findByRole('button', { name: 'Reenviar' }))

    expect(await screen.findByText(new RegExp(message, 'i'))).toBeInTheDocument()
    expect(fetchMock).toHaveBeenNthCalledWith(
      3,
      `/api/organizations/${organizationId}/invitations/${memberInvitationId}/resend`,
      expect.objectContaining({ method: 'POST', body: undefined }),
    )
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(4))
  })

  it('resend 429 atrasado ancora Retry-After no processamento da resposta', async () => {
    let now = 1_000
    let tick: (() => void) | undefined
    let resolveResend!: (value: Response) => void
    const originalSetInterval = window.setInterval
    const pendingResend = new Promise<Response>((resolve) => {
      resolveResend = resolve
    })
    vi.spyOn(Date, 'now').mockImplementation(() => now)
    vi.spyOn(window, 'setInterval').mockImplementation((handler, timeout, ...args) => {
      if (timeout === 1_000 && typeof handler === 'function') {
        tick = () => handler(...args)
        return 42
      }
      return originalSetInterval(handler, timeout, ...args)
    })
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(invitationPage([invitation()]))
      .mockResolvedValueOnce(response(200, { requestToken: 'csrf-token' }))
      .mockReturnValueOnce(pendingResend)
    vi.stubGlobal('fetch', fetchMock)
    renderInvitations()

    fireEvent.click(await screen.findByRole('button', { name: 'Reenviar' }))
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(3))
    now = 6_000
    resolveResend(response(429, undefined, { 'Retry-After': '23' }))

    expect(await screen.findByText(/tente novamente em 23 segundos/i)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Reenviar em 23s' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Revogar' })).toBeEnabled()

    now = 7_000
    act(() => tick?.())
    expect(screen.getByRole('button', { name: 'Reenviar em 22s' })).toBeDisabled()
  })

  it('paginação usa totalCount e corrige página vazia após mutation', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(invitationPage([invitation()], 2, 21))
      .mockResolvedValueOnce(response(200, { requestToken: 'csrf-token' }))
      .mockResolvedValueOnce(response(204))
      .mockResolvedValueOnce(invitationPage([], 2, 20))
      .mockResolvedValueOnce(invitationPage([invitation({ status: 'Revoked' })], 1, 20))
    vi.stubGlobal('fetch', fetchMock)
    const { router } = renderInvitations({
      initialEntry: `/organizations/${organizationId}/invitations?page=2`,
    })

    expect(await screen.findByText('Página 2 de 2')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Revogar' }))
    fireEvent.click(screen.getByRole('button', { name: 'Confirmar revogação' }))

    await waitFor(() => expect(router.state.location.search).toBe(''))
    expect(await screen.findByText('Página 1 de 1')).toBeInTheDocument()
    expect(fetchMock).toHaveBeenLastCalledWith(
      `/api/organizations/${organizationId}/invitations?pageNumber=1&pageSize=20`,
      expect.objectContaining({ method: 'GET' }),
    )
  })

  it('403 em mutation falha fechado, atualiza acesso e remove ações', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(invitationPage([invitation()]))
      .mockResolvedValueOnce(response(200, { requestToken: 'csrf-token' }))
      .mockResolvedValueOnce(response(403, { detail: 'private policy detail' }))
    vi.stubGlobal('fetch', fetchMock)
    const refreshOrganizations = vi.fn<() => void>()
    renderInvitations({ refreshOrganizations })

    fireEvent.click(await screen.findByRole('button', { name: 'Reenviar' }))

    expect(
      await screen.findByRole('heading', {
        name: 'Acesso administrativo indisponível',
      }),
    ).toBeInTheDocument()
    expect(screen.getByText(/seu acesso administrativo mudou/i)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Reenviar' })).not.toBeInTheDocument()
    expect(screen.queryByText('private policy detail')).not.toBeInTheDocument()
    expect(refreshOrganizations).toHaveBeenCalledOnce()
  })
})
