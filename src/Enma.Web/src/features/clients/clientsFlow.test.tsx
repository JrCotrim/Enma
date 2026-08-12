import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createAppRoutes } from '../../app/router'
import { clearCsrfToken } from '../authentication/csrfClient'
import { createEmailVerificationFlow } from '../email-verification/emailVerificationService'
import type { OrganizationNavigationItem } from '../organizations/organizationTypes'
import type { Client } from './clientTypes'

const organizationA: OrganizationNavigationItem = {
  id: '11111111-1111-4111-8111-111111111111',
  name: 'Organização Alfa',
  role: 'Member',
}

const organizationB: OrganizationNavigationItem = {
  id: '22222222-2222-4222-8222-222222222222',
  name: 'Organização Beta',
  role: 'Owner',
}

const activeClient: Client = {
  id: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
  name: 'Cliente Ativo',
  isActive: true,
  createdAt: '2026-08-12T14:30:00Z',
}

const inactiveClient: Client = {
  id: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',
  name: 'Cliente Inativo',
  isActive: false,
  createdAt: '2026-08-11T12:00:00Z',
}

function response(status: number, body?: unknown): Response {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers:
      body === undefined ? undefined : { 'Content-Type': 'application/json' },
  })
}

function organizationResponse(
  items: readonly OrganizationNavigationItem[],
): Response {
  return response(200, { items })
}

function clientListResponse(
  items: readonly Client[],
  pageNumber = 1,
): Response {
  return response(200, { items, pageNumber, pageSize: 20 })
}

function renderRoute(path: string) {
  const router = createMemoryRouter(
    createAppRoutes(createEmailVerificationFlow(undefined)),
    { initialEntries: [path] },
  )

  render(<RouterProvider router={router} />)
  return router
}

function authenticatedFetch(
  organization: OrganizationNavigationItem,
  clientResponse: Response,
) {
  return vi
    .fn()
    .mockResolvedValueOnce(organizationResponse([]))
    .mockResolvedValueOnce(organizationResponse([organization]))
    .mockResolvedValueOnce(clientResponse)
}

function openAndSubmitCreate(name: string) {
  fireEvent.click(screen.getByRole('button', { name: 'Cadastrar cliente' }))
  fireEvent.change(screen.getByLabelText('Nome'), { target: { value: name } })
  const form = screen.getByRole('button', { name: 'Cadastrar' }).closest('form')
  fireEvent.submit(form!)
  return form!
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

describe('Clients D1 flow', () => {
  it('ClientList_MemberRoute_RendersActiveAndInactiveClientsWithoutCreateOrPersistence', async () => {
    const localStorageSpy = vi.spyOn(window.localStorage, 'setItem')
    const sessionStorageSpy = vi.spyOn(window.sessionStorage, 'setItem')
    const fetchMock = authenticatedFetch(
      organizationA,
      clientListResponse([activeClient, inactiveClient]),
    )
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(`/organizations/${organizationA.id}/clients`)

    expect(
      await screen.findByRole('heading', { name: 'Clientes' }),
    ).toBeInTheDocument()
    expect(await screen.findByText(activeClient.name)).toBeInTheDocument()
    expect(screen.getByText(inactiveClient.name)).toBeInTheDocument()
    expect(screen.getByText('Ativo')).toBeInTheDocument()
    expect(screen.getByText('Inativo')).toBeInTheDocument()
    expect(
      screen.queryByRole('button', { name: 'Cadastrar cliente' }),
    ).not.toBeInTheDocument()
    expect(fetchMock).toHaveBeenNthCalledWith(
      3,
      `/api/organizations/${organizationA.id}/clients?pageNumber=1&pageSize=20`,
      {
        method: 'GET',
        cache: 'no-store',
        signal: expect.any(AbortSignal),
        credentials: 'same-origin',
      },
    )
    expect(localStorageSpy).not.toHaveBeenCalled()
    expect(sessionStorageSpy).not.toHaveBeenCalled()
  })

  it.each(['Owner', 'Administrator'] as const)(
    'ClientCreate_%sRole_ShowsAction',
    async (role) => {
      const organization = { ...organizationA, role }
      vi.stubGlobal(
        'fetch',
        authenticatedFetch(organization, clientListResponse([])),
      )

      renderRoute(`/organizations/${organization.id}/clients`)

      expect(
        await screen.findByRole('button', { name: 'Cadastrar cliente' }),
      ).toBeInTheDocument()
      expect(
        await screen.findByRole('button', { name: 'Cadastrar primeiro cliente' }),
      ).toBeInTheDocument()
    },
  )

  it('ClientList_EmptyMemberPage_ShowsEmptyStateWithoutCreateImplication', async () => {
    vi.stubGlobal(
      'fetch',
      authenticatedFetch(organizationA, clientListResponse([])),
    )

    renderRoute(`/organizations/${organizationA.id}/clients`)

    expect(
      await screen.findByText('Nenhum cliente cadastrado nesta organização.'),
    ).toBeInTheDocument()
    expect(screen.queryByText(/cadastrar/i)).not.toBeInTheDocument()
  })

  it('ClientPagination_UrlPage_RequestsBackendPageAndNavigatesWithUrlState', async () => {
    const fullPage = Array.from({ length: 20 }, (_, index) => ({
      ...activeClient,
      id: `aaaaaaaa-aaaa-4aaa-8aaa-${index.toString().padStart(12, '0')}`,
      name: `Cliente ${index + 1}`,
    }))
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(organizationResponse([]))
      .mockResolvedValueOnce(organizationResponse([organizationA]))
      .mockResolvedValueOnce(clientListResponse(fullPage, 2))
      .mockResolvedValueOnce(clientListResponse([], 3))
      .mockResolvedValueOnce(clientListResponse(fullPage, 2))
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(
      `/organizations/${organizationA.id}/clients?page=2`,
    )

    expect(await screen.findByText('Cliente 1')).toBeInTheDocument()
    expect(fetchMock.mock.calls[2]?.[0]).toBe(
      `/api/organizations/${organizationA.id}/clients?pageNumber=2&pageSize=20`,
    )

    fireEvent.click(
      screen.getByRole('button', { name: 'Próxima página de clientes' }),
    )
    expect(
      await screen.findByText('Nenhum cliente encontrado nesta página.'),
    ).toBeInTheDocument()
    expect(router.state.location.search).toBe('?page=3')

    fireEvent.click(
      screen.getByRole('button', { name: 'Página anterior de clientes' }),
    )
    expect(await screen.findByText('Cliente 1')).toBeInTheDocument()
    expect(router.state.location.search).toBe('?page=2')
  })

  it('ClientPagination_InvalidPage_NormalizesAndNeverRequestsInvalidBackendPage', async () => {
    const fetchMock = authenticatedFetch(
      organizationA,
      clientListResponse([], 1),
    )
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(
      `/organizations/${organizationA.id}/clients?page=-4`,
    )

    await screen.findByText('Nenhum cliente cadastrado nesta organização.')

    expect(fetchMock.mock.calls[2]?.[0]).toContain('pageNumber=1')
    expect(fetchMock.mock.calls[2]?.[0]).not.toContain('pageNumber=-4')
    expect(router.state.location.search).toBe('')
  })

  it('ClientList_OldPageResponseCompletesLast_DoesNotReplaceCurrentPage', async () => {
    let resolvePageOne: ((value: Response) => void) | undefined
    const pendingPageOne = new Promise<Response>((resolve) => {
      resolvePageOne = resolve
    })
    const pageOneClient = { ...activeClient, name: 'Cliente Página Um' }
    const pageTwoClient = { ...activeClient, id: inactiveClient.id, name: 'Cliente Página Dois' }
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(organizationResponse([]))
      .mockResolvedValueOnce(organizationResponse([organizationA]))
      .mockReturnValueOnce(pendingPageOne)
      .mockResolvedValueOnce(clientListResponse([pageTwoClient], 2))
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(`/organizations/${organizationA.id}/clients`)

    await screen.findByText('Carregando clientes...')
    await act(async () => {
      await router.navigate(
        `/organizations/${organizationA.id}/clients?page=2`,
      )
    })
    expect(await screen.findByText(pageTwoClient.name)).toBeInTheDocument()

    await act(async () => {
      resolvePageOne?.(clientListResponse([pageOneClient], 1))
      await pendingPageOne
    })

    expect(screen.getByText(pageTwoClient.name)).toBeInTheDocument()
    expect(screen.queryByText(pageOneClient.name)).not.toBeInTheDocument()
  })

  it('ClientList_OldOrganizationResponseCompletesLast_NeverRendersInNewOrganization', async () => {
    let resolveOrganizationA: ((value: Response) => void) | undefined
    const pendingOrganizationA = new Promise<Response>((resolve) => {
      resolveOrganizationA = resolve
    })
    const clientA = { ...activeClient, name: 'Cliente da Alfa' }
    const clientB = { ...activeClient, id: inactiveClient.id, name: 'Cliente da Beta' }
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(organizationResponse([]))
      .mockResolvedValueOnce(organizationResponse([organizationA, organizationB]))
      .mockReturnValueOnce(pendingOrganizationA)
      .mockResolvedValueOnce(clientListResponse([clientB]))
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(`/organizations/${organizationA.id}/clients`)

    await screen.findByText('Carregando clientes...')
    await act(async () => {
      await router.navigate(`/organizations/${organizationB.id}/clients`)
    })
    expect(await screen.findByText(clientB.name)).toBeInTheDocument()

    await act(async () => {
      resolveOrganizationA?.(clientListResponse([clientA]))
      await pendingOrganizationA
    })

    expect(screen.getByText(clientB.name)).toBeInTheDocument()
    expect(screen.queryByText(clientA.name)).not.toBeInTheDocument()
  })

  it('ClientCreate_Success_SendsExactTenantScopedCsrfBodyAndRefreshesWithoutOptimism', async () => {
    const localStorageSpy = vi.spyOn(window.localStorage, 'setItem')
    const sessionStorageSpy = vi.spyOn(window.sessionStorage, 'setItem')
    let resolveCreate: ((value: Response) => void) | undefined
    const pendingCreate = new Promise<Response>((resolve) => {
      resolveCreate = resolve
    })
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(organizationResponse([]))
      .mockResolvedValueOnce(organizationResponse([organizationB]))
      .mockResolvedValueOnce(clientListResponse([activeClient]))
      .mockResolvedValueOnce(response(200, { requestToken: 'test-token' }))
      .mockReturnValueOnce(pendingCreate)
      .mockResolvedValueOnce(clientListResponse([activeClient]))
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(`/organizations/${organizationB.id}/clients`)
    await screen.findByText(activeClient.name)
    const form = openAndSubmitCreate('  Cliente  ')

    expect(
      await screen.findByRole('button', { name: 'Cadastrando...' }),
    ).toBeDisabled()
    fireEvent.submit(form)
    expect(fetchMock).toHaveBeenCalledTimes(5)
    expect(screen.queryByText('Cliente', { exact: true })).not.toBeInTheDocument()

    const [postUrl, postInit] = fetchMock.mock.calls[4] as [string, RequestInit]
    expect(postUrl).toBe(
      `/api/organizations/${organizationB.id}/clients`,
    )
    expect(JSON.parse(postInit.body as string)).toEqual({ name: 'Cliente' })
    expect(Object.keys(JSON.parse(postInit.body as string))).toEqual(['name'])
    expect(postInit.headers).toEqual({
      'Content-Type': 'application/json',
      'X-CSRF-TOKEN': 'test-token',
    })
    expect(localStorageSpy).not.toHaveBeenCalled()
    expect(sessionStorageSpy).not.toHaveBeenCalled()

    await act(async () => {
      resolveCreate?.(response(201, { id: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc' }))
      await pendingCreate
    })

    expect(
      await screen.findByText('Cliente cadastrado com sucesso.'),
    ).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Novo cliente' })).not.toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(6)
  })

  it('ClientCreate_StaleAdministratorRoleGetsForbidden_ShowsPermissionWithoutInsertOrRetry', async () => {
    const administrator = { ...organizationA, role: 'Administrator' as const }
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(organizationResponse([]))
      .mockResolvedValueOnce(organizationResponse([administrator]))
      .mockResolvedValueOnce(clientListResponse([activeClient]))
      .mockResolvedValueOnce(response(200, { requestToken: 'test-token' }))
      .mockResolvedValueOnce(response(403, { detail: 'private authorization reason' }))
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(`/organizations/${administrator.id}/clients`)
    await screen.findByText(activeClient.name)
    openAndSubmitCreate('Cliente Negado')

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent(
      'Você não tem permissão para cadastrar clientes nesta organização.',
    )
    expect(alert).not.toHaveTextContent('private authorization reason')
    expect(screen.queryByText('Cliente Negado')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Cadastrar' })).toBeEnabled()
    expect(screen.getByRole('button', { name: 'Atualizar acesso' })).toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(5)
  })

  it('ClientCreate_NetworkFailure_ShowsSafeRetryableErrorWithoutFakeClient', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(organizationResponse([]))
      .mockResolvedValueOnce(organizationResponse([organizationB]))
      .mockResolvedValueOnce(clientListResponse([]))
      .mockResolvedValueOnce(response(200, { requestToken: 'test-token' }))
      .mockRejectedValueOnce(new Error('private network detail'))
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(`/organizations/${organizationB.id}/clients`)
    await screen.findByText('Nenhum cliente cadastrado nesta organização.')
    openAndSubmitCreate('Cliente Incerto')

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent(
      'Não foi possível cadastrar o cliente. Verifique os dados e tente novamente.',
    )
    expect(alert).not.toHaveTextContent('private network detail')
    expect(screen.queryByText('Cliente Incerto')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Cadastrar' })).toBeEnabled()
  })

  it('ClientCreate_InvalidName_PreventsWhitespaceAndOverlongRequests', async () => {
    const fetchMock = authenticatedFetch(
      organizationB,
      clientListResponse([]),
    )
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(`/organizations/${organizationB.id}/clients`)
    await screen.findByText('Nenhum cliente cadastrado nesta organização.')
    fireEvent.click(screen.getByRole('button', { name: 'Cadastrar cliente' }))
    const nameInput = screen.getByLabelText('Nome')
    const form = screen.getByRole('button', { name: 'Cadastrar' }).closest('form')!

    fireEvent.change(nameInput, { target: { value: '   ' } })
    fireEvent.submit(form)
    expect(await screen.findByText('Informe o nome do cliente.')).toBeInTheDocument()

    fireEvent.change(nameInput, { target: { value: 'x'.repeat(151) } })
    fireEvent.submit(form)
    expect(
      await screen.findByText('O nome deve ter no máximo 150 caracteres.'),
    ).toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(3)
  })

  it('ClientList_Forbidden_ShowsGenericNoAccessAndSafeNavigation', async () => {
    vi.stubGlobal('fetch', authenticatedFetch(organizationA, response(403)))

    renderRoute(`/organizations/${organizationA.id}/clients`)

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent(
      'Não foi possível acessar os clientes desta organização.',
    )
    expect(
      screen.getByRole('link', { name: 'Voltar para organizações' }),
    ).toHaveAttribute('href', '/organizations')
    expect(screen.queryByText(activeClient.name)).not.toBeInTheDocument()
  })

  it('ClientList_UnexpectedContract_ShowsRecoverableSafeError', async () => {
    vi.stubGlobal(
      'fetch',
      authenticatedFetch(
        organizationA,
        response(200, { items: [{ name: 'private malformed item' }] }),
      ),
    )

    renderRoute(`/organizations/${organizationA.id}/clients`)

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent(
      'Não foi possível carregar os clientes. Tente novamente.',
    )
    expect(alert).not.toHaveTextContent('private malformed item')
    expect(
      screen.getByRole('button', { name: 'Tentar novamente' }),
    ).toBeInTheDocument()
  })

  it('ClientList_NetworkFailure_RetriesDeliberatelyAndRecovers', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(organizationResponse([]))
      .mockResolvedValueOnce(organizationResponse([organizationA]))
      .mockRejectedValueOnce(new Error('private network detail'))
      .mockResolvedValueOnce(clientListResponse([activeClient]))
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(`/organizations/${organizationA.id}/clients`)

    const alert = await screen.findByRole('alert')
    expect(alert).not.toHaveTextContent('private network detail')
    expect(fetchMock).toHaveBeenCalledTimes(3)

    fireEvent.click(screen.getByRole('button', { name: 'Tentar novamente' }))

    expect(await screen.findByText(activeClient.name)).toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(4)
  })

  it('ClientList_Unauthorized_InvalidatesSessionAndRemovesProtectedContent', async () => {
    const fetchMock = authenticatedFetch(organizationA, response(401))
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(`/organizations/${organizationA.id}/clients`)

    expect(
      await screen.findByRole('heading', { name: 'Entrar no ENMA' }),
    ).toBeInTheDocument()
    expect(router.state.location.pathname).toBe('/login')
    expect(screen.queryByRole('heading', { name: 'Clientes' })).not.toBeInTheDocument()
  })

  it('OrganizationSwitcher_FromClientsRoute_NavigatesToSelectedWorkspaceRoot', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(organizationResponse([]))
      .mockResolvedValueOnce(organizationResponse([organizationA, organizationB]))
      .mockResolvedValueOnce(clientListResponse([]))
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(`/organizations/${organizationA.id}/clients`)

    await screen.findByRole('heading', { name: 'Clientes' })
    fireEvent.change(screen.getByLabelText('Organização atual'), {
      target: { value: organizationB.id },
    })

    await waitFor(() => {
      expect(router.state.location.pathname).toBe(
        `/organizations/${organizationB.id}`,
      )
    })
    expect(screen.getByRole('heading', { name: organizationB.name })).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Clientes' })).not.toBeInTheDocument()
  })
})
