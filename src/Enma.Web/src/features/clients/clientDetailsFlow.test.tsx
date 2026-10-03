import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

vi.mock('../notifications/NotificationCenter', () => ({
  NotificationCenter: () => null,
}))
import { createAppRoutes } from '../../app/router'
import { clearCsrfToken } from '../authentication/csrfClient'
import { createEmailVerificationFlow } from '../email-verification/emailVerificationService'
import type { OrganizationNavigationItem } from '../organizations/organizationTypes'
import { FinanceRequestError } from '../finance/financeService'
import * as financeService from '../finance/financeService'
import type { ClientFinanceSummary } from '../finance/financeTypes'
import type { Client, ClientDetail } from './clientTypes'

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

const clientA: ClientDetail = {
  id: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
  name: 'Cliente Alfa',
  email: 'cliente.alfa@example.com',
  phone: '22999998888',
  cpf: '52998224725',
  personType: 'individual',
  cnpj: null,
  address: 'Rua Sintética, 10',
  notes: 'Observação sintética',
  isActive: true,
  createdAt: '2026-08-12T14:30:00Z',
}

const clientB: ClientDetail = {
  id: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',
  name: 'Cliente Beta',
  email: null,
  phone: null,
  cpf: null,
  personType: 'individual',
  cnpj: null,
  address: null,
  notes: null,
  isActive: false,
  createdAt: '2026-08-11T12:00:00Z',
}

function financeSummary(
  overrides: Partial<ClientFinanceSummary> = {},
): ClientFinanceSummary {
  return {
    clientId: clientA.id,
    referenceDate: '2026-09-08',
    totalContractedAmount: '1234.56',
    totalReceivedAmount: '90071992547409.93',
    totalOutstandingAmount: '9999999999999999.99',
    overdueAmount: '321.09',
    paymentPlanCount: 3,
    ...overrides,
  }
}

function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>((complete) => {
    resolve = complete
  })
  return { promise, resolve }
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

function clientListResponse(items: readonly Client[]): Response {
  return response(200, {
    items: items.map((client) => ({
      id: client.id,
      name: client.name,
      isActive: client.isActive,
      createdAt: client.createdAt,
    })),
    pageNumber: 1,
    pageSize: 20,
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

function authenticatedDetailFetch(
  organization: OrganizationNavigationItem,
  detailResponse: Response,
) {
  return vi
    .fn()
    .mockResolvedValueOnce(organizationResponse([]))
    .mockResolvedValueOnce(organizationResponse([organization]))
    .mockResolvedValueOnce(detailResponse)
}

function detailPath(
  organization: OrganizationNavigationItem,
  client: Client,
): string {
  return `/organizations/${organization.id}/clients/${client.id}`
}

function openEditAndSubmit(name: string, nameLabel = 'Nome') {
  fireEvent.click(screen.getByRole('button', { name: 'Editar cliente' }))
  const input = screen.getByLabelText(nameLabel)
  fireEvent.change(input, { target: { value: name } })
  const form = screen
    .getByRole('button', { name: 'Salvar alterações' })
    .closest('form')!
  fireEvent.submit(form)
  return form
}

beforeEach(() => {
  clearCsrfToken()
  window.localStorage.clear()
  window.sessionStorage.clear()
  vi.spyOn(financeService, 'getClientFinanceSummary').mockResolvedValue(
    financeSummary(),
  )
})

afterEach(() => {
  clearCsrfToken()
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

describe('Clients D2 flow', () => {
  it('ClientDetail_DirectMemberRoute_TargetsTenantResourceAndRendersReadOnlyFields', async () => {
    const member = { ...organizationA, role: 'Member' as const }
    const localStorageSpy = vi.spyOn(window.localStorage, 'setItem')
    const sessionStorageSpy = vi.spyOn(window.sessionStorage, 'setItem')
    const fetchMock = authenticatedDetailFetch(member, response(200, clientA))
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(member, clientA))

    expect(
      await screen.findByRole('heading', { name: clientA.name }),
    ).toBeInTheDocument()
    expect(screen.getByText('Ativo')).toBeInTheDocument()
    expect(screen.getByText(clientA.email!)).toBeInTheDocument()
    expect(screen.getByText(clientA.phone!)).toBeInTheDocument()
    expect(screen.getByText('529.982.247-25')).toBeInTheDocument()
    expect(screen.queryByText(clientA.cpf!)).not.toBeInTheDocument()
    expect(screen.getByText('Pessoa física')).toBeInTheDocument()
    expect(screen.getByText(clientA.address!)).toBeInTheDocument()
    expect(screen.getByText(clientA.notes!)).toBeInTheDocument()
    expect(screen.getByText(/12\/08\/2026/)).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Voltar para clientes' })).toHaveAttribute(
      'href',
      `/organizations/${member.id}/clients`,
    )
    expect(fetchMock).toHaveBeenNthCalledWith(
      3,
      `/api/organizations/${member.id}/clients/${clientA.id}`,
      {
        method: 'GET',
        cache: 'no-store',
        signal: expect.any(AbortSignal),
        credentials: 'same-origin',
      },
    )
    expect(screen.queryByRole('button', { name: 'Editar cliente' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Desativar cliente' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Reativar cliente' })).not.toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Financeiro' })).not.toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'Ver planos financeiros' })).not.toBeInTheDocument()
    expect(financeService.getClientFinanceSummary).not.toHaveBeenCalled()
    expect(localStorageSpy).not.toHaveBeenCalled()
    expect(sessionStorageSpy).not.toHaveBeenCalled()
  })

  it.each(['Owner', 'Administrator'] as const)(
    'ClientDetail_%sRole_ShowsStateAppropriateMutationControls',
    async (role) => {
      const organization = { ...organizationA, role }
      vi.stubGlobal(
        'fetch',
        authenticatedDetailFetch(organization, response(200, clientA)),
      )

      renderRoute(detailPath(organization, clientA))

      expect(
        await screen.findByRole('button', { name: 'Editar cliente' }),
      ).toBeInTheDocument()
      expect(screen.getByRole('button', { name: 'Desativar cliente' })).toBeInTheDocument()
      expect(screen.queryByRole('button', { name: 'Reativar cliente' })).not.toBeInTheDocument()
      expect(
        await screen.findByRole('heading', { name: 'Financeiro' }),
      ).toBeInTheDocument()
      expect(screen.getByText('3 planos de pagamento')).toBeInTheDocument()
      expect(financeService.getClientFinanceSummary).toHaveBeenCalledWith(
        organization.id,
        clientA.id,
        expect.any(Function),
        expect.any(AbortSignal),
      )
    },
  )

  it.each([true, false])(
    'ClientDetail_MemberLifecycleState%s_HidesAllMutationControls',
    async (isActive) => {
      const member = { ...organizationA, role: 'Member' as const }
      const client = { ...clientA, isActive }
      vi.stubGlobal(
        'fetch',
        authenticatedDetailFetch(member, response(200, client)),
      )

      renderRoute(detailPath(member, client))
      await screen.findByRole('heading', { name: client.name })

      expect(screen.queryByRole('button', { name: 'Editar cliente' })).not.toBeInTheDocument()
      expect(screen.queryByRole('button', { name: 'Desativar cliente' })).not.toBeInTheDocument()
      expect(screen.queryByRole('button', { name: 'Reativar cliente' })).not.toBeInTheDocument()
      expect(screen.queryByRole('heading', { name: 'Financeiro' })).not.toBeInTheDocument()
      expect(financeService.getClientFinanceSummary).not.toHaveBeenCalled()
    },
  )

  it('ClientFinanceSummary_RendersExactMetricsDateSingularAndDeepLink', async () => {
    vi.mocked(financeService.getClientFinanceSummary).mockResolvedValueOnce(
      financeSummary({ paymentPlanCount: 1 }),
    )
    const fetchMock = authenticatedDetailFetch(
      organizationA,
      response(200, clientA),
    )
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(organizationA, clientA))

    expect(await screen.findByText('R$ 1.234,56')).toBeInTheDocument()
    expect(screen.getByText('R$ 90.071.992.547.409,93')).toBeInTheDocument()
    expect(screen.getByText('R$ 9.999.999.999.999.999,99')).toBeInTheDocument()
    expect(screen.getByText('R$ 321,09')).toBeInTheDocument()
    expect(screen.getByText('Posição em 08/09/2026')).toBeInTheDocument()
    expect(screen.getByText('1 plano de pagamento')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Ver planos financeiros' })).toHaveAttribute(
      'href',
      `/organizations/${organizationA.id}/finance?clientId=${clientA.id}&page=1`,
    )
    expect(fetchMock).toHaveBeenNthCalledWith(
      3,
      `/api/organizations/${organizationA.id}/clients/${clientA.id}`,
      expect.objectContaining({ method: 'GET', cache: 'no-store' }),
    )
  })

  it('ClientFinanceSummary_ZeroPlans_RendersZerosPluralAndEmptyState', async () => {
    vi.mocked(financeService.getClientFinanceSummary).mockResolvedValueOnce(
      financeSummary({
        totalContractedAmount: '0',
        totalReceivedAmount: '0.0',
        totalOutstandingAmount: '0.00',
        overdueAmount: '0',
        paymentPlanCount: 0,
      }),
    )
    vi.stubGlobal(
      'fetch',
      authenticatedDetailFetch(organizationA, response(200, clientA)),
    )

    renderRoute(detailPath(organizationA, clientA))

    expect(await screen.findByText('Nenhum plano financeiro cadastrado.')).toBeInTheDocument()
    expect(screen.getAllByText('R$ 0,00')).toHaveLength(4)
    expect(screen.getByText('0 planos de pagamento')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Ver planos financeiros' })).toBeInTheDocument()
  })

  it('ClientFinanceSummary_Loading_RemainsLocalToTheRenderedClientDetail', async () => {
    const pending = deferred<ClientFinanceSummary>()
    vi.mocked(financeService.getClientFinanceSummary).mockReturnValueOnce(
      pending.promise,
    )
    vi.stubGlobal(
      'fetch',
      authenticatedDetailFetch(organizationA, response(200, clientA)),
    )

    renderRoute(detailPath(organizationA, clientA))

    expect(
      await screen.findByRole('heading', { name: clientA.name }),
    ).toBeInTheDocument()
    expect(screen.getByText(clientA.email!)).toBeInTheDocument()
    expect(screen.getByText('Carregando resumo financeiro…')).toHaveAttribute(
      'role',
      'status',
    )
    expect(screen.getByRole('region', { name: 'Financeiro' })).toHaveAttribute(
      'aria-busy',
      'true',
    )
  })

  it('ClientFinanceSummary_ErrorRetriesOnlyFinanceAndKeepsClientUsable', async () => {
    vi.mocked(financeService.getClientFinanceSummary)
      .mockRejectedValueOnce(new Error('private network detail'))
      .mockResolvedValueOnce(financeSummary({ totalContractedAmount: '88.75' }))
    const fetchMock = authenticatedDetailFetch(
      organizationA,
      response(200, clientA),
    )
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(organizationA, clientA))

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent(
      'Não foi possível carregar as informações financeiras deste cliente.',
    )
    expect(alert).not.toHaveTextContent('private network detail')
    expect(screen.getByRole('heading', { name: clientA.name })).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Tentar novamente' }))

    expect(await screen.findByText('R$ 88,75')).toBeInTheDocument()
    expect(financeService.getClientFinanceSummary).toHaveBeenCalledTimes(2)
    expect(fetchMock).toHaveBeenCalledTimes(3)
  })

  it('ClientFinanceSummary_NotFound_IsNeverRenderedAsZero', async () => {
    vi.mocked(financeService.getClientFinanceSummary).mockRejectedValueOnce(
      new FinanceRequestError('not-found'),
    )
    vi.stubGlobal(
      'fetch',
      authenticatedDetailFetch(organizationA, response(200, clientA)),
    )

    renderRoute(detailPath(organizationA, clientA))

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent(
      'Não foi possível carregar as informações financeiras deste cliente.',
    )
    expect(screen.queryByText('Nenhum plano financeiro cadastrado.')).not.toBeInTheDocument()
    expect(screen.queryByText('R$ 0,00')).not.toBeInTheDocument()
    expect(screen.getByRole('heading', { name: clientA.name })).toBeInTheDocument()
  })

  it('ClientFinanceSummary_ForbiddenRefreshToMember_RemovesSectionAndData', async () => {
    const refreshedOrganizations = deferred<Response>()
    const fetchMock = authenticatedDetailFetch(
      organizationA,
      response(200, clientA),
    ).mockReturnValueOnce(refreshedOrganizations.promise)
    vi.stubGlobal('fetch', fetchMock)
    vi.mocked(financeService.getClientFinanceSummary).mockRejectedValueOnce(
      new FinanceRequestError('forbidden'),
    )

    renderRoute(detailPath(organizationA, clientA))

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'O acesso às informações financeiras pode ter mudado.',
    )
    expect(fetchMock).toHaveBeenNthCalledWith(
      4,
      '/api/me/organizations',
      expect.objectContaining({ method: 'GET', cache: 'no-store' }),
    )

    await act(async () => {
      refreshedOrganizations.resolve(
        organizationResponse([{ ...organizationA, role: 'Member' }]),
      )
      await refreshedOrganizations.promise
    })

    await waitFor(() => {
      expect(screen.queryByRole('heading', { name: 'Financeiro' })).not.toBeInTheDocument()
    })
    expect(screen.queryByRole('link', { name: 'Ver planos financeiros' })).not.toBeInTheDocument()
    expect(financeService.getClientFinanceSummary).toHaveBeenCalledTimes(1)
  })

  it('ClientFinanceSummary_Unauthorized_UsesTheEstablishedSessionFlow', async () => {
    vi.mocked(financeService.getClientFinanceSummary).mockImplementationOnce(
      async (_organizationId, _clientId, onUnauthorized) => {
        onUnauthorized()
        throw new FinanceRequestError('unauthorized')
      },
    )
    vi.stubGlobal(
      'fetch',
      authenticatedDetailFetch(organizationA, response(200, clientA)),
    )

    renderRoute(detailPath(organizationA, clientA))

    expect(
      await screen.findByRole('heading', { name: 'Entrar no ENMA' }),
    ).toBeInTheDocument()
    expect(screen.queryByText('Não foi possível carregar as informações financeiras deste cliente.')).not.toBeInTheDocument()
  })

  it('ClientList_NameLink_NavigatesToCurrentOrganizationDetail', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(organizationResponse([]))
      .mockResolvedValueOnce(organizationResponse([organizationA]))
      .mockResolvedValueOnce(clientListResponse([clientA]))
      .mockResolvedValueOnce(response(200, clientA))
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(`/organizations/${organizationA.id}/clients`)

    fireEvent.click(await screen.findByRole('link', { name: clientA.name }))

    expect(
      await screen.findByRole('heading', { name: clientA.name }),
    ).toBeInTheDocument()
    expect(router.state.location.pathname).toBe(detailPath(organizationA, clientA))
  })

  it('ClientEdit_ValidTrimmedName_SendsExactCsrfBodyAndRefetchesWithoutOptimism', async () => {
    let resolveUpdate: ((value: Response) => void) | undefined
    const pendingUpdate = new Promise<Response>((resolve) => {
      resolveUpdate = resolve
    })
    const updatedClient = { ...clientA, name: 'Novo nome' }
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(organizationResponse([]))
      .mockResolvedValueOnce(organizationResponse([organizationA]))
      .mockResolvedValueOnce(response(200, clientA))
      .mockResolvedValueOnce(response(200, { requestToken: 'test-token' }))
      .mockReturnValueOnce(pendingUpdate)
      .mockResolvedValueOnce(response(200, updatedClient))
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(organizationA, clientA))
    await screen.findByRole('heading', { name: clientA.name })
    const form = openEditAndSubmit('  Novo nome  ')

    expect(await screen.findByRole('button', { name: 'Salvando...' })).toBeDisabled()
    fireEvent.submit(form)
    expect(fetchMock).toHaveBeenCalledTimes(5)
    expect(screen.getByRole('heading', { name: clientA.name })).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: updatedClient.name })).not.toBeInTheDocument()

    const [updateUrl, updateInit] = fetchMock.mock.calls[4] as [string, RequestInit]
    expect(updateUrl).toBe(`/api/organizations/${organizationA.id}/clients/${clientA.id}`)
    expect(updateInit.method).toBe('PUT')
    expect(updateInit.headers).toEqual({
      'Content-Type': 'application/json',
      'X-CSRF-TOKEN': 'test-token',
    })
    expect(JSON.parse(updateInit.body as string)).toEqual({
      name: 'Novo nome',
      email: clientA.email,
      phone: clientA.phone,
      cpf: clientA.cpf,
      personType: clientA.personType,
      cnpj: clientA.cnpj,
      address: clientA.address,
      notes: clientA.notes,
    })
    expect(Object.keys(JSON.parse(updateInit.body as string))).toEqual([
      'name',
      'email',
      'phone',
      'cpf',
      'personType',
      'cnpj',
      'address',
      'notes',
    ])

    await act(async () => {
      resolveUpdate?.(response(204))
      await pendingUpdate
    })

    expect(
      await screen.findByRole('heading', { name: updatedClient.name }),
    ).toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(6)
  })

  it('ClientEdit_ClearedOptionalProfileFields_SendsNullAndRefetchesAuthoritativeDetail', async () => {
    const clearedClient: ClientDetail = {
      ...clientA,
      email: null,
      phone: null,
      cpf: null,
    }

    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(organizationResponse([]))
      .mockResolvedValueOnce(organizationResponse([organizationA]))
      .mockResolvedValueOnce(response(200, clientA))
      .mockResolvedValueOnce(response(200, { requestToken: 'test-token' }))
      .mockResolvedValueOnce(response(204))
      .mockResolvedValueOnce(response(200, clearedClient))

    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(organizationA, clientA))

    await screen.findByRole('heading', { name: clientA.name })

    fireEvent.click(
      screen.getByRole('button', { name: 'Editar cliente' }),
    )

    expect(screen.getByLabelText('E-mail')).toHaveValue(clientA.email)
    expect(screen.getByLabelText('Telefone')).toHaveValue(clientA.phone)
    expect(screen.getByLabelText('CPF')).toHaveValue(clientA.cpf)

    fireEvent.change(screen.getByLabelText('E-mail'), {
      target: { value: '   ' },
    })
    fireEvent.change(screen.getByLabelText('Telefone'), {
      target: { value: '   ' },
    })
    fireEvent.change(screen.getByLabelText('CPF'), {
      target: { value: '   ' },
    })

    const form = screen
      .getByRole('button', { name: 'Salvar alterações' })
      .closest('form')!

    fireEvent.submit(form)

    await waitFor(() => {
      expect(fetchMock).toHaveBeenCalledTimes(6)
    })

    const [updateUrl, updateInit] = fetchMock.mock.calls[4] as [
      string,
      RequestInit,
    ]

    expect(updateUrl).toBe(
      `/api/organizations/${organizationA.id}/clients/${clientA.id}`,
    )
    expect(updateInit.method).toBe('PUT')
    expect(JSON.parse(updateInit.body as string)).toEqual({
      name: clientA.name,
      email: null,
      phone: null,
      cpf: null,
      personType: clientA.personType,
      cnpj: clientA.cnpj,
      address: clientA.address,
      notes: clientA.notes,
    })

    expect(screen.getAllByText('Não informado')).toHaveLength(3)
  })

  it('ClientEdit_CompanyClient_ResendsLoadedProfileFieldsUnchanged', async () => {
    const companyClient: ClientDetail = {
      ...clientA,
      cpf: null,
      personType: 'company',
      cnpj: '12ABC34501DE35',
      address: 'Avenida Sintética, 200',
      notes: 'Observação da empresa',
    }
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(organizationResponse([]))
      .mockResolvedValueOnce(organizationResponse([organizationA]))
      .mockResolvedValueOnce(response(200, companyClient))
      .mockResolvedValueOnce(response(200, { requestToken: 'test-token' }))
      .mockResolvedValueOnce(response(204))
      .mockResolvedValueOnce(response(200, { ...companyClient, name: 'Empresa' }))
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(organizationA, companyClient))
    await screen.findByRole('heading', { name: companyClient.name })
    expect(screen.getByText('12.ABC.345/01DE-35')).toBeInTheDocument()
    openEditAndSubmit('Empresa', 'Razão social')

    await waitFor(() => {
      expect(fetchMock).toHaveBeenCalledTimes(6)
    })

    const [, updateInit] = fetchMock.mock.calls[4] as [string, RequestInit]
    expect(updateInit.method).toBe('PUT')
    expect(JSON.parse(updateInit.body as string)).toEqual({
      name: 'Empresa',
      email: companyClient.email,
      phone: companyClient.phone,
      cpf: null,
      personType: 'company',
      cnpj: '12ABC34501DE35',
      address: 'Avenida Sintética, 200',
      notes: 'Observação da empresa',
    })
    expect(
      await screen.findByRole('heading', { name: 'Empresa' }),
    ).toBeInTheDocument()
    expect(screen.getByText('Avenida Sintética, 200')).toBeInTheDocument()
    expect(screen.getByText('Observação da empresa')).toBeInTheDocument()
  })

  it.each([
    ['unknown person type', { ...clientA, personType: 'legalEntity' }],
    ['missing person type', { ...clientA, personType: undefined }],
    [
      'missing notes key',
      Object.fromEntries(
        Object.entries(clientA).filter(([key]) => key !== 'notes'),
      ),
    ],
    ['non-string cnpj', { ...clientA, cnpj: 12345678000195 }],
    ['non-string address', { ...clientA, address: { street: 'Rua' } }],
  ])(
    'ClientDetail_StrictParser_RejectsPayloadWith %s',
    async (_case, payload) => {
      vi.stubGlobal(
        'fetch',
        authenticatedDetailFetch(organizationA, response(200, payload)),
      )

      renderRoute(detailPath(organizationA, clientA))

      const alert = await screen.findByRole('alert')
      expect(alert).toHaveTextContent(
        'Não foi possível carregar o cliente. Tente novamente.',
      )
      expect(
        screen.queryByRole('heading', { name: clientA.name }),
      ).not.toBeInTheDocument()
    },
  )

  it('ClientEdit_InvalidNames_PreventWhitespaceAndOverlongRequests', async () => {
    const fetchMock = authenticatedDetailFetch(organizationA, response(200, clientA))
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(organizationA, clientA))
    await screen.findByRole('heading', { name: clientA.name })
    fireEvent.click(screen.getByRole('button', { name: 'Editar cliente' }))
    const input = screen.getByLabelText('Nome')
    const form = screen.getByRole('button', { name: 'Salvar alterações' }).closest('form')!

    fireEvent.change(input, { target: { value: '   ' } })
    fireEvent.submit(form)
    expect(await screen.findByText('Informe o nome do cliente.')).toBeInTheDocument()

    fireEvent.change(input, { target: { value: 'x'.repeat(151) } })
    fireEvent.submit(form)
    expect(await screen.findByText('O nome deve ter no máximo 150 caracteres.')).toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(3)
  })

  it('ClientEdit_StaleAdministratorRoleForbidden_KeepsAuthoritativeClientWithoutRetry', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(organizationResponse([]))
      .mockResolvedValueOnce(organizationResponse([organizationB]))
      .mockResolvedValueOnce(response(200, clientA))
      .mockResolvedValueOnce(response(200, { requestToken: 'test-token' }))
      .mockResolvedValueOnce(response(403, { detail: 'private role reason' }))
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(organizationB, clientA))
    await screen.findByRole('heading', { name: clientA.name })
    openEditAndSubmit('Nome sem permissão')

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Você não tem permissão para alterar este cliente.')
    expect(alert).not.toHaveTextContent('private role reason')
    expect(screen.getByRole('heading', { name: clientA.name })).toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(5)
  })

  it('ClientDeactivate_ConfirmedPostsNoBodyWithCsrfAndConvergesFromRefetch', async () => {
    const deactivatedClient = { ...clientA, isActive: false }
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(organizationResponse([]))
      .mockResolvedValueOnce(organizationResponse([organizationA]))
      .mockResolvedValueOnce(response(200, clientA))
      .mockResolvedValueOnce(response(200, { requestToken: 'test-token' }))
      .mockResolvedValueOnce(response(204))
      .mockResolvedValueOnce(response(200, deactivatedClient))
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(organizationA, clientA))
    await screen.findByRole('heading', { name: clientA.name })
    fireEvent.click(screen.getByRole('button', { name: 'Desativar cliente' }))
    expect(screen.getByRole('alertdialog')).toHaveTextContent(
      `Desativar ${clientA.name}? O cliente poderá ser reativado depois.`,
    )
    fireEvent.click(screen.getByRole('button', { name: 'Confirmar desativação' }))

    expect(await screen.findByText('Inativo')).toBeInTheDocument()
    const [url, init] = fetchMock.mock.calls[4] as [string, RequestInit]
    expect(url).toBe(`/api/organizations/${organizationA.id}/clients/${clientA.id}/deactivate`)
    expect(init.method).toBe('POST')
    expect(init.headers).toEqual({ 'X-CSRF-TOKEN': 'test-token' })
    expect(init.body).toBeUndefined()
    expect(screen.getByRole('button', { name: 'Reativar cliente' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Desativar cliente' })).not.toBeInTheDocument()
  })

  it('ClientDeactivate_ContainsKeyboardFocusAndRestoresTheTriggerOnClose', async () => {
    vi.stubGlobal(
      'fetch',
      authenticatedDetailFetch(organizationA, response(200, clientA)),
    )

    renderRoute(detailPath(organizationA, clientA))
    const trigger = await screen.findByRole('button', {
      name: 'Desativar cliente',
    })
    fireEvent.click(trigger)

    const dialog = screen.getByRole('alertdialog', {
      name: 'Desativar cliente',
      description: `Desativar ${clientA.name}? O cliente poderá ser reativado depois.`,
    })
    const cancel = screen.getByRole('button', { name: 'Cancelar' })
    const confirm = screen.getByRole('button', {
      name: 'Confirmar desativação',
    })
    expect(cancel).toHaveFocus()

    fireEvent.keyDown(dialog, { key: 'Tab', shiftKey: true })
    expect(confirm).toHaveFocus()
    fireEvent.keyDown(dialog, { key: 'Tab' })
    expect(cancel).toHaveFocus()

    fireEvent.keyDown(dialog, { key: 'Escape' })
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    const restoredTrigger = screen.getByRole('button', {
      name: 'Desativar cliente',
    })
    await waitFor(() => expect(restoredTrigger).toHaveFocus())

    fireEvent.click(restoredTrigger)
    fireEvent.click(screen.getByRole('button', { name: 'Cancelar' }))
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    await waitFor(() =>
      expect(
        screen.getByRole('button', { name: 'Desativar cliente' }),
      ).toHaveFocus(),
    )
  })

  it('ClientReactivate_PostsExactEndpointAndConvergesFromRefetch', async () => {
    const reactivatedClient = { ...clientB, isActive: true }
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(organizationResponse([]))
      .mockResolvedValueOnce(organizationResponse([organizationA]))
      .mockResolvedValueOnce(response(200, clientB))
      .mockResolvedValueOnce(response(200, { requestToken: 'test-token' }))
      .mockResolvedValueOnce(response(204))
      .mockResolvedValueOnce(response(200, reactivatedClient))
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(organizationA, clientB))
    await screen.findByText('Inativo')
    fireEvent.click(screen.getByRole('button', { name: 'Reativar cliente' }))

    expect(await screen.findByText('Ativo')).toBeInTheDocument()
    const [url, init] = fetchMock.mock.calls[4] as [string, RequestInit]
    expect(url).toBe(`/api/organizations/${organizationA.id}/clients/${clientB.id}/reactivate`)
    expect(init.method).toBe('POST')
    expect(init.body).toBeUndefined()
    expect(screen.getByRole('button', { name: 'Desativar cliente' })).toBeInTheDocument()
  })

  it('ClientDetail_NotFound_RendersGenericUnavailableStateWithoutResourceLeak', async () => {
    vi.stubGlobal(
      'fetch',
      authenticatedDetailFetch(organizationA, response(404, { detail: 'wrong tenant' })),
    )

    renderRoute(detailPath(organizationA, clientA))

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Cliente não encontrado ou indisponível.')
    expect(alert).not.toHaveTextContent('wrong tenant')
    expect(alert).not.toHaveTextContent(clientA.name)
  })

  it('ClientEdit_NotFound_RemovesPreviouslyRenderedClientData', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(organizationResponse([]))
      .mockResolvedValueOnce(organizationResponse([organizationA]))
      .mockResolvedValueOnce(response(200, clientA))
      .mockResolvedValueOnce(response(200, { requestToken: 'test-token' }))
      .mockResolvedValueOnce(response(404, { detail: 'private resource detail' }))
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(organizationA, clientA))
    await screen.findByRole('heading', { name: clientA.name })
    openEditAndSubmit('Nome indisponível')

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Cliente não encontrado ou indisponível.',
    )
    expect(screen.queryByText(clientA.name)).not.toBeInTheDocument()
    expect(screen.queryByText('private resource detail')).not.toBeInTheDocument()
  })

  it('ClientDetail_NetworkFailure_RetriesCurrentTenantResourceAndRecovers', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(organizationResponse([]))
      .mockResolvedValueOnce(organizationResponse([organizationA]))
      .mockRejectedValueOnce(new Error('private network detail'))
      .mockResolvedValueOnce(response(200, clientA))
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(organizationA, clientA))

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent(
      'Não foi possível carregar o cliente. Tente novamente.',
    )
    expect(alert).not.toHaveTextContent('private network detail')
    fireEvent.click(screen.getByRole('button', { name: 'Tentar novamente' }))

    expect(
      await screen.findByRole('heading', { name: clientA.name }),
    ).toBeInTheDocument()
    expect(fetchMock.mock.calls[3]?.[0]).toBe(
      `/api/organizations/${organizationA.id}/clients/${clientA.id}`,
    )
  })

  it('ClientDetail_MalformedClientId_DoesNotIssueClientRequest', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(organizationResponse([]))
      .mockResolvedValueOnce(organizationResponse([organizationA]))
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(`/organizations/${organizationA.id}/clients/not-a-guid`)

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Cliente não encontrado ou indisponível.',
    )
    expect(fetchMock).toHaveBeenCalledTimes(2)
  })

  it('ClientDetail_OldResourceResponseCompletesLast_RemainsOnCurrentClient', async () => {
    let resolveClientA: ((value: Response) => void) | undefined
    const pendingClientA = new Promise<Response>((resolve) => {
      resolveClientA = resolve
    })
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(organizationResponse([]))
      .mockResolvedValueOnce(organizationResponse([organizationA]))
      .mockReturnValueOnce(pendingClientA)
      .mockResolvedValueOnce(response(200, clientB))
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(detailPath(organizationA, clientA))

    await screen.findByText('Carregando cliente...')
    await act(async () => router.navigate(detailPath(organizationA, clientB)))
    expect(await screen.findByRole('heading', { name: clientB.name })).toBeInTheDocument()

    await act(async () => {
      resolveClientA?.(response(200, clientA))
      await pendingClientA
    })

    expect(screen.getByRole('heading', { name: clientB.name })).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: clientA.name })).not.toBeInTheDocument()
  })

  it('ClientFinanceSummary_ClientChange_IgnoresTheStaleResponse', async () => {
    const stale = deferred<ClientFinanceSummary>()
    vi.mocked(financeService.getClientFinanceSummary)
      .mockReturnValueOnce(stale.promise)
      .mockResolvedValueOnce(
        financeSummary({
          clientId: clientB.id,
          totalContractedAmount: '222.22',
        }),
      )
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(organizationResponse([]))
      .mockResolvedValueOnce(organizationResponse([organizationA]))
      .mockResolvedValueOnce(response(200, clientA))
      .mockResolvedValueOnce(response(200, clientB))
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(detailPath(organizationA, clientA))

    await screen.findByRole('heading', { name: clientA.name })
    await waitFor(() => {
      expect(financeService.getClientFinanceSummary).toHaveBeenCalledTimes(1)
    })
    await act(async () => router.navigate(detailPath(organizationA, clientB)))
    expect(await screen.findByText('R$ 222,22')).toBeInTheDocument()

    await act(async () => {
      stale.resolve(financeSummary({ totalContractedAmount: '777.77' }))
      await stale.promise
    })

    expect(screen.queryByText('R$ 777,77')).not.toBeInTheDocument()
    expect(screen.getByText('R$ 222,22')).toBeInTheDocument()
  })

  it('ClientDetail_OldOrganizationResponseCompletesLast_NeverRendersAcrossTenantContext', async () => {
    let resolveClientA: ((value: Response) => void) | undefined
    const pendingClientA = new Promise<Response>((resolve) => {
      resolveClientA = resolve
    })
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(organizationResponse([]))
      .mockResolvedValueOnce(organizationResponse([organizationA, organizationB]))
      .mockReturnValueOnce(pendingClientA)
      .mockResolvedValueOnce(response(200, clientB))
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(detailPath(organizationA, clientA))

    await screen.findByText('Carregando cliente...')
    await act(async () => router.navigate(detailPath(organizationB, clientB)))
    expect(await screen.findByRole('heading', { name: clientB.name })).toBeInTheDocument()

    await act(async () => {
      resolveClientA?.(response(200, clientA))
      await pendingClientA
    })

    expect(screen.getByRole('heading', { name: clientB.name })).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: clientA.name })).not.toBeInTheDocument()
  })

  it('ClientFinanceSummary_OrganizationChange_IgnoresTheStaleResponse', async () => {
    const stale = deferred<ClientFinanceSummary>()
    vi.mocked(financeService.getClientFinanceSummary)
      .mockReturnValueOnce(stale.promise)
      .mockResolvedValueOnce(
        financeSummary({
          clientId: clientB.id,
          totalContractedAmount: '333.33',
        }),
      )
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(organizationResponse([]))
      .mockResolvedValueOnce(
        organizationResponse([organizationA, organizationB]),
      )
      .mockResolvedValueOnce(response(200, clientA))
      .mockResolvedValueOnce(response(200, clientB))
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(detailPath(organizationA, clientA))

    await screen.findByRole('heading', { name: clientA.name })
    await waitFor(() => {
      expect(financeService.getClientFinanceSummary).toHaveBeenCalledTimes(1)
    })
    await act(async () => router.navigate(detailPath(organizationB, clientB)))
    expect(await screen.findByText('R$ 333,33')).toBeInTheDocument()

    await act(async () => {
      stale.resolve(financeSummary({ totalContractedAmount: '777.77' }))
      await stale.promise
    })

    expect(screen.queryByText('R$ 777,77')).not.toBeInTheDocument()
    expect(screen.getByText('R$ 333,33')).toBeInTheDocument()
  })

  it('ClientEdit_LateSuccessAfterNavigation_DoesNotRefetchOrOverwriteCurrentResource', async () => {
    let resolveUpdate: ((value: Response) => void) | undefined
    const pendingUpdate = new Promise<Response>((resolve) => {
      resolveUpdate = resolve
    })
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(organizationResponse([]))
      .mockResolvedValueOnce(organizationResponse([organizationA]))
      .mockResolvedValueOnce(response(200, clientA))
      .mockResolvedValueOnce(response(200, { requestToken: 'test-token' }))
      .mockReturnValueOnce(pendingUpdate)
      .mockResolvedValueOnce(response(200, clientB))
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(detailPath(organizationA, clientA))

    await screen.findByRole('heading', { name: clientA.name })
    openEditAndSubmit('Nome atrasado')
    await screen.findByRole('button', { name: 'Salvando...' })
    await act(async () => router.navigate(detailPath(organizationA, clientB)))
    expect(await screen.findByRole('heading', { name: clientB.name })).toBeInTheDocument()

    await act(async () => {
      resolveUpdate?.(response(204))
      await pendingUpdate
    })

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(6))
    expect(screen.getByRole('heading', { name: clientB.name })).toBeInTheDocument()
    expect(screen.queryByText('Nome atrasado')).not.toBeInTheDocument()
  })

  it('OrganizationSwitcher_FromClientDetail_NavigatesToSelectedWorkspaceRoot', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(organizationResponse([]))
      .mockResolvedValueOnce(organizationResponse([organizationA, organizationB]))
      .mockResolvedValueOnce(response(200, clientA))
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(detailPath(organizationA, clientA))

    await screen.findByRole('heading', { name: clientA.name })
    fireEvent.click(screen.getByRole('button', { name: 'Perfil' }))
    fireEvent.click(
      await screen.findByRole('button', {
        name: `Trocar para ${organizationB.name}`,
      }),
    )

    await waitFor(() => {
      expect(router.state.location.pathname).toBe(`/organizations/${organizationB.id}`)
    })
    expect(
      screen.getByRole('heading', {
        name: `Espaço de trabalho: ${organizationB.name}`,
      }),
    ).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: clientA.name })).not.toBeInTheDocument()
  })
})

describe('Clients 9D person type and documents on detail and edit', () => {
  const companyClient: ClientDetail = {
    ...clientA,
    name: 'Empresa Sintética Ltda',
    cpf: null,
    personType: 'company',
    cnpj: '11222333000181',
    address: 'Avenida Sintética, 200',
    notes: 'Linha 1\nLinha 2\nLinha 3',
  }

  function editFetch(detail: ClientDetail, updateResponse: Response) {
    return vi
      .fn()
      .mockResolvedValueOnce(organizationResponse([]))
      .mockResolvedValueOnce(organizationResponse([organizationA]))
      .mockResolvedValueOnce(response(200, detail))
      .mockResolvedValueOnce(response(200, { requestToken: 'test-token' }))
      .mockResolvedValueOnce(updateResponse)
      .mockResolvedValueOnce(response(200, detail))
  }

  function sentBody(fetchMock: ReturnType<typeof editFetch>): unknown {
    const [, init] = fetchMock.mock.calls[4] as [string, RequestInit]
    return JSON.parse(init.body as string)
  }

  function submitEdit() {
    fireEvent.submit(
      screen
        .getByRole('button', { name: 'Salvar alterações' })
        .closest('form')!,
    )
  }

  function profileValue(term: string): HTMLElement {
    const terms = screen.getAllByRole('term')
    const match = terms.find((element) => element.textContent === term)
    expect(match).toBeDefined()
    return match!.nextElementSibling as HTMLElement
  }

  it.each([
    ['Numeric', '11222333000181', '11.222.333/0001-81'],
    ['Alphanumeric', '12ABC34501DE35', '12.ABC.345/01DE-35'],
  ])(
    'ClientDetail_Company%sCnpj_RendersFormattedProfileForMember',
    async (_case, cnpj, formatted) => {
      const member = { ...organizationA, role: 'Member' as const }
      const client = { ...companyClient, cnpj }
      vi.stubGlobal(
        'fetch',
        authenticatedDetailFetch(member, response(200, client)),
      )

      renderRoute(detailPath(member, client))
      await screen.findByRole('heading', { name: client.name })

      expect(profileValue('Tipo de pessoa')).toHaveTextContent('Pessoa jurídica')
      expect(profileValue('CNPJ')).toHaveTextContent(formatted)
      expect(screen.queryByText(cnpj)).not.toBeInTheDocument()
      expect(
        screen.getAllByRole('term').map((element) => element.textContent),
      ).not.toContain('CPF')
      expect(profileValue('Endereço')).toHaveTextContent('Avenida Sintética, 200')
      expect(profileValue('Observações').textContent).toBe(
        'Linha 1\nLinha 2\nLinha 3',
      )
      expect(profileValue('Observações')).toHaveClass('client-property-notes')
      expect(
        screen.queryByRole('button', { name: 'Editar cliente' }),
      ).not.toBeInTheDocument()
      expect(document.title).not.toContain(cnpj)
    },
  )

  it('ClientDetail_IndividualWithoutOptionalFields_RendersNotInformed', async () => {
    vi.stubGlobal(
      'fetch',
      authenticatedDetailFetch(organizationA, response(200, clientB)),
    )

    renderRoute(detailPath(organizationA, clientB))
    await screen.findByRole('heading', { name: clientB.name })

    expect(profileValue('Tipo de pessoa')).toHaveTextContent('Pessoa física')
    expect(profileValue('CPF')).toHaveTextContent('Não informado')
    expect(profileValue('Endereço')).toHaveTextContent('Não informado')
    expect(profileValue('Observações')).toHaveTextContent('Não informado')
    expect(screen.getAllByText('Não informado')).toHaveLength(5)
  })

  it('ClientEdit_IndividualToCompany_ClearsCpfAndSendsCnpjKeepingOtherFields', async () => {
    const fetchMock = editFetch(clientA, response(204))
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(organizationA, clientA))
    await screen.findByRole('heading', { name: clientA.name })
    fireEvent.click(screen.getByRole('button', { name: 'Editar cliente' }))

    expect(screen.getByLabelText('CPF')).toHaveValue(clientA.cpf)
    expect(screen.getByLabelText('Endereço')).toHaveValue(clientA.address)
    expect(screen.getByLabelText('Observações')).toHaveValue(clientA.notes)

    fireEvent.click(screen.getByRole('button', { name: 'Pessoa jurídica' }))
    expect(screen.queryByLabelText('CPF')).not.toBeInTheDocument()
    expect(screen.getByLabelText('CNPJ')).toHaveValue('')
    expect(screen.getByLabelText('Razão social')).toHaveValue(clientA.name)

    fireEvent.change(screen.getByLabelText('CNPJ'), {
      target: { value: '12.abc.345/01de-35' },
    })
    submitEdit()

    await waitFor(() => {
      expect(fetchMock).toHaveBeenCalledTimes(6)
    })
    expect(sentBody(fetchMock)).toEqual({
      name: clientA.name,
      email: clientA.email,
      phone: clientA.phone,
      cpf: null,
      personType: 'company',
      cnpj: '12.ABC.345/01DE-35',
      address: clientA.address,
      notes: clientA.notes,
    })
  })

  it('ClientEdit_CompanyToIndividual_SendsCnpjAsNull', async () => {
    const fetchMock = editFetch(companyClient, response(204))
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(organizationA, companyClient))
    await screen.findByRole('heading', { name: companyClient.name })
    fireEvent.click(screen.getByRole('button', { name: 'Editar cliente' }))

    expect(screen.getByLabelText('CNPJ')).toHaveValue(companyClient.cnpj)
    fireEvent.click(screen.getByRole('button', { name: 'Pessoa física' }))
    expect(screen.getByLabelText('CPF')).toHaveValue('')
    submitEdit()

    await waitFor(() => {
      expect(fetchMock).toHaveBeenCalledTimes(6)
    })
    expect(sentBody(fetchMock)).toEqual({
      name: companyClient.name,
      email: companyClient.email,
      phone: companyClient.phone,
      cpf: null,
      personType: 'individual',
      cnpj: null,
      address: companyClient.address,
      notes: companyClient.notes,
    })
  })

  it.each([
    ['cpf', 'CPF', "Client CPF must be valid. (Parameter 'cpf')", 'CPF inválido.'],
    [
      'address',
      'Endereço',
      "Client address cannot exceed 300 characters. (Parameter 'address')",
      'O endereço deve ter no máximo 300 caracteres.',
    ],
    [
      'notes',
      'Observações',
      "Client notes cannot exceed 2000 characters. (Parameter 'notes')",
      'As observações devem ter no máximo 2000 caracteres.',
    ],
  ] as const)(
    'ClientEdit_BadRequest_%s_ShowsFieldMessageAndKeepsFormOpen',
    async (_parameter, label, detail, message) => {
      const fetchMock = editFetch(
        clientA,
        response(400, { title: 'Invalid request data', status: 400, detail }),
      )
      vi.stubGlobal('fetch', fetchMock)

      renderRoute(detailPath(organizationA, clientA))
      await screen.findByRole('heading', { name: clientA.name })
      fireEvent.click(screen.getByRole('button', { name: 'Editar cliente' }))
      submitEdit()

      const fieldMessage = await screen.findByText(message)
      expect(screen.getByLabelText(label)).toHaveAttribute(
        'aria-describedby',
        fieldMessage.id,
      )
      expect(screen.getByLabelText(label)).toHaveAttribute('aria-invalid', 'true')
      expect(screen.queryByText(/Parameter/)).not.toBeInTheDocument()
      expect(
        screen.queryByText('Não foi possível concluir a solicitação. Tente novamente.'),
      ).not.toBeInTheDocument()
      expect(
        screen.getByRole('button', { name: 'Salvar alterações' }),
      ).toBeEnabled()
      expect(fetchMock).toHaveBeenCalledTimes(5)
    },
  )

  it('ClientEdit_BadRequestWithoutKnownParameter_ShowsGenericMutationError', async () => {
    const fetchMock = editFetch(
      clientA,
      response(400, {
        title: 'Invalid request data',
        status: 400,
        detail: "Client person type must be a valid value. (Parameter 'personType')",
      }),
    )
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(organizationA, clientA))
    await screen.findByRole('heading', { name: clientA.name })
    fireEvent.click(screen.getByRole('button', { name: 'Editar cliente' }))
    submitEdit()

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent(
      'Não foi possível concluir a solicitação. Tente novamente.',
    )
    expect(alert).not.toHaveTextContent(/Parameter|person type/)
  })

  it('ClientEdit_DuplicateCnpj_ShowsMessageOnDocumentField', async () => {
    const fetchMock = editFetch(
      clientA,
      response(409, {
        title: 'Conflict',
        status: 409,
        detail: 'The document is already used by another client.',
      }),
    )
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(organizationA, clientA))
    await screen.findByRole('heading', { name: clientA.name })
    fireEvent.click(screen.getByRole('button', { name: 'Editar cliente' }))
    fireEvent.click(screen.getByRole('button', { name: 'Pessoa jurídica' }))
    fireEvent.change(screen.getByLabelText('CNPJ'), {
      target: { value: '11222333000181' },
    })
    submitEdit()

    const fieldMessage = await screen.findByText(
      'Já existe um cliente com este CNPJ neste escritório.',
    )
    expect(screen.getByLabelText('CNPJ')).toHaveAttribute(
      'aria-describedby',
      fieldMessage.id,
    )
    expect(
      screen.queryByText('The document is already used by another client.'),
    ).not.toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Editar cliente' })).toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(5)
  })

  it('ClientEdit_DuplicateCpf_ShowsMessageOnDocumentField', async () => {
    const fetchMock = editFetch(clientA, response(409))
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(organizationA, clientA))
    await screen.findByRole('heading', { name: clientA.name })
    fireEvent.click(screen.getByRole('button', { name: 'Editar cliente' }))
    submitEdit()

    expect(
      await screen.findByText('Já existe um cliente com este CPF neste escritório.'),
    ).toBeInTheDocument()
    expect(screen.getByLabelText('CPF')).toHaveAttribute('aria-invalid', 'true')
  })

  it('ClientDeactivate_Conflict_StaysGenericAndNeverShowsDocumentMessage', async () => {
    const fetchMock = editFetch(clientA, response(409))
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(organizationA, clientA))
    await screen.findByRole('heading', { name: clientA.name })
    fireEvent.click(screen.getByRole('button', { name: 'Desativar cliente' }))
    fireEvent.click(screen.getByRole('button', { name: 'Confirmar desativação' }))

    expect(
      await screen.findByText('Não foi possível concluir a solicitação. Tente novamente.'),
    ).toBeInTheDocument()
    expect(screen.queryByText(/Já existe um cliente/)).not.toBeInTheDocument()
  })
})
