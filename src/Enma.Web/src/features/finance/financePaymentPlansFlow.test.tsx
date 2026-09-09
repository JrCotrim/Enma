import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import type { ReactNode } from 'react'
import {
  createMemoryRouter,
  MemoryRouter,
  Route,
  RouterProvider,
  Routes,
  useLocation,
} from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { AuthContext, type AuthContextValue } from '../authentication/AuthContext'
import { clearCsrfToken } from '../authentication/csrfClient'
import {
  CurrentOrganizationContext,
  OrganizationDiscoveryContext,
} from '../organizations/OrganizationContext'
import type {
  OrganizationNavigationItem,
  OrganizationRole,
} from '../organizations/organizationTypes'
import { FinancePage } from './FinancePage'
import { PaymentPlanDetailsPage } from './PaymentPlanDetailsPage'
import type {
  FinanceOverview,
  ListPaymentPlansResponse,
  PaymentPlan,
  PaymentPlanSummary,
} from './financeTypes'

const organizationAId = '11111111-1111-4111-8111-111111111111'
const organizationBId = '22222222-2222-4222-8222-222222222222'
const clientAId = '33333333-3333-4333-8333-333333333333'
const clientBId = '44444444-4444-4444-8444-444444444444'
const paymentPlanAId = '55555555-5555-4555-8555-555555555555'
const paymentPlanBId = '66666666-6666-4666-8666-666666666666'

function organization(
  role: OrganizationRole,
  id = organizationAId,
): OrganizationNavigationItem {
  return {
    id,
    membershipId: `${id.slice(0, -1)}a`,
    name: id === organizationAId ? 'Organização Alfa' : 'Organização Beta',
    role,
  }
}

function overview(): FinanceOverview {
  return {
    referenceDate: '2026-09-08',
    totalContractedAmount: '1000.00',
    totalReceivedAmount: '250.00',
    totalOutstandingAmount: '750.00',
    overdueAmount: '100.00',
    dueTodayAmount: '50.00',
    upcomingAmount: '600.00',
    paymentPlanCount: 3,
    openPaymentPlanCount: 2,
    paidInstallmentCount: 1,
    overdueInstallmentCount: 1,
    dueTodayInstallmentCount: 1,
    upcomingInstallmentCount: 2,
  }
}

function planSummary(
  overrides: Partial<PaymentPlanSummary> = {},
): PaymentPlanSummary {
  return {
    id: paymentPlanAId,
    clientId: clientAId,
    clientName: 'Cliente Alfa',
    totalAmount: '90071992547409.93',
    installmentCount: 4,
    firstDueDate: '2026-09-10',
    createdAt: '2026-09-08T12:00:00-03:00',
    outstandingAmount: '12345678901234.56',
    overdueInstallmentCount: 1,
    nextDueDate: '2026-09-10',
    ...overrides,
  }
}

function listResponse(
  items: readonly PaymentPlanSummary[] = [planSummary()],
  overrides: Partial<ListPaymentPlansResponse> = {},
): ListPaymentPlansResponse {
  return {
    items,
    pageNumber: 1,
    pageSize: 20,
    hasNext: false,
    ...overrides,
  }
}

function paymentPlan(
  overrides: Partial<PaymentPlan> = {},
): PaymentPlan {
  return {
    id: paymentPlanAId,
    clientId: clientAId,
    clientName: 'Cliente Alfa',
    totalAmount: '90071992547409.93',
    installmentCount: 4,
    firstDueDate: '2026-09-10',
    createdAt: '2026-09-08T12:30:00-03:00',
    referenceDate: '2026-09-08',
    installments: [
      {
        id: '77777777-7777-4777-8777-777777777771',
        sequenceNumber: 1,
        amount: '10000000000000.01',
        dueDate: '2026-08-10',
        paidAt: '2026-08-08T10:45:00-03:00',
        status: 'Paid',
      },
      {
        id: '77777777-7777-4777-8777-777777777772',
        sequenceNumber: 2,
        amount: '200.02',
        dueDate: '2026-09-01',
        paidAt: '2026-09-01T08:00:00-03:00',
        status: 'Overdue',
      },
      {
        id: '77777777-7777-4777-8777-777777777773',
        sequenceNumber: 3,
        amount: '300.03',
        dueDate: '2026-09-08',
        paidAt: null,
        status: 'DueToday',
      },
      {
        id: '77777777-7777-4777-8777-777777777774',
        sequenceNumber: 4,
        amount: '400.04',
        dueDate: '2026-10-08',
        paidAt: null,
        status: 'Upcoming',
      },
    ],
    ...overrides,
  }
}

function response(status: number, body?: unknown): Response {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
  })
}

function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>((complete) => {
    resolve = complete
  })
  return { promise, resolve }
}

interface ContextProvidersProps {
  readonly children: ReactNode
  readonly currentOrganization: OrganizationNavigationItem
  readonly refreshOrganizations: () => void
  readonly handleUnauthorized: () => void
}

function ContextProviders({
  children,
  currentOrganization,
  refreshOrganizations,
  handleUnauthorized,
}: ContextProvidersProps) {
  const authValue: AuthContextValue = {
    state: 'authenticated',
    login: async () => 'authenticated',
    logout: async () => undefined,
    retrySessionCheck: () => undefined,
    handleUnauthorized,
  }
  const organizations = [currentOrganization]

  return (
    <AuthContext.Provider value={authValue}>
      <OrganizationDiscoveryContext.Provider
        value={{
          state: { status: 'success', organizations },
          refreshOrganizations,
        }}
      >
        <CurrentOrganizationContext.Provider
          value={{ currentOrganization, organizations }}
        >
          {children}
        </CurrentOrganizationContext.Provider>
      </OrganizationDiscoveryContext.Provider>
    </AuthContext.Provider>
  )
}

function LocationProbe() {
  const location = useLocation()
  return <output data-testid="location">{location.search}</output>
}

function renderFinance(
  options: {
    readonly role?: OrganizationRole
    readonly organizationId?: string
    readonly initialEntry?: string
    readonly refreshOrganizations?: () => void
    readonly handleUnauthorized?: () => void
  } = {},
) {
  const currentOrganization = organization(
    options.role ?? 'Owner',
    options.organizationId ?? organizationAId,
  )
  const refreshOrganizations = options.refreshOrganizations ?? vi.fn()
  const handleUnauthorized = options.handleUnauthorized ?? vi.fn()
  const initialEntry =
    options.initialEntry ?? `/organizations/${currentOrganization.id}/finance`
  const view = render(
    <ContextProviders
      currentOrganization={currentOrganization}
      refreshOrganizations={refreshOrganizations}
      handleUnauthorized={handleUnauthorized}
    >
      <MemoryRouter initialEntries={[initialEntry]}>
        <FinancePage />
        <LocationProbe />
      </MemoryRouter>
    </ContextProviders>,
  )

  return {
    ...view,
    currentOrganization,
    refreshOrganizations,
    handleUnauthorized,
  }
}

function renderDetail(
  options: {
    readonly role?: OrganizationRole
    readonly paymentPlanId?: string
    readonly refreshOrganizations?: () => void
    readonly handleUnauthorized?: () => void
  } = {},
) {
  const currentOrganization = organization(options.role ?? 'Owner')
  const refreshOrganizations = options.refreshOrganizations ?? vi.fn()
  const handleUnauthorized = options.handleUnauthorized ?? vi.fn()
  const currentPaymentPlanId = options.paymentPlanId ?? paymentPlanAId
  const path = `/organizations/${organizationAId}/finance/payment-plans/${currentPaymentPlanId}`
  const view = render(
    <ContextProviders
      currentOrganization={currentOrganization}
      refreshOrganizations={refreshOrganizations}
      handleUnauthorized={handleUnauthorized}
    >
      <MemoryRouter initialEntries={[path]}>
        <Routes>
          <Route
            path="/organizations/:organizationId/finance/payment-plans/:paymentPlanId"
            element={<PaymentPlanDetailsPage />}
          />
        </Routes>
      </MemoryRouter>
    </ContextProviders>,
  )

  return { ...view, refreshOrganizations, handleUnauthorized }
}

function financeFetch(
  getList: (url: string) => Response | Promise<Response> = () =>
    response(200, listResponse()),
) {
  return vi.fn((input: RequestInfo | URL) => {
    const url = String(input)
    if (url.endsWith('/finance/overview')) return Promise.resolve(response(200, overview()))
    if (url.includes('/clients/lookup?')) {
      return Promise.resolve(
        response(200, {
          items: [
            { id: clientAId, name: 'Cliente Alfa' },
            { id: clientBId, name: 'Cliente Beta' },
          ],
          pageNumber: 1,
          pageSize: 20,
          hasNext: false,
        }),
      )
    }
    return Promise.resolve(getList(url))
  })
}

function createFlowFetch(
  createResult: (
    url: string,
    init: RequestInit,
  ) => Response | Promise<Response> = () =>
    response(201, { paymentPlanId: paymentPlanBId }),
) {
  return vi.fn((input: RequestInfo | URL, init: RequestInit = {}) => {
    const url = String(input)
    if (url === '/api/auth/csrf') {
      return Promise.resolve(response(200, { requestToken: 'csrf-create-token' }))
    }
    if (url.endsWith('/finance/payment-plans') && init.method === 'POST') {
      return Promise.resolve(createResult(url, init))
    }
    if (url.endsWith('/finance/overview')) {
      return Promise.resolve(response(200, overview()))
    }
    if (url.includes('/clients/lookup?')) {
      return Promise.resolve(
        response(200, {
          items: [
            { id: clientAId, name: 'Cliente Alfa' },
            { id: clientBId, name: 'Cliente Beta' },
          ],
          pageNumber: 1,
          pageSize: 20,
          hasNext: false,
        }),
      )
    }
    if (url.includes('/finance/payment-plans?')) {
      return Promise.resolve(response(200, listResponse()))
    }
    throw new Error(`Unexpected request: ${init.method ?? 'GET'} ${url}`)
  })
}

async function openCreateForm() {
  fireEvent.click(screen.getByRole('button', { name: 'Novo plano' }))
  const heading = await screen.findByRole('heading', {
    name: 'Novo plano de pagamento',
  })
  return heading.closest('section')!
}

async function fillCreateForm(
  panel: HTMLElement,
  values: {
    readonly total?: string
    readonly installmentCount?: string
    readonly firstDueDate?: string
    readonly selectClient?: boolean
  } = {},
) {
  if (values.selectClient !== false) {
    fireEvent.click(
      await within(panel).findByRole('button', { name: 'Cliente Alfa' }),
    )
  }
  fireEvent.change(within(panel).getByLabelText('Total'), {
    target: { value: values.total ?? '120.00' },
  })
  fireEvent.change(within(panel).getByLabelText('Número de parcelas'), {
    target: { value: values.installmentCount ?? '12' },
  })
  fireEvent.change(within(panel).getByLabelText('Primeiro vencimento'), {
    target: { value: values.firstDueDate ?? '2026-10-01' },
  })
}

function submitCreateForm(panel: HTMLElement) {
  fireEvent.submit(
    within(panel).getByRole('button', { name: 'Criar plano' }).closest('form')!,
  )
}

beforeEach(clearCsrfToken)

afterEach(() => {
  clearCsrfToken()
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

describe('Finance payment plans list', () => {
  it.each(['Owner', 'Administrator'] as const)(
    '%s_LoadsThePaymentPlansList',
    async (role) => {
      const fetchMock = financeFetch()
      vi.stubGlobal('fetch', fetchMock)

      renderFinance({ role })

      expect(await screen.findByText('Cliente Alfa')).toBeInTheDocument()
      expect(fetchMock).toHaveBeenCalledWith(
        `/api/organizations/${organizationAId}/finance/payment-plans?pageNumber=1&pageSize=20`,
        expect.objectContaining({ method: 'GET', cache: 'no-store' }),
      )
    },
  )

  it('Member_BlocksBeforeOverviewListOrLookupRequests', () => {
    const fetchMock = vi.fn()
    vi.stubGlobal('fetch', fetchMock)

    renderFinance({ role: 'Member' })

    expect(screen.getByRole('heading', { name: 'Acesso negado' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Novo plano' })).not.toBeInTheDocument()
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('UrlClientAndPage_AreForwardedAndInvalidPageIsNormalized', async () => {
    const fetchMock = financeFetch(() => response(200, listResponse([], { pageNumber: 3 })))
    vi.stubGlobal('fetch', fetchMock)

    const firstView = renderFinance({
      initialEntry: `/organizations/${organizationAId}/finance?clientId=${clientAId}&page=3`,
    })

    await screen.findByText('Nenhum plano encontrado para este cliente.')
    expect(fetchMock).toHaveBeenCalledWith(
      `/api/organizations/${organizationAId}/finance/payment-plans?clientId=${clientAId}&pageNumber=3&pageSize=20`,
      expect.anything(),
    )
    firstView.unmount()

    const normalizedFetch = financeFetch(() => response(200, listResponse([])))
    vi.stubGlobal('fetch', normalizedFetch)
    const secondView = renderFinance({
      initialEntry: `/organizations/${organizationAId}/finance?page=invalid`,
    })
    await waitFor(() => expect(secondView.getByTestId('location')).toHaveTextContent(''))
    expect(normalizedFetch).toHaveBeenCalledWith(
      `/api/organizations/${organizationAId}/finance/payment-plans?pageNumber=1&pageSize=20`,
      expect.anything(),
    )
  })

  it('SelectingAClient_ResetsThePageToOneAndUsesTheSharedLookup', async () => {
    const fetchMock = financeFetch((url) => {
      const requestPage = new URL(url, 'http://localhost').searchParams.get('pageNumber')
      return response(200, listResponse([], { pageNumber: requestPage === '3' ? 3 : 1 }))
    })
    vi.stubGlobal('fetch', fetchMock)
    renderFinance({
      initialEntry: `/organizations/${organizationAId}/finance?clientId=${clientAId}&page=3`,
    })

    fireEvent.click(await screen.findByRole('button', { name: 'Trocar cliente' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Cliente Beta' }))

    await waitFor(() => {
      expect(screen.getByTestId('location')).toHaveTextContent(`?clientId=${clientBId}`)
    })
    expect(screen.getByTestId('location')).not.toHaveTextContent('page=')
    expect(fetchMock).toHaveBeenCalledWith(
      `/api/organizations/${organizationAId}/finance/payment-plans?clientId=${clientBId}&pageNumber=1&pageSize=20`,
      expect.anything(),
    )
  })

  it('PreviousAndNext_UseHasNextWithoutRefetchingOverview', async () => {
    const fetchMock = financeFetch((url) => {
      const page = new URL(url, 'http://localhost').searchParams.get('pageNumber')
      return response(
        200,
        listResponse([planSummary()], {
          pageNumber: page === '2' ? 2 : 1,
          hasNext: page !== '2',
        }),
      )
    })
    vi.stubGlobal('fetch', fetchMock)
    renderFinance()

    const previous = await screen.findByRole('button', { name: 'Página anterior' })
    const next = screen.getByRole('button', { name: 'Próxima página' })
    expect(previous).toBeDisabled()
    expect(next).toBeEnabled()

    fireEvent.click(next)
    await screen.findByText('Página 2')
    const pageTwoPrevious = screen.getByRole('button', { name: 'Página anterior' })
    expect(pageTwoPrevious).toBeEnabled()
    expect(screen.getByRole('button', { name: 'Próxima página' })).toBeDisabled()

    fireEvent.click(pageTwoPrevious)
    await waitFor(() => expect(screen.getByText('Página 1')).toBeInTheDocument())
    expect(
      fetchMock.mock.calls.filter(([input]) => String(input).endsWith('/finance/overview')),
    ).toHaveLength(1)
  })

  it.each([
    [undefined, 'Nenhum plano de pagamento cadastrado.'],
    [clientAId, 'Nenhum plano encontrado para este cliente.'],
  ])('EmptyState_UsesTheExpectedMessage(%s)', async (clientId, message) => {
    vi.stubGlobal('fetch', financeFetch(() => response(200, listResponse([]))))
    renderFinance({
      initialEntry: `/organizations/${organizationAId}/finance${
        clientId ? `?clientId=${clientId}` : ''
      }`,
    })

    expect(await screen.findByText(message)).toBeInTheDocument()
  })

  it('NetworkError_ShowsASafeMessageAndRetryRecovers', async () => {
    let listCalls = 0
    const fetchMock = financeFetch(() => {
      listCalls += 1
      if (listCalls === 1) return Promise.reject(new TypeError('private offline detail'))
      return response(200, listResponse())
    })
    vi.stubGlobal('fetch', fetchMock)
    renderFinance()

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent(
      'Não foi possível carregar os planos de pagamento. Tente novamente.',
    )
    expect(alert).not.toHaveTextContent('private offline detail')
    fireEvent.click(within(alert).getByRole('button', { name: 'Tentar novamente' }))
    expect(await screen.findByText('Cliente Alfa')).toBeInTheDocument()
    expect(listCalls).toBe(2)
  })

  it('Forbidden_RefreshesOrganizations', async () => {
    const refreshOrganizations = vi.fn()
    vi.stubGlobal('fetch', financeFetch(() => response(403, { detail: 'private' })))
    renderFinance({ refreshOrganizations })

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Seu acesso à organização pode ter mudado.')
    expect(alert).not.toHaveTextContent('private')
    expect(refreshOrganizations).toHaveBeenCalledTimes(1)
  })

  it('Unauthorized_DelegatesToTheSessionHandler', async () => {
    const handleUnauthorized = vi.fn()
    vi.stubGlobal('fetch', financeFetch(() => response(401)))
    renderFinance({ handleUnauthorized })

    await waitFor(() => expect(handleUnauthorized).toHaveBeenCalledTimes(1))
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('OrganizationChange_IgnoresAStaleListResponse', async () => {
    const stale = deferred<Response>()
    const fetchMock = vi.fn((input: RequestInfo | URL) => {
      const url = String(input)
      if (url.endsWith('/finance/overview')) return Promise.resolve(response(200, overview()))
      if (url.includes(organizationAId)) return stale.promise
      return Promise.resolve(
        response(
          200,
          listResponse([
            planSummary({ clientName: 'Cliente Atual', clientId: clientBId }),
          ]),
        ),
      )
    })
    vi.stubGlobal('fetch', fetchMock)
    const refreshOrganizations = vi.fn()
    const handleUnauthorized = vi.fn()
    const view = renderFinance({ refreshOrganizations, handleUnauthorized })

    view.rerender(
      <ContextProviders
        currentOrganization={organization('Owner', organizationBId)}
        refreshOrganizations={refreshOrganizations}
        handleUnauthorized={handleUnauthorized}
      >
        <MemoryRouter initialEntries={[`/organizations/${organizationBId}/finance`]}>
          <FinancePage />
        </MemoryRouter>
      </ContextProviders>,
    )

    expect(await screen.findByText('Cliente Atual')).toBeInTheDocument()
    await act(async () => {
      stale.resolve(
        response(200, listResponse([planSummary({ clientName: 'Cliente Antigo' })])),
      )
      await stale.promise
    })
    expect(screen.queryByText('Cliente Antigo')).not.toBeInTheDocument()
  })

  it('Rows_KeepExactMoneyDeriveThreeStatusesAndUseSemanticDetailLinks', async () => {
    const items = [
      planSummary(),
      planSummary({
        id: paymentPlanBId,
        clientName: 'Cliente Quitado',
        outstandingAmount: '0.0',
        overdueInstallmentCount: 0,
        nextDueDate: null,
      }),
      planSummary({
        id: '88888888-8888-4888-8888-888888888888',
        clientName: 'Cliente Em Aberto',
        outstandingAmount: '1.00',
        overdueInstallmentCount: 0,
      }),
    ]
    vi.stubGlobal(
      'fetch',
      financeFetch(() => response(200, listResponse(items))),
    )
    renderFinance()

    const table = await screen.findByRole('table')
    expect(within(table).getAllByText('Em atraso')).toHaveLength(1)
    expect(within(table).getByText('Quitado')).toBeInTheDocument()
    expect(
      within(table).getByText('Em aberto', { selector: '.finance-status' }),
    ).toBeInTheDocument()
    expect(within(table).getAllByText('R$ 90.071.992.547.409,93')).toHaveLength(3)
    expect(within(table).getByText('R$ 12.345.678.901.234,56')).toBeInTheDocument()
    expect(within(table).getAllByRole('link', { name: 'Ver detalhes' })[0]).toHaveAttribute(
      'href',
      `/organizations/${organizationAId}/finance/payment-plans/${paymentPlanAId}`,
    )
  })
})

describe('Finance payment plan creation', () => {
  it('Form_OpensInlineCancelsAndRestoresTriggerFocus', async () => {
    vi.stubGlobal('fetch', createFlowFetch())
    renderFinance()

    const trigger = await screen.findByRole('button', { name: 'Novo plano' })
    expect(trigger).toBeInTheDocument()
    const panel = await openCreateForm()

    expect(panel).toHaveAttribute('id', 'finance-create-payment-plan')
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    fireEvent.click(within(panel).getByRole('button', { name: 'Cancelar' }))
    expect(
      screen.queryByRole('heading', { name: 'Novo plano de pagamento' }),
    ).not.toBeInTheDocument()
    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Novo plano' })).toHaveFocus()
    })
  })

  it('Validation_RequiresAClientAndFocusesItsLookup', async () => {
    const fetchMock = createFlowFetch()
    vi.stubGlobal('fetch', fetchMock)
    renderFinance()
    const panel = await openCreateForm()
    await fillCreateForm(panel, { selectClient: false })

    submitCreateForm(panel)

    const error = within(panel).getByText('Escolha um cliente ativo.')
    const clientField = within(panel).getByRole('group', { name: 'Cliente' })
    expect(clientField).toHaveAttribute('aria-invalid', 'true')
    expect(clientField).toHaveAttribute('aria-describedby', error.id)
    expect(within(panel).getByLabelText('Buscar cliente ativo')).toHaveFocus()
    expect(fetchMock.mock.calls.some(([, init]) => init?.method === 'POST')).toBe(false)
  })

  it('Validation_RequiresTotalAndFocusesTheField', async () => {
    vi.stubGlobal('fetch', createFlowFetch())
    renderFinance()
    const panel = await openCreateForm()
    await fillCreateForm(panel, { total: '' })

    submitCreateForm(panel)

    const total = within(panel).getByLabelText('Total')
    expect(total).toHaveAttribute('aria-invalid', 'true')
    expect(total).toHaveAttribute('aria-describedby', 'finance-create-total-error')
    expect(within(panel).getByText('Informe o total.')).toBeInTheDocument()
    expect(total).toHaveFocus()
  })

  it.each([
    ['abc', 'Informe um total decimal válido.'],
    ['10.123', 'Use no máximo duas casas decimais.'],
    ['0', 'O total deve ser maior que zero.'],
  ])('Validation_RejectsInvalidTotal(%s)', async (value, message) => {
    const fetchMock = createFlowFetch()
    vi.stubGlobal('fetch', fetchMock)
    renderFinance()
    const panel = await openCreateForm()
    await fillCreateForm(panel, { total: value })

    submitCreateForm(panel)

    expect(within(panel).getByText(message)).toBeInTheDocument()
    expect(fetchMock.mock.calls.some(([, init]) => init?.method === 'POST')).toBe(false)
  })

  it('MaximumTotal_StaysAnExactStringAndSuccessRefetchesAndFilters', async () => {
    const fetchMock = createFlowFetch()
    vi.stubGlobal('fetch', fetchMock)
    renderFinance({
      initialEntry: `/organizations/${organizationAId}/finance?page=3`,
    })
    const panel = await openCreateForm()
    await fillCreateForm(panel, {
      total: '9999999999999999.99',
      installmentCount: '120',
    })

    expect(within(panel).getByText('R$ 9.999.999.999.999.999,99')).toBeInTheDocument()
    submitCreateForm(panel)

    const success = await screen.findByText(/Plano criado com sucesso/)
    const postCall = fetchMock.mock.calls.find(([, init]) => init?.method === 'POST')!
    const body = JSON.parse(postCall[1]?.body as string)
    expect(postCall[0]).toBe(
      `/api/organizations/${organizationAId}/finance/payment-plans`,
    )
    expect(postCall[1]).toEqual(
      expect.objectContaining({
        headers: {
          'Content-Type': 'application/json',
          'X-CSRF-TOKEN': 'csrf-create-token',
        },
      }),
    )
    expect(body).toEqual({
      clientId: clientAId,
      totalAmount: '9999999999999999.99',
      installmentCount: 120,
      firstDueDate: '2026-10-01',
    })
    expect(typeof body.totalAmount).toBe('string')
    expect(success).toHaveAttribute('role', 'status')
    expect(within(success).getByRole('link', { name: 'Ver plano' })).toHaveAttribute(
      'href',
      `/organizations/${organizationAId}/finance/payment-plans/${paymentPlanBId}`,
    )
    expect(
      screen.queryByRole('heading', { name: 'Novo plano de pagamento' }),
    ).not.toBeInTheDocument()
    await waitFor(() => {
      expect(screen.getByTestId('location')).toHaveTextContent(`?clientId=${clientAId}`)
      expect(
        fetchMock.mock.calls.filter(([input]) => String(input).endsWith('/finance/overview')),
      ).toHaveLength(2)
      expect(
        fetchMock.mock.calls.filter(([input, init]) =>
          String(input).includes('/finance/payment-plans?') && init?.method === 'GET'),
      ).toHaveLength(2)
    })
    expect(screen.getByTestId('location')).not.toHaveTextContent('page=')

    fireEvent.click(screen.getByRole('button', { name: 'Novo plano' }))
    const resetPanel = await screen.findByRole('heading', {
      name: 'Novo plano de pagamento',
    }).then((heading) => heading.closest('section')!)
    expect(within(resetPanel).getByLabelText('Total')).toHaveValue('')
    expect(within(resetPanel).getByLabelText('Número de parcelas')).toHaveValue(null)
    expect(within(resetPanel).getByLabelText('Primeiro vencimento')).toHaveValue('')
    expect(within(resetPanel).queryByText(/Selecionado:/)).not.toBeInTheDocument()
  })

  it('Validation_RejectsTotalAboveTheExactMaximum', async () => {
    const fetchMock = createFlowFetch()
    vi.stubGlobal('fetch', fetchMock)
    renderFinance()
    const panel = await openCreateForm()
    await fillCreateForm(panel, { total: '10000000000000000.00' })

    submitCreateForm(panel)

    expect(
      within(panel).getByText('O total não pode exceder 9999999999999999,99.'),
    ).toBeInTheDocument()
    expect(fetchMock.mock.calls.some(([, init]) => init?.method === 'POST')).toBe(false)
  })

  it.each(['0', '121', '1.5'])(
    'Validation_RequiresAnIntegerInstallmentCountFromOneTo120(%s)',
    async (value) => {
      const fetchMock = createFlowFetch()
      vi.stubGlobal('fetch', fetchMock)
      renderFinance()
      const panel = await openCreateForm()
      await fillCreateForm(panel, { installmentCount: value })

      submitCreateForm(panel)

      expect(
        within(panel).getByText('Informe um número inteiro entre 1 e 120.'),
      ).toBeInTheDocument()
      expect(fetchMock.mock.calls.some(([, init]) => init?.method === 'POST')).toBe(false)
    },
  )

  it('Validation_RejectsMoreInstallmentsThanTotalCents', async () => {
    const fetchMock = createFlowFetch()
    vi.stubGlobal('fetch', fetchMock)
    renderFinance()
    const panel = await openCreateForm()
    await fillCreateForm(panel, { total: '0.01', installmentCount: '2' })

    submitCreateForm(panel)

    expect(
      within(panel).getByText(
        'O número de parcelas não pode superar o total em centavos.',
      ),
    ).toBeInTheDocument()
    expect(fetchMock.mock.calls.some(([, init]) => init?.method === 'POST')).toBe(false)
  })

  it('Validation_RequiresTheFirstDueDate', async () => {
    vi.stubGlobal('fetch', createFlowFetch())
    renderFinance()
    const panel = await openCreateForm()
    await fillCreateForm(panel, { firstDueDate: '' })

    submitCreateForm(panel)

    const dueDate = within(panel).getByLabelText('Primeiro vencimento')
    expect(within(panel).getByText('Informe o primeiro vencimento.')).toBeInTheDocument()
    expect(dueDate).toHaveAttribute('aria-invalid', 'true')
    expect(dueDate).toHaveFocus()
  })

  it('PastDate_IsAllowedAndCommaMoneyIsNormalizedWithoutNumberConversion', async () => {
    const fetchMock = createFlowFetch()
    vi.stubGlobal('fetch', fetchMock)
    renderFinance()
    const panel = await openCreateForm()
    await fillCreateForm(panel, {
      total: '0,01',
      installmentCount: '1',
      firstDueDate: '2000-01-01',
    })

    submitCreateForm(panel)

    await screen.findByText(/Plano criado com sucesso/)
    const postCall = fetchMock.mock.calls.find(([, init]) => init?.method === 'POST')!
    expect(JSON.parse(postCall[1]?.body as string)).toEqual(
      expect.objectContaining({ totalAmount: '0.01', firstDueDate: '2000-01-01' }),
    )
  })

  it('BadRequest_ShowsOnlySafeFeedbackBesideTheForm', async () => {
    vi.stubGlobal(
      'fetch',
      createFlowFetch(() => response(400, { detail: 'private validation detail' })),
    )
    renderFinance()
    const panel = await openCreateForm()
    await fillCreateForm(panel)
    submitCreateForm(panel)

    const alert = await within(panel).findByRole('alert')
    expect(alert).toHaveTextContent(
      'Não foi possível criar o plano. Revise os dados e tente novamente.',
    )
    expect(alert).not.toHaveTextContent('private validation detail')
  })

  it('NotFound_ClearsTheUnavailableClientAndRequiresAnotherSelection', async () => {
    vi.stubGlobal('fetch', createFlowFetch(() => response(404)))
    renderFinance()
    const panel = await openCreateForm()
    await fillCreateForm(panel)
    submitCreateForm(panel)

    expect(await within(panel).findByText(
      'O cliente selecionado não está mais disponível. Escolha outro cliente.',
    )).toBeInTheDocument()
    expect(within(panel).queryByText(/Selecionado:/)).not.toBeInTheDocument()
    expect(within(panel).getByLabelText('Buscar cliente ativo')).toHaveFocus()
  })

  it('Forbidden_RefreshesOrganizationsAndStopsTheAction', async () => {
    const refreshOrganizations = vi.fn()
    vi.stubGlobal('fetch', createFlowFetch(() => response(403)))
    renderFinance({ refreshOrganizations })
    const panel = await openCreateForm()
    await fillCreateForm(panel)
    submitCreateForm(panel)

    expect(await within(panel).findByText(
      'Seu acesso à organização mudou. Atualize o acesso antes de tentar novamente.',
    )).toBeInTheDocument()
    expect(refreshOrganizations).toHaveBeenCalledTimes(1)
    expect(screen.queryByText(/Plano criado com sucesso/)).not.toBeInTheDocument()
  })

  it('Unauthorized_DelegatesToSessionHandlingWithoutExposingAnError', async () => {
    const handleUnauthorized = vi.fn()
    vi.stubGlobal('fetch', createFlowFetch(() => response(401)))
    renderFinance({ handleUnauthorized })
    const panel = await openCreateForm()
    await fillCreateForm(panel)
    submitCreateForm(panel)

    await waitFor(() => expect(handleUnauthorized).toHaveBeenCalledTimes(1))
    expect(within(panel).queryByText(/Não foi possível criar/)).not.toBeInTheDocument()
  })

  it('NetworkFailure_IsSafeAndASecondSubmitRetries', async () => {
    let postCalls = 0
    const fetchMock = createFlowFetch(() => {
      postCalls += 1
      return postCalls === 1
        ? Promise.reject(new TypeError('private network detail'))
        : response(201, { paymentPlanId: paymentPlanBId })
    })
    vi.stubGlobal('fetch', fetchMock)
    renderFinance()
    const panel = await openCreateForm()
    await fillCreateForm(panel)
    submitCreateForm(panel)

    const alert = await within(panel).findByRole('alert')
    expect(alert).toHaveTextContent(
      'Não foi possível criar o plano de pagamento. Tente novamente.',
    )
    expect(alert).not.toHaveTextContent('private network detail')
    submitCreateForm(panel)
    expect(await screen.findByText(/Plano criado com sucesso/)).toBeInTheDocument()
    expect(postCalls).toBe(2)
  })

  it('PendingSubmit_DisablesActionsAndPreventsDuplicatePosts', async () => {
    const pending = deferred<Response>()
    const fetchMock = createFlowFetch(() => pending.promise)
    vi.stubGlobal('fetch', fetchMock)
    renderFinance()
    const panel = await openCreateForm()
    await fillCreateForm(panel)

    submitCreateForm(panel)
    fireEvent.submit(within(panel).getByRole('button', { name: 'Criando…' }).closest('form')!)

    expect(within(panel).getByRole('button', { name: 'Criando…' })).toBeDisabled()
    expect(within(panel).getByRole('button', { name: 'Cancelar' })).toBeDisabled()
    expect(within(panel).getByText('Criando plano de pagamento…')).toHaveAttribute(
      'role',
      'status',
    )
    await waitFor(() => {
      expect(fetchMock.mock.calls.filter(([, init]) => init?.method === 'POST')).toHaveLength(1)
    })
    await act(async () => {
      pending.resolve(response(201, { paymentPlanId: paymentPlanBId }))
      await pending.promise
    })
    expect(await screen.findByText(/Plano criado com sucesso/)).toBeInTheDocument()
  })

  it.each([
    ['server', () => response(500, { detail: 'private server detail' })],
    ['malformed', () => response(201, { paymentPlanId: 'invalid' })],
  ])('%sFailure_IsSafeAndRetryable', async (_name, failure) => {
    let postCalls = 0
    const fetchMock = createFlowFetch(() => {
      postCalls += 1
      return postCalls === 1
        ? failure()
        : response(201, { paymentPlanId: paymentPlanBId })
    })
    vi.stubGlobal('fetch', fetchMock)
    renderFinance()
    const panel = await openCreateForm()
    await fillCreateForm(panel)
    submitCreateForm(panel)

    expect(await within(panel).findByText(
      'Não foi possível criar o plano de pagamento. Tente novamente.',
    )).toBeInTheDocument()
    expect(panel).not.toHaveTextContent(/private server detail|invalid/)
    submitCreateForm(panel)
    expect(await screen.findByText(/Plano criado com sucesso/)).toBeInTheDocument()
  })

  it('OrganizationChange_AbortsAndIgnoresTheStaleCreateResponse', async () => {
    const stale = deferred<Response>()
    const fetchMock = createFlowFetch((_url, init) => {
      expect(init.signal).toBeInstanceOf(AbortSignal)
      return stale.promise
    })
    vi.stubGlobal('fetch', fetchMock)
    const refreshOrganizations = vi.fn()
    const handleUnauthorized = vi.fn()
    const view = renderFinance({ refreshOrganizations, handleUnauthorized })
    const panel = await openCreateForm()
    await fillCreateForm(panel)
    submitCreateForm(panel)

    view.rerender(
      <ContextProviders
        currentOrganization={organization('Owner', organizationBId)}
        refreshOrganizations={refreshOrganizations}
        handleUnauthorized={handleUnauthorized}
      >
        <MemoryRouter initialEntries={[`/organizations/${organizationBId}/finance`]}>
          <FinancePage />
          <LocationProbe />
        </MemoryRouter>
      </ContextProviders>,
    )

    expect(await screen.findByRole('heading', {
      name: 'Novo plano de pagamento',
    })).toBeInTheDocument()
    await act(async () => {
      stale.resolve(response(201, { paymentPlanId: paymentPlanBId }))
      await stale.promise
    })
    expect(screen.queryByText(/Plano criado com sucesso/)).not.toBeInTheDocument()
    expect(screen.getByTestId('location')).not.toHaveTextContent('clientId=')
  })
})

describe('Payment plan detail', () => {
  it('LoadsTheTenantRouteAndRendersExactStatusesMoneyDatesAndPaidAt', async () => {
    const fetchMock = vi.fn().mockResolvedValue(response(200, paymentPlan()))
    vi.stubGlobal('fetch', fetchMock)
    const view = renderDetail()

    const table = await screen.findByRole('table')
    expect(fetchMock).toHaveBeenCalledWith(
      `/api/organizations/${organizationAId}/finance/payment-plans/${paymentPlanAId}`,
      expect.objectContaining({ method: 'GET', cache: 'no-store' }),
    )
    for (const label of ['Paga', 'Em atraso', 'Vence hoje', 'A vencer']) {
      expect(within(table).getByText(label)).toBeInTheDocument()
    }
    expect(screen.getByText('R$ 90.071.992.547.409,93')).toBeInTheDocument()
    expect(within(table).getByText('R$ 10.000.000.000.000,01')).toBeInTheDocument()
    expect(screen.getByText('10/09/2026')).toBeInTheDocument()
    expect(screen.getAllByText('08/09/2026')).toHaveLength(2)
    expect(view.container.querySelectorAll('time')).toHaveLength(2)
    expect(
      view.container.querySelector('time[datetime="2026-08-08T10:45:00-03:00"]'),
    ).toBeInTheDocument()
    expect(
      view.container.querySelector('time[datetime="2026-09-01T08:00:00-03:00"]'),
    ).not.toBeInTheDocument()
  })

  it('NotFound_ShowsTheExpectedMessageAndReturnLink', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(response(404)))
    renderDetail()

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Plano não encontrado')
    expect(within(alert).getByRole('link', { name: 'Voltar para Financeiro' })).toHaveAttribute(
      'href',
      `/organizations/${organizationAId}/finance`,
    )
  })

  it('Forbidden_RefreshesOrganizations', async () => {
    const refreshOrganizations = vi.fn()
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(response(403, { detail: 'private' })))
    renderDetail({ refreshOrganizations })

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Seu acesso à organização pode ter mudado.')
    expect(alert).not.toHaveTextContent('private')
    expect(refreshOrganizations).toHaveBeenCalledTimes(1)
  })

  it('Unauthorized_DelegatesToTheSessionHandler', async () => {
    const handleUnauthorized = vi.fn()
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(response(401)))
    renderDetail({ handleUnauthorized })

    await waitFor(() => expect(handleUnauthorized).toHaveBeenCalledTimes(1))
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it.each([
    ['network', () => Promise.reject(new TypeError('private network detail'))],
    ['malformed', () => Promise.resolve(response(200, { totalAmount: 123.45 }))],
  ])('%sError_IsSafeAndRetryRecovers', async (_name, firstResult) => {
    const fetchMock = vi
      .fn()
      .mockImplementationOnce(firstResult)
      .mockResolvedValue(response(200, paymentPlan()))
    vi.stubGlobal('fetch', fetchMock)
    renderDetail()

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent(
      'Não foi possível carregar o plano de pagamento. Tente novamente.',
    )
    expect(alert).not.toHaveTextContent(/private network detail|123\.45/)
    fireEvent.click(within(alert).getByRole('button', { name: 'Tentar novamente' }))
    expect(await screen.findByText('Cliente Alfa')).toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(2)
  })

  it('PaymentPlanIdChange_IgnoresTheStaleResponse', async () => {
    const stale = deferred<Response>()
    const fetchMock = vi.fn((input: RequestInfo | URL) => {
      const url = String(input)
      if (url.endsWith(paymentPlanAId)) return stale.promise
      return Promise.resolve(
        response(200, paymentPlan({ id: paymentPlanBId, clientName: 'Cliente Atual' })),
      )
    })
    vi.stubGlobal('fetch', fetchMock)
    const routePath = '/organizations/:organizationId/finance/payment-plans/:paymentPlanId'
    const router = createMemoryRouter(
      [{ path: routePath, element: <PaymentPlanDetailsPage /> }],
      {
        initialEntries: [
          `/organizations/${organizationAId}/finance/payment-plans/${paymentPlanAId}`,
        ],
      },
    )
    render(
      <ContextProviders
        currentOrganization={organization('Owner')}
        refreshOrganizations={vi.fn()}
        handleUnauthorized={vi.fn()}
      >
        <RouterProvider router={router} />
      </ContextProviders>,
    )

    await act(async () => {
      await router.navigate(
        `/organizations/${organizationAId}/finance/payment-plans/${paymentPlanBId}`,
      )
    })
    expect(await screen.findByText('Cliente Atual')).toBeInTheDocument()
    await act(async () => {
      stale.resolve(
        response(200, paymentPlan({ clientName: 'Cliente Antigo' })),
      )
      await stale.promise
    })
    expect(screen.queryByText('Cliente Antigo')).not.toBeInTheDocument()
  })

  it('Member_BlocksBeforeTheDetailRequest', () => {
    const fetchMock = vi.fn()
    vi.stubGlobal('fetch', fetchMock)
    renderDetail({ role: 'Member' })

    expect(screen.getByRole('heading', { name: 'Acesso negado' })).toBeInTheDocument()
    expect(fetchMock).not.toHaveBeenCalled()
  })
})
