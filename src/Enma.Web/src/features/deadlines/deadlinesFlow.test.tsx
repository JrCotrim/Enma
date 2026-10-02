import { act, fireEvent, render, screen, within } from '@testing-library/react'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

vi.mock('../notifications/NotificationCenter', () => ({
  NotificationCenter: () => null,
}))
import { createAppRoutes } from '../../app/router'
import { clearCsrfToken } from '../authentication/csrfClient'
import { createEmailVerificationFlow } from '../email-verification/emailVerificationService'
import type { OrganizationNavigationItem } from '../organizations/organizationTypes'
import { formatLegalDeadlineDueDate } from './legalDeadlineFormatting'
import type {
  LegalDeadlineListItem,
  LegalProcessLookupItem,
} from './legalDeadlineTypes'

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

const pendingDeadline: LegalDeadlineListItem = {
  id: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
  title: 'Apresentar contestação',
  dueDate: '2026-11-01',
  processId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',
  processTitle: 'Ação de cobrança',
  clientName: 'Cliente Exemplo',
  state: 'Pending',
  responsibleMembershipId: null,
  responsibleDisplayName: null,
}

const completedDeadline: LegalDeadlineListItem = {
  ...pendingDeadline,
  id: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc',
  title: 'Protocolar recurso',
  dueDate: '2028-02-29',
  state: 'Completed',
}

const lookupProcess: LegalProcessLookupItem = {
  id: pendingDeadline.processId,
  title: pendingDeadline.processTitle,
  clientName: pendingDeadline.clientName,
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

function deadlineListResponse(
  items: readonly LegalDeadlineListItem[],
  pageNumber = 1,
): Response {
  return response(200, { items, pageNumber, pageSize: 20 })
}

function lookupResponse(
  items: readonly LegalProcessLookupItem[],
  pageNumber = 1,
  hasNext = false,
): Response {
  return response(200, { items, pageNumber, pageSize: 20, hasNext })
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

function renderRoute(path: string) {
  const router = createMemoryRouter(
    createAppRoutes(createEmailVerificationFlow(undefined)),
    { initialEntries: [path] },
  )

  render(<RouterProvider router={router} />)
  return router
}

async function openCreate() {
  fireEvent.click(screen.getByRole('button', { name: 'Cadastrar prazo' }))
  expect(
    await screen.findByRole('heading', { name: 'Novo prazo' }),
  ).toBeInTheDocument()
}

function selectProcess(process = lookupProcess) {
  fireEvent.click(
    screen.getByRole('button', { name: new RegExp(process.title) }),
  )
}

function submitCreate(title: string, dueDate: string) {
  fireEvent.change(screen.getByLabelText('Título'), {
    target: { value: title },
  })
  fireEvent.change(screen.getByLabelText('Data do prazo'), {
    target: { value: dueDate },
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

describe('Deadlines D1 flow', () => {
  it('DeadlineRoute_Member_RendersNavigationContextualListAndDateOnlyValues', async () => {
    const localStorageSpy = vi.spyOn(window.localStorage, 'setItem')
    const sessionStorageSpy = vi.spyOn(window.sessionStorage, 'setItem')
    const fetchMock = authenticatedFetch(
      [memberOrganization],
      deadlineListResponse([pendingDeadline, completedDeadline]),
    )
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(`/organizations/${memberOrganization.id}/deadlines`)

    expect(
      await screen.findByRole('heading', { name: 'Prazos' }),
    ).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Clientes' })).toHaveAttribute(
      'href',
      `/organizations/${memberOrganization.id}/clients`,
    )
    expect(screen.getByRole('link', { name: 'Processos' })).toHaveAttribute(
      'href',
      `/organizations/${memberOrganization.id}/processes`,
    )
    expect(screen.getByRole('link', { name: 'Prazos' })).toHaveAttribute(
      'href',
      `/organizations/${memberOrganization.id}/deadlines`,
    )
    expect(await screen.findByText(pendingDeadline.title)).toBeInTheDocument()
    expect(
      screen.getByRole('heading', { name: 'Prazos cadastrados' }),
    ).toBeInTheDocument()
    expect(
      screen.getByRole('link', { name: pendingDeadline.title }),
    ).toHaveAttribute(
      'href',
      `/organizations/${memberOrganization.id}/deadlines/${pendingDeadline.id}`,
    )
    expect(screen.getAllByText(pendingDeadline.processTitle)).toHaveLength(2)
    expect(screen.getAllByText(pendingDeadline.clientName)).toHaveLength(2)
    expect(screen.getByText('01/11/2026')).toBeInTheDocument()
    expect(screen.getByText('01/11/2026').closest('time')).toHaveAttribute(
      'datetime',
      pendingDeadline.dueDate,
    )
    expect(screen.getByText('29/02/2028')).toBeInTheDocument()
    expect(screen.getByText('Pendente')).toBeInTheDocument()
    expect(screen.getByText('Concluído')).toBeInTheDocument()
    expect(
      screen.queryByRole('button', { name: 'Cadastrar prazo' }),
    ).not.toBeInTheDocument()
    expect(fetchMock).toHaveBeenNthCalledWith(
      3,
      `/api/organizations/${memberOrganization.id}/deadlines?pageNumber=1&pageSize=20`,
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
    'DeadlineCreate_%sRole_IsAvailable',
    async (role) => {
      vi.stubGlobal(
        'fetch',
        authenticatedFetch(
          [{ ...organizationA, role }],
          deadlineListResponse([]),
        ),
      )

      renderRoute(`/organizations/${organizationA.id}/deadlines`)

      expect(
        await screen.findByRole('button', { name: 'Cadastrar prazo' }),
      ).toBeInTheDocument()
    },
  )

  it('DeadlineList_EmptyFirstPage_ShowsClearState', async () => {
    vi.stubGlobal(
      'fetch',
      authenticatedFetch([memberOrganization], deadlineListResponse([])),
    )

    renderRoute(`/organizations/${memberOrganization.id}/deadlines`)

    expect(
      await screen.findByText('Nenhum prazo cadastrado nesta organização.'),
    ).toBeInTheDocument()
  })

  it.each([
    [403, 'Não foi possível acessar os prazos desta organização.'],
    [500, 'Não foi possível carregar os prazos. Tente novamente.'],
  ] as const)(
    'DeadlineList_Status%s_ShowsSafeState',
    async (status, expectedMessage) => {
      vi.stubGlobal(
        'fetch',
        authenticatedFetch([memberOrganization], response(status)),
      )

      renderRoute(`/organizations/${memberOrganization.id}/deadlines`)

      expect(await screen.findByRole('alert')).toHaveTextContent(expectedMessage)
    },
  )

  it('DeadlineList_MalformedDateOrState_FailsSafely', async () => {
    vi.stubGlobal(
      'fetch',
      authenticatedFetch(
        [memberOrganization],
        response(200, {
          items: [{ ...pendingDeadline, dueDate: '2026-02-30', state: 'Overdue' }],
          pageNumber: 1,
          pageSize: 20,
        }),
      ),
    )

    renderRoute(`/organizations/${memberOrganization.id}/deadlines`)

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Não foi possível carregar os prazos. Tente novamente.',
    )
    expect(screen.queryByText('Overdue')).not.toBeInTheDocument()
  })

  it('DeadlineList_Unauthorized_InvalidatesSession', async () => {
    vi.stubGlobal(
      'fetch',
      authenticatedFetch([memberOrganization], response(401)),
    )
    const router = renderRoute(
      `/organizations/${memberOrganization.id}/deadlines`,
    )

    expect(
      await screen.findByRole('heading', { name: 'Entrar no ENMA' }),
    ).toBeInTheDocument()
    expect(router.state.location.pathname).toBe('/login')
  })

  it('DeadlinePagination_PageTwoAndControls_UseUrlAndFixedPageSize', async () => {
    const twentyItems = Array.from({ length: 20 }, (_, index) => ({
      ...pendingDeadline,
      id: `${index + 1}`,
      title: `Prazo ${index + 1}`,
    }))
    const fetchMock = authenticatedFetch(
      [memberOrganization],
      deadlineListResponse(twentyItems, 2),
      deadlineListResponse([], 1),
    )
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(
      `/organizations/${memberOrganization.id}/deadlines?page=2`,
    )

    expect(await screen.findByText('Página 2')).toBeInTheDocument()
    expect(fetchMock.mock.calls[2]?.[0]).toContain(
      '/deadlines?pageNumber=2&pageSize=20',
    )
    expect(
      screen.getByRole('button', { name: 'Próxima página de prazos' }),
    ).toBeEnabled()
    fireEvent.click(
      screen.getByRole('button', { name: 'Página anterior de prazos' }),
    )
    expect(await screen.findByText('Página 1')).toBeInTheDocument()
    expect(router.state.location.search).toBe('')
  })

  it.each(['0', '-1', '1.5', 'abc', '999999999999999999999'])(
    'DeadlinePagination_InvalidPage%s_NormalizesToOne',
    async (invalidPage) => {
      const fetchMock = authenticatedFetch(
        [memberOrganization],
        deadlineListResponse([]),
      )
      vi.stubGlobal('fetch', fetchMock)
      const router = renderRoute(
        `/organizations/${memberOrganization.id}/deadlines?page=${invalidPage}`,
      )

      expect(await screen.findByText('Página 1')).toBeInTheDocument()
      expect(fetchMock.mock.calls[2]?.[0]).toContain(
        '/deadlines?pageNumber=1&pageSize=20',
      )
      expect(router.state.location.search).toBe('')
    },
  )

  it('DeadlineList_OldPageCompletesLast_DoesNotOverwriteCurrentPage', async () => {
    let resolvePageOne: ((value: Response) => void) | undefined
    const pendingPageOne = new Promise<Response>((resolve) => {
      resolvePageOne = resolve
    })
    const pageTwoDeadline = { ...pendingDeadline, title: 'Prazo da página dois' }
    vi.stubGlobal(
      'fetch',
      authenticatedFetch(
        [memberOrganization],
        pendingPageOne,
        deadlineListResponse([pageTwoDeadline], 2),
      ),
    )
    const router = renderRoute(
      `/organizations/${memberOrganization.id}/deadlines`,
    )

    await screen.findByRole('heading', { name: 'Prazos' })
    await act(async () => {
      await router.navigate(
        `/organizations/${memberOrganization.id}/deadlines?page=2`,
      )
    })
    expect(await screen.findByText(pageTwoDeadline.title)).toBeInTheDocument()

    await act(async () => {
      resolvePageOne?.(deadlineListResponse([pendingDeadline]))
      await pendingPageOne
    })

    expect(screen.getByText(pageTwoDeadline.title)).toBeInTheDocument()
    expect(screen.queryByText(pendingDeadline.title)).not.toBeInTheDocument()
  })

  it('DeadlineList_OldOrganizationCompletesLast_NeverRendersUnderNewOrganization', async () => {
    let resolveOrganizationA: ((value: Response) => void) | undefined
    const pendingOrganizationA = new Promise<Response>((resolve) => {
      resolveOrganizationA = resolve
    })
    const deadlineB = { ...pendingDeadline, title: 'Prazo exclusivo da Beta' }
    vi.stubGlobal(
      'fetch',
      authenticatedFetch(
        [organizationA, organizationB],
        pendingOrganizationA,
        deadlineListResponse([deadlineB]),
      ),
    )
    const router = renderRoute(`/organizations/${organizationA.id}/deadlines`)

    await screen.findByRole('heading', { name: 'Prazos' })
    await act(async () => {
      await router.navigate(`/organizations/${organizationB.id}/deadlines`)
    })
    expect(await screen.findByText(deadlineB.title)).toBeInTheDocument()

    await act(async () => {
      resolveOrganizationA?.(deadlineListResponse([pendingDeadline]))
      await pendingOrganizationA
    })

    expect(screen.getByText(deadlineB.title)).toBeInTheDocument()
    expect(screen.queryByText(pendingDeadline.title)).not.toBeInTheDocument()
  })

  it('DateOnlyFormatter_ValidCalendarDates_FormatsWithoutInstantConversion', () => {
    expect(formatLegalDeadlineDueDate('2026-11-01')).toBe('01/11/2026')
    expect(formatLegalDeadlineDueDate('2028-02-29')).toBe('29/02/2028')
    expect(() => formatLegalDeadlineDueDate('2027-02-29')).toThrow()
  })

  it('ProcessLookup_SearchLoadMoreDedupAndSelection_UsesLookupEndpoint', async () => {
    const laterProcess = {
      id: 'dddddddd-dddd-4ddd-8ddd-dddddddddddd',
      title: 'Inventário página dois',
      clientName: 'Cliente Inativo Permitido',
    }
    const searchedProcess = {
      id: 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee',
      title: 'Processo pesquisado',
      clientName: 'Cliente Pesquisado',
    }
    const fetchMock = authenticatedFetch(
      [organizationA],
      deadlineListResponse([]),
      lookupResponse([lookupProcess], 1, true),
      lookupResponse([lookupProcess, laterProcess], 2),
      lookupResponse([searchedProcess]),
    )
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(`/organizations/${organizationA.id}/deadlines`)
    await screen.findByText('Nenhum prazo cadastrado nesta organização.')
    await openCreate()
    await screen.findByRole('button', { name: new RegExp(lookupProcess.title) })
    expect(fetchMock.mock.calls[3]?.[0]).toBe(
      `/api/organizations/${organizationA.id}/processes/lookup?search=&pageNumber=1&pageSize=20`,
    )
    expect(fetchMock.mock.calls[3]?.[0]).not.toMatch(
      /\/processes\?pageNumber/,
    )
    fireEvent.click(screen.getByRole('button', { name: 'Carregar mais' }))
    expect(
      await screen.findByRole('button', { name: new RegExp(laterProcess.title) }),
    ).toHaveTextContent(laterProcess.clientName)
    expect(
      screen.getAllByRole('button', { name: new RegExp(lookupProcess.title) }),
    ).toHaveLength(1)

    fireEvent.change(screen.getByLabelText('Buscar processo'), {
      target: { value: '  pesquisado & especial  ' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Buscar' }))
    expect(
      await screen.findByRole('button', {
        name: new RegExp(searchedProcess.title),
      }),
    ).toHaveTextContent(searchedProcess.clientName)
    expect(fetchMock.mock.calls[5]?.[0]).toContain(
      'search=pesquisado+%26+especial&pageNumber=1&pageSize=20',
    )
    selectProcess(searchedProcess)
    expect(screen.getByText(/Processo selecionado:/)).toHaveTextContent(
      `${searchedProcess.title} — Cliente: ${searchedProcess.clientName}`,
    )
    fireEvent.click(screen.getByRole('button', { name: 'Limpar seleção' }))
    expect(screen.queryByText(/Processo selecionado:/)).not.toBeInTheDocument()
    selectProcess(searchedProcess)
  })

  it('ProcessLookup_BlankAndNonblankEmptyStates_AreDistinct', async () => {
    vi.stubGlobal(
      'fetch',
      authenticatedFetch(
        [organizationA],
        deadlineListResponse([]),
        lookupResponse([]),
        lookupResponse([]),
      ),
    )

    renderRoute(`/organizations/${organizationA.id}/deadlines`)
    await screen.findByText('Nenhum prazo cadastrado nesta organização.')
    await openCreate()
    expect(
      await screen.findByText(
        'Não há processo disponível para cadastrar um prazo nesta organização.',
      ),
    ).toBeInTheDocument()

    fireEvent.change(screen.getByLabelText('Buscar processo'), {
      target: { value: 'Inexistente' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Buscar' }))
    expect(
      await screen.findByText('Nenhum processo encontrado para esta busca.'),
    ).toBeInTheDocument()
  })

  it('ProcessLookup_OldSearchCompletesLast_DoesNotOverwriteNewSearch', async () => {
    let resolveOldSearch: ((value: Response) => void) | undefined
    const pendingOldSearch = new Promise<Response>((resolve) => {
      resolveOldSearch = resolve
    })
    const oldProcess = { ...lookupProcess, title: 'Busca antiga' }
    const newProcess = {
      ...lookupProcess,
      id: 'dddddddd-dddd-4ddd-8ddd-dddddddddddd',
      title: 'Busca atual',
    }
    vi.stubGlobal(
      'fetch',
      authenticatedFetch(
        [organizationA],
        deadlineListResponse([]),
        lookupResponse([]),
        pendingOldSearch,
        lookupResponse([newProcess]),
      ),
    )

    renderRoute(`/organizations/${organizationA.id}/deadlines`)
    await screen.findByText('Nenhum prazo cadastrado nesta organização.')
    await openCreate()
    await screen.findByText(
      'Não há processo disponível para cadastrar um prazo nesta organização.',
    )
    fireEvent.change(screen.getByLabelText('Buscar processo'), {
      target: { value: 'antiga' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Buscar' }))
    fireEvent.change(screen.getByLabelText('Buscar processo'), {
      target: { value: 'atual' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Buscar' }))
    expect(
      await screen.findByRole('button', { name: new RegExp(newProcess.title) }),
    ).toBeInTheDocument()

    await act(async () => {
      resolveOldSearch?.(lookupResponse([oldProcess]))
      await pendingOldSearch
    })

    expect(screen.queryByText(oldProcess.title)).not.toBeInTheDocument()
    expect(screen.getByText(newProcess.title)).toBeInTheDocument()
  })

  it('ProcessLookup_OldOrganizationCompletesLast_NeverPopulatesNewOrganization', async () => {
    let resolveLookupA: ((value: Response) => void) | undefined
    const pendingLookupA = new Promise<Response>((resolve) => {
      resolveLookupA = resolve
    })
    const processA = { ...lookupProcess, title: 'Processo da Alfa' }
    const processB = {
      ...lookupProcess,
      id: 'dddddddd-dddd-4ddd-8ddd-dddddddddddd',
      title: 'Processo da Beta',
    }
    vi.stubGlobal(
      'fetch',
      authenticatedFetch(
        [organizationA, organizationB],
        deadlineListResponse([]),
        pendingLookupA,
        deadlineListResponse([]),
        lookupResponse([processB]),
      ),
    )
    const router = renderRoute(`/organizations/${organizationA.id}/deadlines`)

    await screen.findByText('Nenhum prazo cadastrado nesta organização.')
    await openCreate()
    await screen.findByText('Carregando processos...')
    await act(async () => {
      await router.navigate(`/organizations/${organizationB.id}/deadlines`)
    })
    await screen.findByText('Nenhum prazo cadastrado nesta organização.')
    await openCreate()
    expect(
      await screen.findByRole('button', { name: new RegExp(processB.title) }),
    ).toBeInTheDocument()

    await act(async () => {
      resolveLookupA?.(lookupResponse([processA]))
      await pendingLookupA
    })

    expect(screen.queryByText(processA.title)).not.toBeInTheDocument()
    expect(screen.getByText(processB.title)).toBeInTheDocument()
  })

  it('ProcessLookup_LateLoadMoreAfterNewSearch_DoesNotMixResults', async () => {
    let resolveLoadMore: ((value: Response) => void) | undefined
    const pendingLoadMore = new Promise<Response>((resolve) => {
      resolveLoadMore = resolve
    })
    const lateProcess = { ...lookupProcess, title: 'Página antiga' }
    const currentProcess = {
      ...lookupProcess,
      id: 'dddddddd-dddd-4ddd-8ddd-dddddddddddd',
      title: 'Busca nova',
    }
    vi.stubGlobal(
      'fetch',
      authenticatedFetch(
        [organizationA],
        deadlineListResponse([]),
        lookupResponse([lookupProcess], 1, true),
        pendingLoadMore,
        lookupResponse([currentProcess]),
      ),
    )

    renderRoute(`/organizations/${organizationA.id}/deadlines`)
    await screen.findByText('Nenhum prazo cadastrado nesta organização.')
    await openCreate()
    await screen.findByRole('button', { name: 'Carregar mais' })
    fireEvent.click(screen.getByRole('button', { name: 'Carregar mais' }))
    fireEvent.change(screen.getByLabelText('Buscar processo'), {
      target: { value: 'nova' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Buscar' }))
    await screen.findByText(currentProcess.title)

    await act(async () => {
      resolveLoadMore?.(lookupResponse([lateProcess], 2))
      await pendingLoadMore
    })

    expect(screen.queryByText(lateProcess.title)).not.toBeInTheDocument()
    expect(screen.queryByText(lookupProcess.title)).not.toBeInTheDocument()
    expect(screen.getByText(currentProcess.title)).toBeInTheDocument()
  })

  it('ProcessLookup_LateOrganizationLoadMore_NeverMixesIntoNewOrganization', async () => {
    let resolveLoadMoreA: ((value: Response) => void) | undefined
    const pendingLoadMoreA = new Promise<Response>((resolve) => {
      resolveLoadMoreA = resolve
    })
    const processB = {
      ...lookupProcess,
      id: 'dddddddd-dddd-4ddd-8ddd-dddddddddddd',
      title: 'Processo da Beta',
    }
    const lateProcessA = { ...lookupProcess, title: 'Processo tardio da Alfa' }
    vi.stubGlobal(
      'fetch',
      authenticatedFetch(
        [organizationA, organizationB],
        deadlineListResponse([]),
        lookupResponse([lookupProcess], 1, true),
        pendingLoadMoreA,
        deadlineListResponse([]),
        lookupResponse([processB]),
      ),
    )
    const router = renderRoute(`/organizations/${organizationA.id}/deadlines`)

    await screen.findByText('Nenhum prazo cadastrado nesta organização.')
    await openCreate()
    await screen.findByRole('button', { name: 'Carregar mais' })
    fireEvent.click(screen.getByRole('button', { name: 'Carregar mais' }))
    await act(async () => {
      await router.navigate(`/organizations/${organizationB.id}/deadlines`)
    })
    await screen.findByText('Nenhum prazo cadastrado nesta organização.')
    await openCreate()
    expect(
      await screen.findByRole('button', { name: new RegExp(processB.title) }),
    ).toBeInTheDocument()

    await act(async () => {
      resolveLoadMoreA?.(lookupResponse([lateProcessA], 2))
      await pendingLoadMoreA
    })

    expect(screen.queryByText(lateProcessA.title)).not.toBeInTheDocument()
    expect(screen.getByText(processB.title)).toBeInTheDocument()
  })

  it('DeadlineCreate_ExactCsrfDateOnlyBody_NoOptimismAndAuthoritativeRefresh', async () => {
    let resolveCreate: ((value: Response) => void) | undefined
    const pendingCreate = new Promise<Response>((resolve) => {
      resolveCreate = resolve
    })
    const createdDeadline = {
      ...pendingDeadline,
      id: 'dddddddd-dddd-4ddd-8ddd-dddddddddddd',
      title: 'Prazo seguro',
    }
    const fetchMock = authenticatedFetch(
      [organizationA],
      deadlineListResponse([]),
      lookupResponse([lookupProcess]),
      response(200, { requestToken: 'transient-test-token' }),
      pendingCreate,
      deadlineListResponse([createdDeadline]),
    )
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(`/organizations/${organizationA.id}/deadlines`)
    await screen.findByText('Nenhum prazo cadastrado nesta organização.')
    await openCreate()
    await screen.findByText(lookupProcess.title)
    selectProcess()
    const form = submitCreate('  Prazo seguro  ', '2026-11-01')

    expect(
      await screen.findByRole('button', { name: 'Cadastrando...' }),
    ).toBeDisabled()
    fireEvent.submit(form)
    expect(fetchMock).toHaveBeenCalledTimes(6)
    expect(screen.queryByText(createdDeadline.title)).not.toBeInTheDocument()
    const [postUrl, postInit] = fetchMock.mock.calls[5] as [string, RequestInit]
    expect(postUrl).toBe(
      `/api/organizations/${organizationA.id}/deadlines`,
    )
    const body = JSON.parse(postInit.body as string) as Record<string, unknown>
    expect(body).toEqual({
      processId: lookupProcess.id,
      title: 'Prazo seguro',
      dueDate: '2026-11-01',
    })
    expect(Object.keys(body)).toEqual(['processId', 'title', 'dueDate'])
    for (const forbiddenField of [
      'organizationId',
      'tenantId',
      'clientId',
      'processTitle',
      'clientName',
      'state',
      'status',
      'createdAt',
      'completedAt',
      'role',
      'isOverdue',
    ]) {
      expect(body).not.toHaveProperty(forbiddenField)
    }
    expect(postInit.headers).toEqual({
      'Content-Type': 'application/json',
      'X-CSRF-TOKEN': 'transient-test-token',
    })

    await act(async () => {
      resolveCreate?.(response(201, { id: createdDeadline.id }))
      await pendingCreate
    })

    expect(
      await screen.findByText('Prazo cadastrado com sucesso.'),
    ).toBeInTheDocument()
    expect(await screen.findByText(createdDeadline.title)).toBeInTheDocument()
    expect(fetchMock.mock.calls[6]?.[0]).toContain(
      `/organizations/${organizationA.id}/deadlines?pageNumber=1&pageSize=20`,
    )
  })

  it('DeadlineCreate_Validation_RejectsMissingFieldsAndAllowsPastDateAtTitleLimit', async () => {
    const titleAtLimit = 'x'.repeat(150)
    const fetchMock = authenticatedFetch(
      [organizationA],
      deadlineListResponse([]),
      lookupResponse([lookupProcess]),
      response(200, { requestToken: 'test-token' }),
      response(201, { id: pendingDeadline.id }),
      deadlineListResponse([]),
    )
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(`/organizations/${organizationA.id}/deadlines`)
    await screen.findByText('Nenhum prazo cadastrado nesta organização.')
    await openCreate()
    await screen.findByText(lookupProcess.title)

    submitCreate('   ', '')
    expect(await screen.findByText('Informe o título do prazo.')).toBeInTheDocument()
    expect(screen.getByText('Selecione um processo.')).toBeInTheDocument()
    expect(screen.getByText('Informe uma data do prazo válida.')).toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(4)

    selectProcess()
    submitCreate('x'.repeat(151), '2000-01-01')
    expect(
      await screen.findByText('O título deve ter no máximo 150 caracteres.'),
    ).toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(4)

    submitCreate(`  ${titleAtLimit}  `, '2000-01-01')
    expect(
      await screen.findByText('Prazo cadastrado com sucesso.'),
    ).toBeInTheDocument()
    const postInit = fetchMock.mock.calls[5]?.[1] as RequestInit
    expect(JSON.parse(postInit.body as string)).toEqual({
      processId: lookupProcess.id,
      title: titleAtLimit,
      dueDate: '2000-01-01',
    })
  })

  it.each([
    [403, 'Você não tem permissão para cadastrar prazos nesta organização.'],
    [404, 'O processo selecionado não está disponível para este cadastro.'],
    [400, 'Não foi possível validar o cadastro. Verifique os dados e tente novamente.'],
  ] as const)(
    'DeadlineCreate_Status%s_ShowsSafeErrorWithoutRetry',
    async (status, expectedMessage) => {
      const fetchMock = authenticatedFetch(
        [organizationA],
        deadlineListResponse([]),
        lookupResponse([lookupProcess]),
        response(200, { requestToken: 'test-token' }),
        response(status, { detail: 'private server reason' }),
      )
      vi.stubGlobal('fetch', fetchMock)

      renderRoute(`/organizations/${organizationA.id}/deadlines`)
      await screen.findByText('Nenhum prazo cadastrado nesta organização.')
      await openCreate()
      await screen.findByText(lookupProcess.title)
      selectProcess()
      submitCreate('Prazo negado', '2026-11-01')

      const alert = await screen.findByRole('alert')
      expect(alert).toHaveTextContent(expectedMessage)
      expect(alert).not.toHaveTextContent('private server reason')
      expect(fetchMock).toHaveBeenCalledTimes(6)
      expect(screen.queryByText('Prazo negado')).not.toBeInTheDocument()
      if (status === 404) {
        expect(screen.queryByText(/Processo selecionado:/)).not.toBeInTheDocument()
      }
    },
  )

  it('DeadlineCreate_NetworkFailure_ShowsRetryableErrorWithoutAutomaticRetry', async () => {
    const fetchMock = authenticatedFetch(
      [organizationA],
      deadlineListResponse([]),
      lookupResponse([lookupProcess]),
      response(200, { requestToken: 'test-token' }),
    )
    fetchMock.mockRejectedValueOnce(new Error('private network detail'))
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(`/organizations/${organizationA.id}/deadlines`)
    await screen.findByText('Nenhum prazo cadastrado nesta organização.')
    await openCreate()
    await screen.findByText(lookupProcess.title)
    selectProcess()
    submitCreate('Prazo incerto', '2026-11-01')

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent(
      'Não foi possível cadastrar o prazo. Tente novamente.',
    )
    expect(alert).not.toHaveTextContent('private network detail')
    expect(fetchMock).toHaveBeenCalledTimes(6)
  })

  it('DeadlineCreate_LateSuccessAfterOrganizationNavigation_DoesNotAffectNewContext', async () => {
    let resolveCreateA: ((value: Response) => void) | undefined
    const pendingCreateA = new Promise<Response>((resolve) => {
      resolveCreateA = resolve
    })
    const deadlineB = { ...pendingDeadline, title: 'Prazo existente da Beta' }
    const processB = {
      ...lookupProcess,
      id: 'dddddddd-dddd-4ddd-8ddd-dddddddddddd',
      title: 'Processo da Beta',
    }
    vi.stubGlobal(
      'fetch',
      authenticatedFetch(
        [organizationA, organizationB],
        deadlineListResponse([]),
        lookupResponse([lookupProcess]),
        response(200, { requestToken: 'test-token' }),
        pendingCreateA,
        deadlineListResponse([deadlineB]),
        lookupResponse([processB]),
      ),
    )
    const router = renderRoute(`/organizations/${organizationA.id}/deadlines`)

    await screen.findByText('Nenhum prazo cadastrado nesta organização.')
    await openCreate()
    await screen.findByText(lookupProcess.title)
    selectProcess()
    submitCreate('Prazo tardio da Alfa', '2026-11-01')
    await screen.findByRole('button', { name: 'Cadastrando...' })

    await act(async () => {
      await router.navigate(`/organizations/${organizationB.id}/deadlines`)
    })
    expect(await screen.findByText(deadlineB.title)).toBeInTheDocument()
    await openCreate()
    await screen.findByText(processB.title)
    selectProcess(processB)

    await act(async () => {
      resolveCreateA?.(response(201, { id: pendingDeadline.id }))
      await pendingCreateA
    })

    expect(screen.getByText(deadlineB.title)).toBeInTheDocument()
    expect(screen.getByText(/Processo selecionado:/)).toHaveTextContent(
      processB.title,
    )
    expect(screen.queryByText('Prazo cadastrado com sucesso.')).not.toBeInTheDocument()
  })

  it('DeadlineRefresh_LateOldOrganizationResponse_DoesNotOverwriteNewOrganization', async () => {
    let resolveRefreshA: ((value: Response) => void) | undefined
    const pendingRefreshA = new Promise<Response>((resolve) => {
      resolveRefreshA = resolve
    })
    const deadlineB = { ...pendingDeadline, title: 'Prazo atual da Beta' }
    const staleDeadlineA = { ...pendingDeadline, title: 'Prazo criado na Alfa' }
    const fetchMock = authenticatedFetch(
      [organizationA, organizationB],
      deadlineListResponse([]),
      lookupResponse([lookupProcess]),
      response(200, { requestToken: 'test-token' }),
      response(201, { id: staleDeadlineA.id }),
      pendingRefreshA,
      deadlineListResponse([deadlineB]),
    )
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(`/organizations/${organizationA.id}/deadlines`)

    await screen.findByText('Nenhum prazo cadastrado nesta organização.')
    await openCreate()
    await screen.findByText(lookupProcess.title)
    selectProcess()
    submitCreate(staleDeadlineA.title, '2026-11-01')
    await screen.findByText('Prazo cadastrado com sucesso.')
    expect(fetchMock).toHaveBeenCalledTimes(7)

    await act(async () => {
      await router.navigate(`/organizations/${organizationB.id}/deadlines`)
    })
    expect(await screen.findByText(deadlineB.title)).toBeInTheDocument()

    await act(async () => {
      resolveRefreshA?.(deadlineListResponse([staleDeadlineA]))
      await pendingRefreshA
    })

    expect(screen.getByText(deadlineB.title)).toBeInTheDocument()
    expect(screen.queryByText(staleDeadlineA.title)).not.toBeInTheDocument()
  })
})

const responsibleMember = {
  id: 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee',
  displayName: 'Bruna Costa',
}

const processWithOtherResponsible: LegalProcessLookupItem = {
  id: 'dddddddd-dddd-4ddd-8ddd-dddddddddddd',
  title: 'Revisional de contrato',
  clientName: 'Cliente Beta',
  responsibleMembershipId: responsibleMember.id,
}

const processWithOwnResponsible: LegalProcessLookupItem = {
  id: 'ffffffff-ffff-4fff-8fff-ffffffffffff',
  title: 'Inventário familiar',
  clientName: 'Cliente Gama',
  responsibleMembershipId: organizationA.membershipId,
}

const processWithoutResponsible: LegalProcessLookupItem = {
  ...lookupProcess,
  responsibleMembershipId: null,
}

const responsibleUnavailableMessage =
  'O responsável selecionado não está mais disponível. Escolha outra pessoa.'

function memberLookupResponse(
  items: readonly { readonly id: string; readonly displayName: string }[],
): Response {
  return response(200, { items, pageNumber: 1, pageSize: 20, hasNext: false })
}

function deadlinesUrl(organizationId: string, query: string): string {
  return `/api/organizations/${organizationId}/deadlines?${query}`
}

function memberLookupUrl(organizationId: string): string {
  return `/api/organizations/${organizationId}/members/lookup?search=&pageNumber=1&pageSize=20`
}

function requestedUrls(fetchMock: ReturnType<typeof vi.fn>): string[] {
  return fetchMock.mock.calls.map((call) => String(call[0]))
}

function filters() {
  return screen.getByRole('group', { name: 'Filtros de prazos' })
}

function createForm(): HTMLFormElement {
  const form = screen.getByRole('button', { name: 'Cadastrar' }).closest('form')
  expect(form).not.toBeNull()
  return form!
}

function createResponsibleSelect(): HTMLSelectElement {
  return within(createForm()).getByLabelText('Responsável') as HTMLSelectElement
}

function lastPostBody(fetchMock: ReturnType<typeof vi.fn>): Record<string, unknown> {
  const postCall = fetchMock.mock.calls.find(
    (call) => (call[1] as RequestInit | undefined)?.method === 'POST',
  )
  expect(postCall).toBeDefined()
  return JSON.parse((postCall![1] as RequestInit).body as string) as Record<
    string,
    unknown
  >
}

describe('Deadlines responsible list and create (Phase 9C.2e)', () => {
  it('DeadlineList_Metadata_RendersResponsibleBeforeDueDateAndStatusLast', async () => {
    const assignedDeadline: LegalDeadlineListItem = {
      ...pendingDeadline,
      responsibleMembershipId: responsibleMember.id,
      responsibleDisplayName: responsibleMember.displayName,
    }
    vi.stubGlobal(
      'fetch',
      authenticatedFetch(
        [memberOrganization],
        deadlineListResponse([assignedDeadline, completedDeadline]),
      ),
    )

    renderRoute(`/organizations/${memberOrganization.id}/deadlines`)

    const results = await screen.findByRole('region', { name: 'Prazos cadastrados' })
    const [assignedItem, unassignedItem] = within(results).getAllByRole('listitem')
    const assignedMetadata = assignedItem!.querySelector('.deadline-record-metadata')!
    expect([...assignedMetadata.children].map((child) => child.textContent)).toEqual([
      assignedDeadline.processTitle,
      assignedDeadline.clientName,
      responsibleMember.displayName,
      'Vence em 01/11/2026',
      'Pendente',
    ])
    expect(assignedMetadata.lastElementChild).toHaveClass('deadline-status', 'is-pending')
    expect(
      within(assignedItem!).getByRole('link', { name: assignedDeadline.title }),
    ).toHaveAccessibleDescription(/Bruna Costa.*Vence em 01\/11\/2026.*Pendente/)

    const unassignedMetadata = unassignedItem!.querySelector('.deadline-record-metadata')!
    expect(unassignedMetadata.children[2]).toHaveTextContent('Sem responsável')
    expect(unassignedMetadata.lastElementChild).toHaveTextContent('Concluído')
  })

  it('DeadlineFilters_Member_SeesResponsibleFilterWithoutCreateAction', async () => {
    vi.stubGlobal(
      'fetch',
      authenticatedFetch([memberOrganization], deadlineListResponse([pendingDeadline])),
    )

    renderRoute(`/organizations/${memberOrganization.id}/deadlines`)
    await screen.findByText(pendingDeadline.title)

    const responsibleFilter = within(filters()).getByLabelText('Responsável')
    expect(responsibleFilter).toHaveValue('any')
    expect(
      within(responsibleFilter).getAllByRole('option').map((option) => option.textContent),
    ).toEqual(['Todos', 'Meus', 'Sem responsável', 'Pessoa específica'])
    expect(screen.queryByRole('button', { name: 'Cadastrar prazo' })).not.toBeInTheDocument()
  })

  it('DeadlineFilters_SelfAndUnassigned_SyncUrlQueryAndReturnToFirstPage', async () => {
    const fetchMock = authenticatedFetch(
      [memberOrganization],
      deadlineListResponse([pendingDeadline], 2),
      deadlineListResponse([pendingDeadline]),
      deadlineListResponse([pendingDeadline]),
      deadlineListResponse([pendingDeadline]),
    )
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(
      `/organizations/${memberOrganization.id}/deadlines?page=2`,
    )
    await screen.findByText(pendingDeadline.title)

    fireEvent.change(within(filters()).getByLabelText('Responsável'), {
      target: { value: 'self' },
    })
    await vi.waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(4))
    expect(router.state.location.search).toBe('?responsible=self')
    expect(fetchMock.mock.calls[3]?.[0]).toBe(
      deadlinesUrl(memberOrganization.id, 'pageNumber=1&pageSize=20&responsible=self'),
    )
    expect(await screen.findByText('Página 1')).toBeInTheDocument()

    fireEvent.change(within(filters()).getByLabelText('Responsável'), {
      target: { value: 'unassigned' },
    })
    await vi.waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(5))
    expect(router.state.location.search).toBe('?responsible=unassigned')
    expect(fetchMock.mock.calls[4]?.[0]).toBe(
      deadlinesUrl(
        memberOrganization.id,
        'pageNumber=1&pageSize=20&responsible=unassigned',
      ),
    )

    await screen.findByText(pendingDeadline.title)
    fireEvent.change(within(filters()).getByLabelText('Responsável'), {
      target: { value: 'any' },
    })
    await vi.waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(6))
    expect(router.state.location.search).toBe('')
    expect(fetchMock.mock.calls[5]?.[0]).toBe(
      deadlinesUrl(memberOrganization.id, 'pageNumber=1&pageSize=20'),
    )
    expect(requestedUrls(fetchMock).some((url) => url.includes('/members/lookup'))).toBe(
      false,
    )
  })

  it('DeadlineFilters_SpecificPerson_LoadsMembersOnlyWhenChosenAndFiltersById', async () => {
    const fetchMock = authenticatedFetch(
      [memberOrganization],
      deadlineListResponse([pendingDeadline]),
      memberLookupResponse([responsibleMember]),
      deadlineListResponse([pendingDeadline]),
    )
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(`/organizations/${memberOrganization.id}/deadlines`)
    await screen.findByText(pendingDeadline.title)
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
      deadlinesUrl(
        memberOrganization.id,
        `pageNumber=1&pageSize=20&responsible=${responsibleMember.id}`,
      ),
    )
    expect(within(filters()).getByLabelText('Responsável')).toHaveValue('specific')
    expect(within(filters()).getByText(responsibleMember.displayName)).toBeInTheDocument()
  })

  it('DeadlineFilters_UrlState_RestoresControlAndRequestsFilteredList', async () => {
    const fetchMock = authenticatedFetch(
      [memberOrganization],
      deadlineListResponse([pendingDeadline]),
    )
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(`/organizations/${memberOrganization.id}/deadlines?responsible=unassigned`)
    await screen.findByText(pendingDeadline.title)

    expect(fetchMock.mock.calls[2]?.[0]).toBe(
      deadlinesUrl(
        memberOrganization.id,
        'pageNumber=1&pageSize=20&responsible=unassigned',
      ),
    )
    expect(within(filters()).getByLabelText('Responsável')).toHaveValue('unassigned')
  })

  it('DeadlineFilters_InvalidResponsibleParameter_NormalizesUrlAndRequestsUnfiltered', async () => {
    const fetchMock = authenticatedFetch(
      [memberOrganization],
      deadlineListResponse([pendingDeadline]),
      deadlineListResponse([pendingDeadline]),
    )
    vi.stubGlobal('fetch', fetchMock)

    const router = renderRoute(
      `/organizations/${memberOrganization.id}/deadlines?responsible=not-a-guid`,
    )
    await screen.findByText(pendingDeadline.title)

    await vi.waitFor(() => expect(router.state.location.search).toBe(''))
    expect(
      requestedUrls(fetchMock).filter((url) => url.includes('responsible=')),
    ).toEqual([])
    expect(within(filters()).getByLabelText('Responsável')).toHaveValue('any')
  })

  it('DeadlineFilters_FilteredEmpty_IsDistinctFromUnfilteredEmptyAndClears', async () => {
    const fetchMock = authenticatedFetch(
      [organizationA],
      deadlineListResponse([]),
      deadlineListResponse([]),
    )
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(
      `/organizations/${organizationA.id}/deadlines?responsible=unassigned`,
    )

    expect(
      await screen.findByText('Nenhum prazo encontrado com estes filtros.'),
    ).toBeInTheDocument()
    expect(
      screen.queryByText('Nenhum prazo cadastrado nesta organização.'),
    ).not.toBeInTheDocument()
    expect(
      screen.queryByRole('button', { name: 'Cadastrar primeiro prazo' }),
    ).not.toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'Limpar filtros' }))

    expect(
      await screen.findByText('Nenhum prazo cadastrado nesta organização.'),
    ).toBeInTheDocument()
    expect(router.state.location.search).toBe('')
    expect(fetchMock.mock.calls[3]?.[0]).toBe(
      deadlinesUrl(organizationA.id, 'pageNumber=1&pageSize=20'),
    )
  })

  it('DeadlineCreate_ProcessWithOwnResponsible_SuggestsSelfAndSendsMembership', async () => {
    const fetchMock = authenticatedFetch(
      [organizationA],
      deadlineListResponse([]),
      lookupResponse([processWithOwnResponsible]),
      response(200, { requestToken: 'test-token' }),
      response(201, { id: pendingDeadline.id }),
      deadlineListResponse([]),
    )
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(`/organizations/${organizationA.id}/deadlines`)
    await screen.findByText('Nenhum prazo cadastrado nesta organização.')
    await openCreate()
    await screen.findByText(processWithOwnResponsible.title)
    expect(createResponsibleSelect()).toHaveValue('unassigned')

    selectProcess(processWithOwnResponsible)

    expect(createResponsibleSelect()).toHaveValue('self')
    expect(screen.queryByLabelText('Buscar responsável para prazo')).not.toBeInTheDocument()
    submitCreate('Prazo sugerido', '2026-11-01')
    expect(await screen.findByText('Prazo cadastrado com sucesso.')).toBeInTheDocument()
    expect(lastPostBody(fetchMock)).toEqual({
      processId: processWithOwnResponsible.id,
      title: 'Prazo sugerido',
      dueDate: '2026-11-01',
      responsibleMembershipId: organizationA.membershipId,
    })
    expect(requestedUrls(fetchMock).some((url) => url.includes('/members/lookup'))).toBe(
      false,
    )
  })

  it('DeadlineCreate_ProcessWithOtherResponsible_SuggestsOtherPersonInPicker', async () => {
    const fetchMock = authenticatedFetch(
      [organizationA],
      deadlineListResponse([]),
      lookupResponse([processWithOtherResponsible]),
      memberLookupResponse([responsibleMember]),
      response(200, { requestToken: 'test-token' }),
      response(201, { id: pendingDeadline.id }),
      deadlineListResponse([]),
    )
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(`/organizations/${organizationA.id}/deadlines`)
    await screen.findByText('Nenhum prazo cadastrado nesta organização.')
    await openCreate()
    await screen.findByText(processWithOtherResponsible.title)

    selectProcess(processWithOtherResponsible)

    expect(createResponsibleSelect()).toHaveValue('other')
    expect(
      await screen.findByRole('button', { name: responsibleMember.displayName }),
    ).toHaveAttribute('aria-pressed', 'true')
    expect(fetchMock.mock.calls[4]?.[0]).toBe(memberLookupUrl(organizationA.id))
    expect(screen.queryByText(responsibleMember.id)).not.toBeInTheDocument()

    submitCreate('Prazo de outra pessoa', '2026-11-01')
    expect(await screen.findByText('Prazo cadastrado com sucesso.')).toBeInTheDocument()
    expect(lastPostBody(fetchMock)).toMatchObject({
      processId: processWithOtherResponsible.id,
      responsibleMembershipId: responsibleMember.id,
    })
  })

  it.each([
    ['sem o campo', lookupProcess],
    ['com responsável nulo', processWithoutResponsible],
  ])(
    'DeadlineCreate_ProcessWithoutResponsible_%s_KeepsUnassignedAndOmitsField',
    async (_, process) => {
      const fetchMock = authenticatedFetch(
        [organizationA],
        deadlineListResponse([]),
        lookupResponse([process]),
        response(200, { requestToken: 'test-token' }),
        response(201, { id: pendingDeadline.id }),
        deadlineListResponse([]),
      )
      vi.stubGlobal('fetch', fetchMock)

      renderRoute(`/organizations/${organizationA.id}/deadlines`)
      await screen.findByText('Nenhum prazo cadastrado nesta organização.')
      await openCreate()
      await screen.findByText(process.title)
      selectProcess(process)

      expect(createResponsibleSelect()).toHaveValue('unassigned')
      submitCreate('Prazo sem responsável', '2026-11-01')
      expect(await screen.findByText('Prazo cadastrado com sucesso.')).toBeInTheDocument()
      expect(Object.keys(lastPostBody(fetchMock))).toEqual([
        'processId',
        'title',
        'dueDate',
      ])
    },
  )

  it('DeadlineCreate_ManualResponsibleChoice_IsNeverOverwrittenBySuggestion', async () => {
    const fetchMock = authenticatedFetch(
      [organizationA],
      deadlineListResponse([]),
      lookupResponse([processWithOtherResponsible, processWithOwnResponsible]),
      memberLookupResponse([responsibleMember]),
    )
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(`/organizations/${organizationA.id}/deadlines`)
    await screen.findByText('Nenhum prazo cadastrado nesta organização.')
    await openCreate()
    await screen.findByText(processWithOtherResponsible.title)

    selectProcess(processWithOtherResponsible)
    expect(createResponsibleSelect()).toHaveValue('other')

    fireEvent.change(createResponsibleSelect(), { target: { value: 'unassigned' } })
    selectProcess(processWithOwnResponsible)
    expect(createResponsibleSelect()).toHaveValue('unassigned')

    fireEvent.change(createResponsibleSelect(), { target: { value: 'self' } })
    selectProcess(processWithOtherResponsible)
    expect(createResponsibleSelect()).toHaveValue('self')
    expect(screen.queryByLabelText('Buscar responsável para prazo')).not.toBeInTheDocument()
  })

  it('DeadlineCreate_Picker_RendersOnlyForOtherPersonAndRequiresSelection', async () => {
    const fetchMock = authenticatedFetch(
      [organizationA],
      deadlineListResponse([]),
      lookupResponse([lookupProcess]),
      memberLookupResponse([responsibleMember]),
      response(200, { requestToken: 'test-token' }),
      response(201, { id: pendingDeadline.id }),
      deadlineListResponse([]),
    )
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(`/organizations/${organizationA.id}/deadlines`)
    await screen.findByText('Nenhum prazo cadastrado nesta organização.')
    await openCreate()
    await screen.findByText(lookupProcess.title)
    selectProcess()

    expect(
      within(createResponsibleSelect())
        .getAllByRole('option')
        .map((option) => option.textContent),
    ).toEqual(['Sem responsável', 'Eu', 'Outra pessoa'])
    expect(screen.queryByLabelText('Buscar responsável para prazo')).not.toBeInTheDocument()
    fireEvent.change(createResponsibleSelect(), { target: { value: 'self' } })
    expect(screen.queryByLabelText('Buscar responsável para prazo')).not.toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(4)

    fireEvent.change(createResponsibleSelect(), { target: { value: 'other' } })
    expect(screen.getByLabelText('Buscar responsável para prazo')).toBeInTheDocument()
    await screen.findByRole('button', { name: responsibleMember.displayName })
    expect(fetchMock.mock.calls[4]?.[0]).toBe(memberLookupUrl(organizationA.id))

    submitCreate('Prazo sem pessoa', '2026-11-01')
    expect(
      await screen.findByText('Selecione uma pessoa responsável.'),
    ).toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(5)

    fireEvent.click(screen.getByRole('button', { name: responsibleMember.displayName }))
    expect(screen.getByText(/Responsável selecionado:/)).toHaveTextContent(
      `Responsável selecionado: ${responsibleMember.displayName}`,
    )
    submitCreate('Prazo com pessoa', '2026-11-01')
    expect(await screen.findByText('Prazo cadastrado com sucesso.')).toBeInTheDocument()
    expect(lastPostBody(fetchMock)).toMatchObject({
      responsibleMembershipId: responsibleMember.id,
    })
  })

  it.each([
    ['outra pessoa', processWithOtherResponsible, 'other'],
    ['eu', processWithOwnResponsible, 'unassigned'],
  ] as const)(
    'DeadlineCreate_RelatedResponsibleUnavailable_%s_ClearsSelectionAndExplains',
    async (_, process, expectedMode) => {
      const usesPicker = process === processWithOtherResponsible
      const fetchMock = authenticatedFetch(
        [organizationA],
        deadlineListResponse([]),
        lookupResponse([process]),
        ...(usesPicker ? [memberLookupResponse([responsibleMember])] : []),
        response(200, { requestToken: 'test-token' }),
        response(400, {
          title: 'Related responsible member unavailable',
          detail: 'private server reason',
        }),
      )
      vi.stubGlobal('fetch', fetchMock)

      renderRoute(`/organizations/${organizationA.id}/deadlines`)
      await screen.findByText('Nenhum prazo cadastrado nesta organização.')
      await openCreate()
      await screen.findByText(process.title)
      selectProcess(process)
      if (usesPicker) {
        await screen.findByRole('button', { name: responsibleMember.displayName })
      }
      submitCreate('Prazo indisponível', '2026-11-01')

      expect(await screen.findByText(responsibleUnavailableMessage)).toBeInTheDocument()
      expect(screen.queryByText('private server reason')).not.toBeInTheDocument()
      expect(createResponsibleSelect()).toHaveValue(expectedMode)
      expect(screen.queryByText(/Responsável selecionado:/)).not.toBeInTheDocument()
      expect(screen.getByText(/Processo selecionado:/)).toBeInTheDocument()

      // The unavailable suggestion must not come back when picking the process again.
      selectProcess(process)
      expect(createResponsibleSelect()).toHaveValue(expectedMode)
      expect(screen.queryByText(/Responsável selecionado:/)).not.toBeInTheDocument()
    },
  )
})
