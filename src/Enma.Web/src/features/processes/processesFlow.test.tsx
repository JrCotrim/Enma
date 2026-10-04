import { act, fireEvent, render, screen, within } from '@testing-library/react'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

vi.mock('../notifications/NotificationCenter', () => ({
  NotificationCenter: () => null,
}))
import { createAppRoutes } from '../../app/router'
import { clearCsrfToken } from '../authentication/csrfClient'
import type { Client } from '../clients/clientTypes'
import { createEmailVerificationFlow } from '../email-verification/emailVerificationService'
import type { OrganizationNavigationItem } from '../organizations/organizationTypes'
import type {
  ActiveClientLookupItem,
  LegalProcessListItem,
} from './legalProcessTypes'

const organizationA: OrganizationNavigationItem = {
  id: '11111111-1111-4111-8111-111111111111',
  membershipId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa1',
  name: 'Organização Alfa',
  role: 'Owner',
}

const organizationB: OrganizationNavigationItem = {
  id: '22222222-2222-4222-8222-222222222222',
  membershipId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbb2',
  name: 'Organização Beta',
  role: 'Administrator',
}

const memberOrganization: OrganizationNavigationItem = {
  ...organizationA,
  role: 'Member',
}

const legalProcess: LegalProcessListItem = {
  id: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
  title: 'Ação de cobrança',
  clientId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',
  clientName: 'Cliente Exemplo',
  createdAt: '2026-08-12T14:30:00Z',
  processNumber: null,
  status: 'inProgress',
  courtOrAuthority: null,
  responsibleMembershipId: null,
  responsibleDisplayName: null,
}

const lookupClient: ActiveClientLookupItem = {
  id: legalProcess.clientId,
  name: legalProcess.clientName,
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

function processListResponse(
  items: readonly LegalProcessListItem[],
  pageNumber = 1,
  hasNext = false,
): Response {
  return response(200, { items, pageNumber, pageSize: 20, hasNext })
}

function lookupResponse(
  items: readonly ActiveClientLookupItem[],
  pageNumber = 1,
  hasNext = false,
): Response {
  return response(200, { items, pageNumber, pageSize: 20, hasNext })
}

function clientListResponse(items: readonly Client[]): Response {
  return response(200, { items, pageNumber: 1, pageSize: 20 })
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
  organizations: readonly OrganizationNavigationItem[],
  ...scopedResponses: readonly (Response | Promise<Response>)[]
) {
  const fetchMock = vi
    .fn()
    .mockResolvedValueOnce(organizationResponse([]))
    .mockResolvedValueOnce(organizationResponse(organizations))

  for (const scopedResponse of scopedResponses) {
    fetchMock.mockReturnValueOnce(Promise.resolve(scopedResponse))
  }

  return fetchMock
}

async function openCreate() {
  fireEvent.click(screen.getByRole('button', { name: 'Cadastrar processo' }))
  expect(
    await screen.findByRole('heading', { name: 'Novo processo' }),
  ).toBeInTheDocument()
}

function selectClient(client = lookupClient) {
  fireEvent.click(
    screen.getByRole('button', { name: `Selecionar ${client.name}` }),
  )
}

function submitCreate(title: string) {
  fireEvent.change(screen.getByLabelText('Título'), {
    target: { value: title },
  })
  const submit = screen.getByRole('button', { name: 'Cadastrar' })
  const form = submit.closest('form')
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

describe('Processes D1 flow', () => {
  it('ProcessList_MemberRoute_RendersContextualFieldsNavigationAndNoDurableState', async () => {
    const localStorageSpy = vi.spyOn(window.localStorage, 'setItem')
    const sessionStorageSpy = vi.spyOn(window.sessionStorage, 'setItem')
    const fetchMock = authenticatedFetch(
      [memberOrganization],
      processListResponse([legalProcess]),
    )
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(`/organizations/${memberOrganization.id}/processes`)

    expect(
      await screen.findByRole('heading', { name: 'Processos' }),
    ).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Clientes' })).toHaveAttribute(
      'href',
      `/organizations/${memberOrganization.id}/clients`,
    )
    expect(screen.getByRole('link', { name: 'Processos' })).toHaveAttribute(
      'href',
      `/organizations/${memberOrganization.id}/processes`,
    )
    const results = await screen.findByRole('region', {
      name: 'Processos cadastrados',
    })
    const processLink = within(results).getByRole('link', {
      name: new RegExp(legalProcess.title),
    })
    expect(processLink).toHaveAttribute(
      'href',
      `/organizations/${memberOrganization.id}/processes/${legalProcess.id}`,
    )
    expect(within(results).getAllByRole('listitem')).toHaveLength(1)
    expect(within(results).queryByRole('table')).not.toBeInTheDocument()
    expect(screen.getByText(legalProcess.clientName)).toBeInTheDocument()
    expect(screen.getByText(/12\/08\/2026/)).toBeInTheDocument()
    expect(
      screen.queryByRole('button', { name: 'Cadastrar processo' }),
    ).not.toBeInTheDocument()
    expect(fetchMock).toHaveBeenNthCalledWith(
      3,
      `/api/organizations/${memberOrganization.id}/processes?pageNumber=1&pageSize=20`,
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

  it('WorkspaceNavigation_ClientsLink_StillRendersExistingClientRoute', async () => {
    const client: Client = {
      id: lookupClient.id,
      name: lookupClient.name,
      isActive: true,
      createdAt: legalProcess.createdAt,
    }
    vi.stubGlobal(
      'fetch',
      authenticatedFetch(
        [organizationA],
        processListResponse([]),
        clientListResponse([client]),
      ),
    )
    const router = renderRoute(`/organizations/${organizationA.id}/processes`)

    await screen.findByRole('heading', { name: 'Processos' })
    fireEvent.click(screen.getByRole('link', { name: 'Clientes' }))

    expect(
      await screen.findByRole('heading', { name: 'Clientes' }),
    ).toBeInTheDocument()
    expect(screen.getByText(client.name)).toBeInTheDocument()
    expect(router.state.location.pathname).toBe(
      `/organizations/${organizationA.id}/clients`,
    )
  })

  it.each(['Owner', 'Administrator'] as const)(
    'ProcessCreate_%sRole_ShowsActionAndLoadsInitialContextualLookup',
    async (role) => {
      const organization = { ...organizationA, role }
      const fetchMock = authenticatedFetch(
        [organization],
        processListResponse([]),
        lookupResponse([lookupClient]),
      )
      vi.stubGlobal('fetch', fetchMock)

      renderRoute(`/organizations/${organization.id}/processes`)
      await screen.findByText('Nenhum processo cadastrado nesta organização.')
      await openCreate()

      expect(
        await screen.findByRole('button', {
          name: `Selecionar ${lookupClient.name}`,
        }),
      ).toBeInTheDocument()
      expect(fetchMock.mock.calls[3]?.[0]).toBe(
        `/api/organizations/${organization.id}/clients/lookup?search=&pageNumber=1&pageSize=20`,
      )
    },
  )

  it('ProcessList_EmptyMemberPage_ShowsEmptyStateWithoutCreateImplication', async () => {
    vi.stubGlobal(
      'fetch',
      authenticatedFetch([memberOrganization], processListResponse([])),
    )

    renderRoute(`/organizations/${memberOrganization.id}/processes`)

    expect(
      await screen.findByText('Nenhum processo cadastrado nesta organização.'),
    ).toBeInTheDocument()
    expect(screen.queryByText(/cadastrar/i)).not.toBeInTheDocument()
  })

  it('ProcessPagination_UrlAndControls_RequestAuthoritativeBackendPages', async () => {
    const fullPage = Array.from({ length: 20 }, (_, index) => ({
      ...legalProcess,
      id: `aaaaaaaa-aaaa-4aaa-8aaa-${index.toString().padStart(12, '0')}`,
      title: `Processo ${index + 1}`,
    }))
    const fetchMock = authenticatedFetch(
      [memberOrganization],
      processListResponse(fullPage, 2, true),
      processListResponse([], 3),
      processListResponse(fullPage, 2, true),
    )
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(
      `/organizations/${memberOrganization.id}/processes?page=2`,
    )

    expect(await screen.findByText('Processo 1')).toBeInTheDocument()
    expect(fetchMock.mock.calls[2]?.[0]).toContain(
      'processes?pageNumber=2&pageSize=20',
    )
    fireEvent.click(
      screen.getByRole('button', { name: 'Próxima página de processos' }),
    )
    expect(
      await screen.findByText('Nenhum processo encontrado nesta página.'),
    ).toBeInTheDocument()
    expect(router.state.location.search).toBe('?page=3')
    fireEvent.click(
      screen.getByRole('button', { name: 'Página anterior de processos' }),
    )
    expect(await screen.findByText('Processo 1')).toBeInTheDocument()
    expect(router.state.location.search).toBe('?page=2')
  })

  it.each(['0', '-2', '1.5', 'abc', '999999999999999999999999'])(
    'ProcessPagination_InvalidPage%s_NormalizesWithoutInvalidRequest',
    async (invalidPage) => {
      const fetchMock = authenticatedFetch(
        [memberOrganization],
        processListResponse([]),
      )
      vi.stubGlobal('fetch', fetchMock)
      const router = renderRoute(
        `/organizations/${memberOrganization.id}/processes?page=${invalidPage}`,
      )

      await screen.findByText('Nenhum processo cadastrado nesta organização.')
      expect(fetchMock.mock.calls[2]?.[0]).toContain('pageNumber=1')
      expect(router.state.location.search).toBe('')
    },
  )

  it('ProcessList_OldPageResponseCompletesLast_DoesNotReplaceCurrentPage', async () => {
    let resolvePageOne: ((value: Response) => void) | undefined
    const pendingPageOne = new Promise<Response>((resolve) => {
      resolvePageOne = resolve
    })
    const pageOneProcess = { ...legalProcess, title: 'Processo página um' }
    const pageTwoProcess = {
      ...legalProcess,
      id: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc',
      title: 'Processo página dois',
    }
    vi.stubGlobal(
      'fetch',
      authenticatedFetch(
        [memberOrganization],
        pendingPageOne,
        processListResponse([pageTwoProcess], 2),
      ),
    )
    const router = renderRoute(`/organizations/${memberOrganization.id}/processes`)

    await screen.findByText('Carregando processos...')
    await act(async () => {
      await router.navigate(
        `/organizations/${memberOrganization.id}/processes?page=2`,
      )
    })
    expect(await screen.findByText(pageTwoProcess.title)).toBeInTheDocument()

    await act(async () => {
      resolvePageOne?.(processListResponse([pageOneProcess]))
      await pendingPageOne
    })

    expect(screen.getByText(pageTwoProcess.title)).toBeInTheDocument()
    expect(screen.queryByText(pageOneProcess.title)).not.toBeInTheDocument()
  })

  it('ProcessList_OldOrganizationResponseCompletesLast_NeverRendersUnderNewOrganization', async () => {
    let resolveOrganizationA: ((value: Response) => void) | undefined
    const pendingOrganizationA = new Promise<Response>((resolve) => {
      resolveOrganizationA = resolve
    })
    const processA = { ...legalProcess, title: 'Processo da Alfa' }
    const processB = {
      ...legalProcess,
      id: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc',
      title: 'Processo da Beta',
    }
    vi.stubGlobal(
      'fetch',
      authenticatedFetch(
        [organizationA, organizationB],
        pendingOrganizationA,
        processListResponse([processB]),
      ),
    )
    const router = renderRoute(`/organizations/${organizationA.id}/processes`)

    await screen.findByText('Carregando processos...')
    await act(async () => {
      await router.navigate(`/organizations/${organizationB.id}/processes`)
    })
    expect(await screen.findByText(processB.title)).toBeInTheDocument()

    await act(async () => {
      resolveOrganizationA?.(processListResponse([processA]))
      await pendingOrganizationA
    })

    expect(screen.getByText(processB.title)).toBeInTheDocument()
    expect(screen.queryByText(processA.title)).not.toBeInTheDocument()
  })

  it('OrganizationSwitcher_FromProcessesRoute_NavigatesToSelectedWorkspaceRoot', async () => {
    vi.stubGlobal(
      'fetch',
      authenticatedFetch(
        [organizationA, organizationB],
        processListResponse([]),
      ),
    )
    const router = renderRoute(`/organizations/${organizationA.id}/processes`)

    await screen.findByRole('heading', { name: 'Processos' })
    fireEvent.click(screen.getByRole('button', { name: 'Perfil' }))
    fireEvent.click(
      await screen.findByRole('button', {
        name: `Trocar para ${organizationB.name}`,
      }),
    )

    expect(
      await screen.findByRole('heading', {
        name: `Espaço de trabalho: ${organizationB.name}`,
      }),
    ).toBeInTheDocument()
    expect(router.state.location.pathname).toBe(
      `/organizations/${organizationB.id}`,
    )
    expect(
      screen.queryByRole('heading', { name: 'Processos' }),
    ).not.toBeInTheDocument()
  })

  it('ProcessList_ForbiddenAndMalformedResponses_ShowSafeStates', async () => {
    vi.stubGlobal(
      'fetch',
      authenticatedFetch([memberOrganization], response(403)),
    )
    renderRoute(`/organizations/${memberOrganization.id}/processes`)

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Não foi possível acessar os processos desta organização.',
    )
  })

  it('ProcessList_MalformedContract_ShowsGenericRecoverableError', async () => {
    vi.stubGlobal(
      'fetch',
      authenticatedFetch(
        [memberOrganization],
        response(200, { items: [{ title: 'private malformed data' }] }),
      ),
    )

    renderRoute(`/organizations/${memberOrganization.id}/processes`)

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent(genericListErrorForTest)
    expect(alert).not.toHaveTextContent('private malformed data')
  })

  it('ProcessList_Unauthorized_InvalidatesSessionAndRemovesProtectedContent', async () => {
    vi.stubGlobal(
      'fetch',
      authenticatedFetch([memberOrganization], response(401)),
    )
    const router = renderRoute(`/organizations/${memberOrganization.id}/processes`)

    expect(
      await screen.findByRole('heading', { name: 'Entrar no ENMA' }),
    ).toBeInTheDocument()
    expect(router.state.location.pathname).toBe('/login')
    expect(
      screen.queryByRole('heading', { name: 'Processos' }),
    ).not.toBeInTheDocument()
  })

  it('ClientLookup_SearchLoadMoreAndSelection_UsesServerPagesAndDiscardsPriorSearch', async () => {
    const laterClient = {
      id: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc',
      name: 'Cliente Página Dois',
    }
    const searchedClient = {
      id: 'dddddddd-dddd-4ddd-8ddd-dddddddddddd',
      name: 'Cliente Pesquisado',
    }
    const fetchMock = authenticatedFetch(
      [organizationA],
      processListResponse([]),
      lookupResponse([lookupClient], 1, true),
      lookupResponse([laterClient], 2),
      lookupResponse([searchedClient]),
    )
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(`/organizations/${organizationA.id}/processes`)
    await screen.findByText('Nenhum processo cadastrado nesta organização.')
    await openCreate()
    await screen.findByRole('button', { name: `Selecionar ${lookupClient.name}` })
    fireEvent.click(screen.getByRole('button', { name: 'Carregar mais' }))

    expect(
      await screen.findByRole('button', { name: `Selecionar ${laterClient.name}` }),
    ).toBeInTheDocument()
    expect(fetchMock.mock.calls[4]?.[0]).toContain(
      'search=&pageNumber=2&pageSize=20',
    )
    selectClient(laterClient)
    expect(screen.getByText(/Cliente selecionado:/)).toHaveTextContent(
      laterClient.name,
    )

    fireEvent.change(screen.getByLabelText('Buscar cliente'), {
      target: { value: '  Pesquisado & especial  ' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Buscar' }))

    expect(
      await screen.findByRole('button', {
        name: `Selecionar ${searchedClient.name}`,
      }),
    ).toBeInTheDocument()
    expect(
      screen.queryByRole('button', { name: `Selecionar ${lookupClient.name}` }),
    ).not.toBeInTheDocument()
    expect(fetchMock.mock.calls[5]?.[0]).toContain(
      'search=Pesquisado+%26+especial&pageNumber=1&pageSize=20',
    )
    selectClient(searchedClient)
    expect(screen.getByText(/Cliente selecionado:/)).toHaveTextContent(
      searchedClient.name,
    )
  })

  it('ClientLookup_BlankAndNonblankEmptyStates_AreDistinct', async () => {
    vi.stubGlobal(
      'fetch',
      authenticatedFetch(
        [organizationA],
        processListResponse([]),
        lookupResponse([]),
        lookupResponse([]),
      ),
    )

    renderRoute(`/organizations/${organizationA.id}/processes`)
    await screen.findByText('Nenhum processo cadastrado nesta organização.')
    await openCreate()
    expect(
      await screen.findByText(
        'É necessário ter um cliente ativo para cadastrar um processo.',
      ),
    ).toBeInTheDocument()

    fireEvent.change(screen.getByLabelText('Buscar cliente'), {
      target: { value: 'Inexistente' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Buscar' }))

    expect(
      await screen.findByText('Nenhum cliente encontrado para esta busca.'),
    ).toBeInTheDocument()
    expect(
      screen.queryByText(
        'É necessário ter um cliente ativo para cadastrar um processo.',
      ),
    ).not.toBeInTheDocument()
  })

  it('ClientLookup_NetworkFailure_ShowsSafeRetryAndRecoversDeliberately', async () => {
    const fetchMock = authenticatedFetch(
      [organizationA],
      processListResponse([]),
    )
    fetchMock
      .mockRejectedValueOnce(new Error('private lookup network detail'))
      .mockResolvedValueOnce(lookupResponse([lookupClient]))
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(`/organizations/${organizationA.id}/processes`)
    await screen.findByText('Nenhum processo cadastrado nesta organização.')
    await openCreate()

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent(
      'Não foi possível carregar os clientes ativos. Tente novamente.',
    )
    expect(alert).not.toHaveTextContent('private lookup network detail')
    expect(fetchMock).toHaveBeenCalledTimes(4)

    fireEvent.click(screen.getByRole('button', { name: 'Tentar novamente' }))

    expect(
      await screen.findByRole('button', {
        name: `Selecionar ${lookupClient.name}`,
      }),
    ).toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(5)
  })

  it('ClientLookup_OldSearchCompletesLast_DoesNotOverwriteNewSearch', async () => {
    let resolveOldSearch: ((value: Response) => void) | undefined
    const pendingOldSearch = new Promise<Response>((resolve) => {
      resolveOldSearch = resolve
    })
    const oldClient = { ...lookupClient, name: 'Cliente busca antiga' }
    const newClient = {
      id: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc',
      name: 'Cliente busca nova',
    }
    vi.stubGlobal(
      'fetch',
      authenticatedFetch(
        [organizationA],
        processListResponse([]),
        lookupResponse([]),
        pendingOldSearch,
        lookupResponse([newClient]),
      ),
    )

    renderRoute(`/organizations/${organizationA.id}/processes`)
    await screen.findByText('Nenhum processo cadastrado nesta organização.')
    await openCreate()
    await screen.findByText(
      'É necessário ter um cliente ativo para cadastrar um processo.',
    )

    fireEvent.change(screen.getByLabelText('Buscar cliente'), {
      target: { value: 'antiga' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Buscar' }))
    fireEvent.change(screen.getByLabelText('Buscar cliente'), {
      target: { value: 'nova' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Buscar' }))
    expect(
      await screen.findByRole('button', { name: `Selecionar ${newClient.name}` }),
    ).toBeInTheDocument()

    await act(async () => {
      resolveOldSearch?.(lookupResponse([oldClient]))
      await pendingOldSearch
    })

    expect(
      screen.getByRole('button', { name: `Selecionar ${newClient.name}` }),
    ).toBeInTheDocument()
    expect(screen.queryByText(oldClient.name)).not.toBeInTheDocument()
  })

  it('ClientLookup_OldOrganizationCompletesLast_NeverPopulatesNewOrganization', async () => {
    let resolveLookupA: ((value: Response) => void) | undefined
    const pendingLookupA = new Promise<Response>((resolve) => {
      resolveLookupA = resolve
    })
    const clientA = { ...lookupClient, name: 'Cliente da Alfa' }
    const clientB = {
      id: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc',
      name: 'Cliente da Beta',
    }
    vi.stubGlobal(
      'fetch',
      authenticatedFetch(
        [organizationA, organizationB],
        processListResponse([]),
        pendingLookupA,
        processListResponse([]),
        lookupResponse([clientB]),
      ),
    )
    const router = renderRoute(`/organizations/${organizationA.id}/processes`)

    await screen.findByText('Nenhum processo cadastrado nesta organização.')
    await openCreate()
    await screen.findByText('Carregando clientes ativos...')
    await act(async () => {
      await router.navigate(`/organizations/${organizationB.id}/processes`)
    })
    await screen.findByText('Nenhum processo cadastrado nesta organização.')
    await openCreate()
    expect(
      await screen.findByRole('button', { name: `Selecionar ${clientB.name}` }),
    ).toBeInTheDocument()

    await act(async () => {
      resolveLookupA?.(lookupResponse([clientA]))
      await pendingLookupA
    })

    expect(screen.queryByText(clientA.name)).not.toBeInTheDocument()
    expect(
      screen.getByRole('button', { name: `Selecionar ${clientB.name}` }),
    ).toBeInTheDocument()
  })

  it('ClientLookup_LateLoadMoreAfterNewSearch_DoesNotMixResults', async () => {
    let resolveLoadMore: ((value: Response) => void) | undefined
    const pendingLoadMore = new Promise<Response>((resolve) => {
      resolveLoadMore = resolve
    })
    const lateClient = { ...lookupClient, name: 'Cliente página antiga' }
    const searchedClient = {
      id: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc',
      name: 'Cliente busca atual',
    }
    vi.stubGlobal(
      'fetch',
      authenticatedFetch(
        [organizationA],
        processListResponse([]),
        lookupResponse([lookupClient], 1, true),
        pendingLoadMore,
        lookupResponse([searchedClient]),
      ),
    )

    renderRoute(`/organizations/${organizationA.id}/processes`)
    await screen.findByText('Nenhum processo cadastrado nesta organização.')
    await openCreate()
    await screen.findByRole('button', { name: 'Carregar mais' })
    fireEvent.click(screen.getByRole('button', { name: 'Carregar mais' }))
    fireEvent.change(screen.getByLabelText('Buscar cliente'), {
      target: { value: 'atual' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Buscar' }))
    await screen.findByRole('button', { name: `Selecionar ${searchedClient.name}` })

    await act(async () => {
      resolveLoadMore?.(lookupResponse([lateClient], 2))
      await pendingLoadMore
    })

    expect(screen.queryByText(lateClient.name)).not.toBeInTheDocument()
    expect(screen.queryByText(lookupClient.name)).not.toBeInTheDocument()
    expect(
      screen.getByRole('button', { name: `Selecionar ${searchedClient.name}` }),
    ).toBeInTheDocument()
  })

  it('ProcessCreate_Success_SendsExactCsrfBodyWaitsForServerAndRefreshesAuthoritatively', async () => {
    let resolveCreate: ((value: Response) => void) | undefined
    const pendingCreate = new Promise<Response>((resolve) => {
      resolveCreate = resolve
    })
    const createdProcess = {
      ...legalProcess,
      id: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc',
      title: 'Processo Seguro',
    }
    const fetchMock = authenticatedFetch(
      [organizationA],
      processListResponse([]),
      lookupResponse([
        { ...lookupClient, extraPrivateField: 'ignored' } as ActiveClientLookupItem,
      ]),
      response(200, { requestToken: 'transient-test-token' }),
      pendingCreate,
      processListResponse([createdProcess]),
    )
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(`/organizations/${organizationA.id}/processes`)
    await screen.findByText('Nenhum processo cadastrado nesta organização.')
    await openCreate()
    await screen.findByRole('button', { name: `Selecionar ${lookupClient.name}` })
    selectClient()
    const form = submitCreate('  Processo Seguro  ')

    expect(
      await screen.findByRole('button', { name: 'Cadastrando...' }),
    ).toBeDisabled()
    fireEvent.submit(form)
    expect(fetchMock).toHaveBeenCalledTimes(6)
    expect(screen.queryByText(createdProcess.title)).not.toBeInTheDocument()
    const [postUrl, postInit] = fetchMock.mock.calls[5] as [string, RequestInit]
    expect(postUrl).toBe(
      `/api/organizations/${organizationA.id}/processes`,
    )
    const body = JSON.parse(postInit.body as string) as Record<string, unknown>
    expect(body).toEqual({
      clientId: lookupClient.id,
      title: 'Processo Seguro',
    })
    expect(Object.keys(body)).toEqual(['clientId', 'title'])
    expect(body).not.toHaveProperty('organizationId')
    expect(body).not.toHaveProperty('tenantId')
    expect(body).not.toHaveProperty('clientName')
    expect(body).not.toHaveProperty('processId')
    expect(body).not.toHaveProperty('role')
    expect(body).not.toHaveProperty('createdAt')
    expect(body).not.toHaveProperty('status')
    expect(postInit.headers).toEqual({
      'Content-Type': 'application/json',
      'X-CSRF-TOKEN': 'transient-test-token',
    })

    await act(async () => {
      resolveCreate?.(
        response(201, { id: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc' }),
      )
      await pendingCreate
    })

    expect(
      await screen.findByText('Processo cadastrado com sucesso.'),
    ).toBeInTheDocument()
    expect(await screen.findByText(createdProcess.title)).toBeInTheDocument()
    expect(
      screen.queryByRole('heading', { name: 'Novo processo' }),
    ).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Cadastrar processo' }))
    expect(await screen.findByLabelText('Título')).toHaveValue('')
    expect(screen.queryByText(/Cliente selecionado:/)).not.toBeInTheDocument()
  })

  it('ProcessCreate_TitleValidation_RejectsWhitespaceAndOverlongButAllows150Characters', async () => {
    const titleAtLimit = 'x'.repeat(150)
    const fetchMock = authenticatedFetch(
      [organizationA],
      processListResponse([]),
      lookupResponse([lookupClient]),
      response(200, { requestToken: 'test-token' }),
      response(201, { id: legalProcess.id }),
      processListResponse([]),
    )
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(`/organizations/${organizationA.id}/processes`)
    await screen.findByText('Nenhum processo cadastrado nesta organização.')
    await openCreate()
    await screen.findByRole('button', {
      name: `Selecionar ${lookupClient.name}`,
    })
    selectClient()

    submitCreate('   ')
    expect(
      await screen.findByText('Informe o título do processo.'),
    ).toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(4)

    submitCreate('x'.repeat(151))
    expect(
      await screen.findByText('O título deve ter no máximo 150 caracteres.'),
    ).toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(4)

    submitCreate(titleAtLimit)
    await screen.findByText('Processo cadastrado com sucesso.')
    const postInit = fetchMock.mock.calls[5]?.[1] as RequestInit
    expect(JSON.parse(postInit.body as string)).toEqual({
      clientId: lookupClient.id,
      title: titleAtLimit,
    })
  })

  it.each([
    [403, 'Você não tem permissão para cadastrar processos nesta organização.'],
    [404, 'O cliente selecionado não está disponível para este cadastro.'],
    [400, 'Não foi possível validar o cadastro. Verifique os dados e tente novamente.'],
  ] as const)(
    'ProcessCreate_Status%s_ShowsSafeSemanticErrorWithoutRetry',
    async (status, expectedMessage) => {
      const fetchMock = authenticatedFetch(
        [organizationA],
        processListResponse([]),
        lookupResponse([lookupClient]),
        response(200, { requestToken: 'test-token' }),
        response(status, { detail: 'private server reason' }),
      )
      vi.stubGlobal('fetch', fetchMock)

      renderRoute(`/organizations/${organizationA.id}/processes`)
      await screen.findByText('Nenhum processo cadastrado nesta organização.')
      await openCreate()
      await screen.findByRole('button', {
        name: `Selecionar ${lookupClient.name}`,
      })
      selectClient()
      submitCreate('Processo negado')

      const alert = await screen.findByRole('alert')
      expect(alert).toHaveTextContent(expectedMessage)
      expect(alert).not.toHaveTextContent('private server reason')
      expect(fetchMock).toHaveBeenCalledTimes(6)
      expect(screen.queryByText('Processo negado')).not.toBeInTheDocument()
      if (status === 404) {
        expect(screen.queryByText(/Cliente selecionado:/)).not.toBeInTheDocument()
      }
    },
  )

  it('ProcessCreate_NetworkFailure_ShowsRetryableErrorWithoutAutomaticRetry', async () => {
    const fetchMock = authenticatedFetch(
      [organizationA],
      processListResponse([]),
      lookupResponse([lookupClient]),
      response(200, { requestToken: 'test-token' }),
    )
    fetchMock.mockRejectedValueOnce(new Error('private network detail'))
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(`/organizations/${organizationA.id}/processes`)
    await screen.findByText('Nenhum processo cadastrado nesta organização.')
    await openCreate()
    await screen.findByRole('button', {
      name: `Selecionar ${lookupClient.name}`,
    })
    selectClient()
    submitCreate('Processo incerto')

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent(
      'Não foi possível cadastrar o processo. Tente novamente.',
    )
    expect(alert).not.toHaveTextContent('private network detail')
    expect(fetchMock).toHaveBeenCalledTimes(6)
    expect(screen.queryByText('Processo incerto')).not.toBeInTheDocument()
  })

  it('ProcessCreate_LateSuccessAfterOrganizationNavigation_DoesNotAffectNewContext', async () => {
    let resolveCreateA: ((value: Response) => void) | undefined
    const pendingCreateA = new Promise<Response>((resolve) => {
      resolveCreateA = resolve
    })
    const clientB = {
      id: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc',
      name: 'Cliente da Beta',
    }
    const processB = {
      ...legalProcess,
      id: 'dddddddd-dddd-4ddd-8ddd-dddddddddddd',
      title: 'Processo existente da Beta',
    }
    const fetchMock = authenticatedFetch(
      [organizationA, organizationB],
      processListResponse([]),
      lookupResponse([lookupClient]),
      response(200, { requestToken: 'test-token' }),
      pendingCreateA,
      processListResponse([processB]),
      lookupResponse([clientB]),
    )
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(`/organizations/${organizationA.id}/processes`)

    await screen.findByText('Nenhum processo cadastrado nesta organização.')
    await openCreate()
    await screen.findByRole('button', {
      name: `Selecionar ${lookupClient.name}`,
    })
    selectClient()
    submitCreate('Processo tardio da Alfa')
    await screen.findByRole('button', { name: 'Cadastrando...' })

    await act(async () => {
      await router.navigate(`/organizations/${organizationB.id}/processes`)
    })
    expect(await screen.findByText(processB.title)).toBeInTheDocument()
    await openCreate()
    await screen.findByRole('button', { name: `Selecionar ${clientB.name}` })
    selectClient(clientB)

    await act(async () => {
      resolveCreateA?.(response(201, { id: legalProcess.id }))
      await pendingCreateA
    })

    expect(screen.getByText(processB.title)).toBeInTheDocument()
    expect(screen.getByText(/Cliente selecionado:/)).toHaveTextContent(clientB.name)
    expect(screen.queryByText('Processo cadastrado com sucesso.')).not.toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(8)
    expect(router.state.location.pathname).toBe(
      `/organizations/${organizationB.id}/processes`,
    )
  })
})

const genericListErrorForTest =
  'Não foi possível carregar os processos. Tente novamente.'

const responsibleMember = {
  id: 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee',
  displayName: 'Bruna Costa',
}

function memberLookupResponse(
  items: readonly { readonly id: string; readonly displayName: string }[],
): Response {
  return response(200, { items, pageNumber: 1, pageSize: 20, hasNext: false })
}

function processesUrl(organizationId: string, query: string): string {
  return `/api/organizations/${organizationId}/processes?${query}`
}

function memberLookupUrl(organizationId: string): string {
  return `/api/organizations/${organizationId}/members/lookup?search=&pageNumber=1&pageSize=20`
}

function requestedUrls(fetchMock: ReturnType<typeof vi.fn>): string[] {
  return fetchMock.mock.calls.map((call) => String(call[0]))
}

function filters() {
  return screen.getByRole('group', { name: 'Filtros de processos' })
}

function createForm(): HTMLFormElement {
  const form = screen.getByRole('button', { name: 'Cadastrar' }).closest('form')
  expect(form).not.toBeNull()
  return form!
}

async function openCreateWithSelectedClient() {
  await openCreate()
  await screen.findByRole('button', { name: `Selecionar ${lookupClient.name}` })
  selectClient()
}

describe('Processes operational list and create (Phase 9B.2D-b)', () => {
  it('ProcessList_OperationalMetadata_RendersNumberResponsibleDateAndStatusBadgeLast', async () => {
    const numberedProcess: LegalProcessListItem = {
      ...legalProcess,
      processNumber: '0001234-56.2026.8.26.0100',
      status: 'suspended',
      responsibleMembershipId: responsibleMember.id,
      responsibleDisplayName: responsibleMember.displayName,
    }
    const unnumberedProcess: LegalProcessListItem = {
      ...legalProcess,
      id: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc',
      title: 'Processo sem número',
      status: 'closed',
    }
    vi.stubGlobal(
      'fetch',
      authenticatedFetch(
        [memberOrganization],
        processListResponse([numberedProcess, unnumberedProcess]),
      ),
    )

    renderRoute(`/organizations/${memberOrganization.id}/processes`)

    const results = await screen.findByRole('region', {
      name: 'Processos cadastrados',
    })
    const [numberedItem, unnumberedItem] = within(results).getAllByRole('listitem')
    const numberedMetadata = numberedItem!.querySelector('.process-record-metadata')!
    expect(
      [...numberedMetadata.children].map((child) => child.textContent),
    ).toEqual([
      numberedProcess.clientName,
      numberedProcess.processNumber,
      responsibleMember.displayName,
      expect.stringMatching(/12\/08\/2026/),
      'Suspenso',
    ])
    expect(numberedMetadata.lastElementChild).toHaveClass(
      'process-status',
      'is-pending',
    )
    expect(
      within(numberedItem!).getByRole('link', {
        name: new RegExp(numberedProcess.title),
      }),
    ).toHaveAccessibleDescription(/0001234-56\.2026\.8\.26\.0100.*Bruna Costa.*Suspenso/)

    const unnumberedMetadata = unnumberedItem!.querySelector('.process-record-metadata')!
    expect(unnumberedMetadata.querySelector('.process-record-number')).toBeNull()
    expect(within(unnumberedItem!).getByText('Sem responsável')).toBeInTheDocument()
    expect(unnumberedMetadata.lastElementChild).toHaveTextContent('Encerrado')
    expect(unnumberedMetadata.lastElementChild).toHaveClass(
      'process-status',
      'is-inactive',
    )
  })

  it('ProcessFilters_EachFilter_BuildsQuerySyncsUrlAndReturnsToFirstPage', async () => {
    const fetchMock = authenticatedFetch(
      [memberOrganization],
      processListResponse([legalProcess], 2),
      processListResponse([legalProcess], 1, true),
      processListResponse([legalProcess], 2),
      processListResponse([legalProcess]),
      processListResponse([legalProcess]),
      processListResponse([legalProcess]),
    )
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(
      `/organizations/${memberOrganization.id}/processes?page=2`,
    )
    await screen.findByText(legalProcess.title)

    fireEvent.change(within(filters()).getByLabelText(/Buscar por título/), {
      target: { value: '  Cobrança  ' },
    })
    fireEvent.click(within(filters()).getByRole('button', { name: 'Buscar processos' }))
    await vi.waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(4))
    expect(router.state.location.search).toBe('?search=Cobran%C3%A7a')
    expect(fetchMock.mock.calls[3]?.[0]).toBe(
      processesUrl(
        memberOrganization.id,
        'pageNumber=1&pageSize=20&search=Cobran%C3%A7a',
      ),
    )

    await screen.findByText(legalProcess.title)
    fireEvent.click(
      screen.getByRole('button', { name: 'Próxima página de processos' }),
    )
    await vi.waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(5))
    expect(router.state.location.search).toBe('?search=Cobran%C3%A7a&page=2')

    await screen.findByText(legalProcess.title)
    fireEvent.change(within(filters()).getByLabelText('Status'), {
      target: { value: 'suspended' },
    })
    await vi.waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(6))
    expect(router.state.location.search).toBe(
      '?search=Cobran%C3%A7a&status=suspended',
    )
    expect(fetchMock.mock.calls[5]?.[0]).toBe(
      processesUrl(
        memberOrganization.id,
        'pageNumber=1&pageSize=20&search=Cobran%C3%A7a&status=suspended',
      ),
    )

    await screen.findByText(legalProcess.title)
    fireEvent.change(within(filters()).getByLabelText('Responsável'), {
      target: { value: 'self' },
    })
    await vi.waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(7))
    expect(router.state.location.search).toBe(
      '?search=Cobran%C3%A7a&status=suspended&responsible=self',
    )
    expect(fetchMock.mock.calls[6]?.[0]).toBe(
      processesUrl(
        memberOrganization.id,
        'pageNumber=1&pageSize=20&search=Cobran%C3%A7a&status=suspended&responsible=self',
      ),
    )

    await screen.findByText(legalProcess.title)
    fireEvent.change(within(filters()).getByLabelText('Responsável'), {
      target: { value: 'unassigned' },
    })
    await vi.waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(8))
    expect(fetchMock.mock.calls[7]?.[0]).toBe(
      processesUrl(
        memberOrganization.id,
        'pageNumber=1&pageSize=20&search=Cobran%C3%A7a&status=suspended&responsible=unassigned',
      ),
    )
    expect(requestedUrls(fetchMock).some((url) => url.includes('/members/lookup'))).toBe(false)
  })

  it('ProcessFilters_SpecificPerson_LoadsMembersOnlyWhenChosenAndFiltersById', async () => {
    const fetchMock = authenticatedFetch(
      [memberOrganization],
      processListResponse([legalProcess]),
      memberLookupResponse([responsibleMember]),
      processListResponse([legalProcess]),
    )
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(`/organizations/${memberOrganization.id}/processes`)
    await screen.findByText(legalProcess.title)
    expect(fetchMock).toHaveBeenCalledTimes(3)

    fireEvent.change(within(filters()).getByLabelText('Responsável'), {
      target: { value: 'specific' },
    })
    fireEvent.click(
      await screen.findByRole('button', { name: responsibleMember.displayName }),
    )

    expect(fetchMock.mock.calls[3]?.[0]).toBe(memberLookupUrl(memberOrganization.id))
    await vi.waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(5))
    expect(router.state.location.search).toBe(`?responsible=${responsibleMember.id}`)
    expect(fetchMock.mock.calls[4]?.[0]).toBe(
      processesUrl(
        memberOrganization.id,
        `pageNumber=1&pageSize=20&responsible=${responsibleMember.id}`,
      ),
    )
    expect(within(filters()).getByLabelText('Responsável')).toHaveValue('specific')
    expect(within(filters()).getByText(responsibleMember.displayName)).toBeInTheDocument()
  })

  it('ProcessFilters_UrlState_RestoresControlsAndRequestsFilteredList', async () => {
    const fetchMock = authenticatedFetch(
      [memberOrganization],
      processListResponse([legalProcess]),
    )
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(
      `/organizations/${memberOrganization.id}/processes?search=A%C3%A7%C3%A3o&status=closed&responsible=unassigned`,
    )
    await screen.findByText(legalProcess.title)

    expect(fetchMock.mock.calls[2]?.[0]).toBe(
      processesUrl(
        memberOrganization.id,
        'pageNumber=1&pageSize=20&search=A%C3%A7%C3%A3o&status=closed&responsible=unassigned',
      ),
    )
    expect(within(filters()).getByLabelText(/Buscar por título/)).toHaveValue('Ação')
    expect(within(filters()).getByLabelText('Status')).toHaveValue('closed')
    expect(within(filters()).getByLabelText('Responsável')).toHaveValue('unassigned')
    expect(
      within(filters()).getByRole('button', { name: 'Limpar busca' }),
    ).toBeInTheDocument()
  })

  it('ProcessFilters_InvalidUrlValues_NormalizeToDefaultRequest', async () => {
    const fetchMock = authenticatedFetch(
      [memberOrganization],
      processListResponse([legalProcess]),
    )
    vi.stubGlobal('fetch', fetchMock)

    const router = renderRoute(
      `/organizations/${memberOrganization.id}/processes?search=%20%20&status=archived&responsible=someone`,
    )
    await screen.findByText(legalProcess.title)

    expect(fetchMock.mock.calls[2]?.[0]).toBe(
      processesUrl(memberOrganization.id, 'pageNumber=1&pageSize=20'),
    )
    await vi.waitFor(() => expect(router.state.location.search).toBe(''))
    expect(fetchMock).toHaveBeenCalledTimes(3)
  })

  it('ProcessList_FilteredEmpty_IsDistinctFromUnfilteredEmptyAndClearsFilters', async () => {
    const fetchMock = authenticatedFetch(
      [memberOrganization],
      processListResponse([]),
      processListResponse([]),
    )
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(
      `/organizations/${memberOrganization.id}/processes?status=closed&responsible=self`,
    )

    expect(
      await screen.findByText('Nenhum processo encontrado com estes filtros.'),
    ).toBeInTheDocument()
    expect(
      screen.queryByText('Nenhum processo cadastrado nesta organização.'),
    ).not.toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'Limpar filtros' }))

    expect(
      await screen.findByText('Nenhum processo cadastrado nesta organização.'),
    ).toBeInTheDocument()
    expect(router.state.location.search).toBe('')
    expect(fetchMock.mock.calls[3]?.[0]).toBe(
      processesUrl(memberOrganization.id, 'pageNumber=1&pageSize=20'),
    )
    expect(
      screen.queryByText('Nenhum processo encontrado com estes filtros.'),
    ).not.toBeInTheDocument()
  })

  it('ProcessList_MemberRole_SeesFiltersButNoCreateAction', async () => {
    vi.stubGlobal(
      'fetch',
      authenticatedFetch([memberOrganization], processListResponse([legalProcess])),
    )

    renderRoute(`/organizations/${memberOrganization.id}/processes`)
    await screen.findByText(legalProcess.title)

    expect(within(filters()).getByLabelText(/Buscar por título/)).toBeInTheDocument()
    expect(within(filters()).getByLabelText('Status')).toBeInTheDocument()
    expect(within(filters()).getByLabelText('Responsável')).toBeInTheDocument()
    expect(
      screen.queryByRole('button', { name: /Cadastrar/ }),
    ).not.toBeInTheDocument()
  })

  it.each([
    { label: 'FullPageWithoutNext', count: 20, hasNext: false, disabled: true },
    { label: 'PartialPageWithNext', count: 1, hasNext: true, disabled: false },
  ])(
    'ProcessPagination_HasNextControlsNextButton_$label',
    async ({ count, hasNext, disabled }) => {
      const items = Array.from({ length: count }, (_, index) => ({
        ...legalProcess,
        id: `aaaaaaaa-aaaa-4aaa-8aaa-${index.toString().padStart(12, '0')}`,
        title: `Processo ${index + 1}`,
      }))
      vi.stubGlobal(
        'fetch',
        authenticatedFetch(
          [memberOrganization],
          processListResponse(items, 1, hasNext),
        ),
      )

      renderRoute(`/organizations/${memberOrganization.id}/processes`)
      await screen.findByText('Processo 1')

      const next = screen.getByRole('button', {
        name: 'Próxima página de processos',
      })
      if (disabled) {
        expect(next).toBeDisabled()
      } else {
        expect(next).toBeEnabled()
      }
    },
  )

  it('ProcessList_OldFilterResponseCompletesLast_DoesNotReplaceCurrentFilter', async () => {
    let resolveUnfiltered: ((value: Response) => void) | undefined
    const pendingUnfiltered = new Promise<Response>((resolve) => {
      resolveUnfiltered = resolve
    })
    const staleProcess = { ...legalProcess, title: 'Processo sem filtro' }
    const filteredProcess = {
      ...legalProcess,
      id: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc',
      title: 'Processo filtrado',
    }
    vi.stubGlobal(
      'fetch',
      authenticatedFetch(
        [memberOrganization],
        pendingUnfiltered,
        processListResponse([filteredProcess]),
      ),
    )
    const router = renderRoute(`/organizations/${memberOrganization.id}/processes`)

    await screen.findByText('Carregando processos...')
    await act(async () => {
      await router.navigate(
        `/organizations/${memberOrganization.id}/processes?status=suspended`,
      )
    })
    expect(await screen.findByText(filteredProcess.title)).toBeInTheDocument()

    await act(async () => {
      resolveUnfiltered?.(processListResponse([staleProcess]))
      await pendingUnfiltered
    })

    expect(screen.getByText(filteredProcess.title)).toBeInTheDocument()
    expect(screen.queryByText(staleProcess.title)).not.toBeInTheDocument()
  })

  it('ProcessCreate_BlankOperationalFields_SendsBodyIdenticalToTitleOnlyCreate', async () => {
    const fetchMock = authenticatedFetch(
      [organizationA],
      processListResponse([]),
      lookupResponse([lookupClient]),
      response(200, { requestToken: 'test-token' }),
      response(201, { id: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc' }),
      processListResponse([]),
    )
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(`/organizations/${organizationA.id}/processes`)
    await screen.findByText('Nenhum processo cadastrado nesta organização.')
    await openCreateWithSelectedClient()
    fireEvent.change(within(createForm()).getByLabelText('Número do processo'), {
      target: { value: '   ' },
    })
    fireEvent.change(within(createForm()).getByLabelText('Órgão/tribunal'), {
      target: { value: '  ' },
    })
    expect(within(createForm()).getByLabelText('Status')).toHaveValue('inProgress')
    expect(within(createForm()).getByLabelText('Responsável')).toHaveValue('unassigned')
    submitCreate('Processo simples')

    expect(
      await screen.findByText('Processo cadastrado com sucesso.'),
    ).toBeInTheDocument()
    const body = JSON.parse(
      (fetchMock.mock.calls[5]?.[1] as RequestInit).body as string,
    ) as Record<string, unknown>
    expect(Object.keys(body)).toEqual(['clientId', 'title'])
    expect(body).toEqual({ clientId: lookupClient.id, title: 'Processo simples' })
  })

  it('ProcessCreate_OperationalFields_SendsTrimmedValuesAndOwnMembershipForSelf', async () => {
    const fetchMock = authenticatedFetch(
      [organizationA],
      processListResponse([]),
      lookupResponse([lookupClient]),
      response(200, { requestToken: 'test-token' }),
      response(201, { id: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc' }),
      processListResponse([]),
    )
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(`/organizations/${organizationA.id}/processes`)
    await screen.findByText('Nenhum processo cadastrado nesta organização.')
    await openCreateWithSelectedClient()
    const form = createForm()
    expect(within(form).getByLabelText('Número do processo')).toHaveAttribute(
      'maxLength',
      '100',
    )
    expect(within(form).getByLabelText('Órgão/tribunal')).toHaveAttribute(
      'maxLength',
      '200',
    )
    fireEvent.change(within(form).getByLabelText('Número do processo'), {
      target: { value: '  0001234-56.2026.8.26.0100  ' },
    })
    fireEvent.change(within(form).getByLabelText('Órgão/tribunal'), {
      target: { value: ' Tribunal de Justiça ' },
    })
    fireEvent.change(within(form).getByLabelText('Status'), {
      target: { value: 'suspended' },
    })
    fireEvent.change(within(form).getByLabelText('Responsável'), {
      target: { value: 'self' },
    })
    submitCreate('Processo operacional')

    expect(
      await screen.findByText('Processo cadastrado com sucesso.'),
    ).toBeInTheDocument()
    const body = JSON.parse(
      (fetchMock.mock.calls[5]?.[1] as RequestInit).body as string,
    ) as Record<string, unknown>
    expect(body).toEqual({
      clientId: lookupClient.id,
      title: 'Processo operacional',
      processNumber: '0001234-56.2026.8.26.0100',
      status: 'suspended',
      courtOrAuthority: 'Tribunal de Justiça',
      responsibleMembershipId: organizationA.membershipId,
    })
    expect(requestedUrls(fetchMock).some((url) => url.includes('/members/lookup'))).toBe(false)
  })

  it('ProcessCreate_OtherPerson_LoadsPickerOnlyWhenChosenAndSendsSelectedMember', async () => {
    const fetchMock = authenticatedFetch(
      [organizationA],
      processListResponse([]),
      lookupResponse([lookupClient]),
      memberLookupResponse([responsibleMember]),
      response(200, { requestToken: 'test-token' }),
      response(201, { id: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc' }),
      processListResponse([]),
    )
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(`/organizations/${organizationA.id}/processes`)
    await screen.findByText('Nenhum processo cadastrado nesta organização.')
    await openCreateWithSelectedClient()
    expect(fetchMock).toHaveBeenCalledTimes(4)
    expect(
      screen.queryByLabelText('Buscar responsável para processo'),
    ).not.toBeInTheDocument()

    fireEvent.change(within(createForm()).getByLabelText('Responsável'), {
      target: { value: 'other' },
    })
    const memberButton = await screen.findByRole('button', {
      name: responsibleMember.displayName,
    })
    expect(fetchMock.mock.calls[4]?.[0]).toBe(memberLookupUrl(organizationA.id))

    submitCreate('Processo delegado')
    expect(
      await screen.findByText('Selecione uma pessoa responsável.'),
    ).toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(5)

    fireEvent.click(memberButton)
    expect(screen.getByText(/Responsável selecionado:/)).toHaveTextContent(
      responsibleMember.displayName,
    )
    fireEvent.submit(createForm())

    expect(
      await screen.findByText('Processo cadastrado com sucesso.'),
    ).toBeInTheDocument()
    const body = JSON.parse(
      (fetchMock.mock.calls[6]?.[1] as RequestInit).body as string,
    ) as Record<string, unknown>
    expect(body).toEqual({
      clientId: lookupClient.id,
      title: 'Processo delegado',
      responsibleMembershipId: responsibleMember.id,
    })
  })

  it('ProcessCreate_DuplicateNumberConflict_ShowsErrorOnNumberField', async () => {
    const fetchMock = authenticatedFetch(
      [organizationA],
      processListResponse([]),
      lookupResponse([lookupClient]),
      response(200, { requestToken: 'test-token' }),
      response(409, { title: 'private conflict detail' }),
    )
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(`/organizations/${organizationA.id}/processes`)
    await screen.findByText('Nenhum processo cadastrado nesta organização.')
    await openCreateWithSelectedClient()
    fireEvent.change(within(createForm()).getByLabelText('Número do processo'), {
      target: { value: '0001234-56.2026.8.26.0100' },
    })
    submitCreate('Processo duplicado')

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent(
      'Já existe um processo com este número nesta organização.',
    )
    expect(alert).not.toHaveTextContent('private conflict detail')
    const numberInput = within(createForm()).getByLabelText('Número do processo')
    expect(numberInput).toHaveAttribute('aria-invalid', 'true')
    expect(numberInput).toHaveAccessibleDescription(
      'Já existe um processo com este número nesta organização.',
    )
    expect(numberInput).toHaveValue('0001234-56.2026.8.26.0100')
    expect(
      screen.queryByText('Não foi possível cadastrar o processo. Tente novamente.'),
    ).not.toBeInTheDocument()
  })

  it('ProcessCreate_ResponsibleUnavailable_ClearsSelectionAndAsksForAnotherPerson', async () => {
    const fetchMock = authenticatedFetch(
      [organizationA],
      processListResponse([]),
      lookupResponse([lookupClient]),
      memberLookupResponse([responsibleMember]),
      response(200, { requestToken: 'test-token' }),
      response(400, {
        title: 'Related responsible member unavailable',
        code: 'related_responsible_unavailable',
      }),
    )
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(`/organizations/${organizationA.id}/processes`)
    await screen.findByText('Nenhum processo cadastrado nesta organização.')
    await openCreateWithSelectedClient()
    fireEvent.change(within(createForm()).getByLabelText('Responsável'), {
      target: { value: 'other' },
    })
    fireEvent.click(
      await screen.findByRole('button', { name: responsibleMember.displayName }),
    )
    submitCreate('Processo com responsável removido')

    expect(
      await screen.findByText(
        'O responsável selecionado não está mais disponível. Escolha outra pessoa.',
      ),
    ).toBeInTheDocument()
    expect(screen.queryByText(/Responsável selecionado:/)).not.toBeInTheDocument()
    expect(
      screen.getByRole('button', { name: responsibleMember.displayName }),
    ).toHaveAttribute('aria-pressed', 'false')
    expect(fetchMock).toHaveBeenCalledTimes(7)
  })
})
