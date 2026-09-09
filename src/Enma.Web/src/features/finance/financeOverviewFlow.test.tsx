import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import type { ReactNode } from 'react'
import { MemoryRouter } from 'react-router-dom'
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
import * as financeService from './financeService'
import type { FinanceOverview } from './financeTypes'

const organizationAId = '11111111-1111-4111-8111-111111111111'
const organizationBId = '22222222-2222-4222-8222-222222222222'

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

function overview(
  overrides: Partial<FinanceOverview> = {},
): FinanceOverview {
  return {
    referenceDate: '2026-09-08',
    totalContractedAmount: '1234.56',
    totalReceivedAmount: '90071992547409.93',
    totalOutstandingAmount: '9999999999999999.99',
    overdueAmount: '321.09',
    dueTodayAmount: '10.00',
    upcomingAmount: '20.00',
    paymentPlanCount: 5,
    openPaymentPlanCount: 2,
    paidInstallmentCount: 7,
    overdueInstallmentCount: 3,
    dueTodayInstallmentCount: 1,
    upcomingInstallmentCount: 4,
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

interface FinanceProvidersProps {
  readonly children: ReactNode
  readonly currentOrganization: OrganizationNavigationItem
  readonly refreshOrganizations: () => void
  readonly handleUnauthorized: () => void
}

function FinanceProviders({
  children,
  currentOrganization,
  refreshOrganizations,
  handleUnauthorized,
}: FinanceProvidersProps) {
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
          <MemoryRouter>{children}</MemoryRouter>
        </CurrentOrganizationContext.Provider>
      </OrganizationDiscoveryContext.Provider>
    </AuthContext.Provider>
  )
}

function renderFinance(
  currentOrganization = organization('Owner'),
  refreshOrganizations = vi.fn(),
  handleUnauthorized = vi.fn(),
) {
  const view = render(
    <FinanceProviders
      currentOrganization={currentOrganization}
      refreshOrganizations={refreshOrganizations}
      handleUnauthorized={handleUnauthorized}
    >
      <FinancePage />
    </FinanceProviders>,
  )

  return { ...view, refreshOrganizations, handleUnauthorized }
}

beforeEach(() => {
  clearCsrfToken()
  vi.spyOn(financeService, 'listPaymentPlans').mockResolvedValue({
    items: [],
    pageNumber: 1,
    pageSize: 20,
    hasNext: false,
  })
})

afterEach(() => {
  clearCsrfToken()
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

describe('Finance overview access and states', () => {
  it('Owner_LoadsAndRendersTheCompleteOverviewWithExactMoney', async () => {
    const fetchMock = vi.fn().mockResolvedValue(response(200, overview()))
    vi.stubGlobal('fetch', fetchMock)

    renderFinance()

    expect(await screen.findByText('R$ 1.234,56')).toBeInTheDocument()
    expect(screen.getByText('R$ 90.071.992.547.409,93')).toBeInTheDocument()
    expect(screen.getByText('R$ 9.999.999.999.999.999,99')).toBeInTheDocument()
    expect(screen.getByText('Posição em 08/09/2026')).toBeInTheDocument()
    expect(screen.getByText('7 parcelas')).toBeInTheDocument()
    expect(screen.getByText('3 parcelas')).toBeInTheDocument()
    expect(screen.getByText('1 parcela')).toBeInTheDocument()
    expect(screen.getByText('4 parcelas')).toBeInTheDocument()
    expect(screen.getByText('2 de 5')).toBeInTheDocument()
    expect(screen.getByText('2 planos abertos')).toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledWith(
      `/api/organizations/${organizationAId}/finance/overview`,
      expect.objectContaining({
        method: 'GET',
        cache: 'no-store',
        signal: expect.any(AbortSignal),
      }),
    )
  })

  it('Administrator_LoadsTheOverview', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(response(200, overview())))

    renderFinance(organization('Administrator'))

    expect(await screen.findByText('Posição em 08/09/2026')).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Resumo financeiro' })).toBeInTheDocument()
  })

  it('Member_BlocksBeforeAnyFinanceRequest', () => {
    const fetchMock = vi.fn()
    vi.stubGlobal('fetch', fetchMock)

    renderFinance(organization('Member'))

    expect(screen.getByRole('heading', { name: 'Acesso negado' })).toBeInTheDocument()
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('Loading_UsesANonBlockingBusyStatus', () => {
    const pending = deferred<Response>()
    vi.stubGlobal('fetch', vi.fn().mockReturnValue(pending.promise))

    renderFinance()

    expect(screen.getByText('Carregando resumo financeiro...')).toHaveAttribute(
      'role',
      'status',
    )
    expect(screen.getByRole('region', { name: 'Resumo financeiro' })).toHaveAttribute(
      'aria-busy',
      'true',
    )
  })

  it('ZeroOverview_RendersValuesAndTheEmptyMessage', async () => {
    const zeroOverview = overview({
      totalContractedAmount: '0',
      totalReceivedAmount: '0.0',
      totalOutstandingAmount: '0.00',
      overdueAmount: '0',
      dueTodayAmount: '0.0',
      upcomingAmount: '0.00',
      paymentPlanCount: 0,
      openPaymentPlanCount: 0,
      paidInstallmentCount: 0,
      overdueInstallmentCount: 0,
      dueTodayInstallmentCount: 0,
      upcomingInstallmentCount: 0,
    })
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(response(200, zeroOverview)))

    renderFinance()

    expect(await screen.findByText('Nenhum plano financeiro cadastrado ainda.')).toBeInTheDocument()
    expect(screen.getAllByText('R$ 0,00')).toHaveLength(6)
  })

  it('MalformedResponse_ShowsOnlyTheSafeError', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        response(200, {
          ...overview(),
          totalContractedAmount: 1234.56,
          detail: 'private server reason',
        }),
      ),
    )

    renderFinance()

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Não foi possível carregar o resumo financeiro. Tente novamente.')
    expect(alert).not.toHaveTextContent('private server reason')
  })

  it.each([
    ['network', () => Promise.reject(new TypeError('offline private detail'))],
    ['5xx', () => Promise.resolve(response(500, { detail: 'private server reason' }))],
  ])('%sFailure_ShowsSafeErrorAndRetry', async (_name, result) => {
    vi.stubGlobal('fetch', vi.fn().mockImplementation(result))

    renderFinance()

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Não foi possível carregar o resumo financeiro. Tente novamente.')
    expect(screen.getByRole('button', { name: 'Tentar novamente' })).toBeInTheDocument()
    expect(alert).not.toHaveTextContent(/private detail|private server reason/)
  })

  it('Retry_StartsANewRequestAndRecovers', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(response(500))
      .mockResolvedValueOnce(response(200, overview({ totalContractedAmount: '88.75' })))
    vi.stubGlobal('fetch', fetchMock)
    renderFinance()

    fireEvent.click(await screen.findByRole('button', { name: 'Tentar novamente' }))

    expect(await screen.findByText('R$ 88,75')).toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(2)
  })

  it('Forbidden_RefreshesOrganizationsAndOffersAccessRecovery', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(response(403, { detail: 'private server reason' })),
    )
    const refreshOrganizations = vi.fn()
    renderFinance(organization('Owner'), refreshOrganizations)

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('O acesso ao Financeiro pode ter mudado.')
    expect(alert).not.toHaveTextContent('private server reason')
    expect(refreshOrganizations).toHaveBeenCalledTimes(1)

    fireEvent.click(screen.getByRole('button', { name: 'Atualizar acesso' }))
    expect(refreshOrganizations).toHaveBeenCalledTimes(2)
  })

  it('Unauthorized_UsesTheEstablishedSessionHandler', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(response(401)))
    const handleUnauthorized = vi.fn()
    renderFinance(organization('Owner'), vi.fn(), handleUnauthorized)

    await waitFor(() => expect(handleUnauthorized).toHaveBeenCalledTimes(1))
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('OrganizationChange_IgnoresTheStaleResponse', async () => {
    const stale = deferred<Response>()
    const currentOverview = overview({ totalContractedAmount: '222.22' })
    const fetchMock = vi
      .fn()
      .mockReturnValueOnce(stale.promise)
      .mockResolvedValueOnce(response(200, currentOverview))
    vi.stubGlobal('fetch', fetchMock)
    const refreshOrganizations = vi.fn()
    const handleUnauthorized = vi.fn()
    const initialOrganization = organization('Owner')
    const nextOrganization = organization('Owner', organizationBId)
    const view = renderFinance(
      initialOrganization,
      refreshOrganizations,
      handleUnauthorized,
    )

    view.rerender(
      <FinanceProviders
        currentOrganization={nextOrganization}
        refreshOrganizations={refreshOrganizations}
        handleUnauthorized={handleUnauthorized}
      >
        <FinancePage />
      </FinanceProviders>,
    )

    expect(await screen.findByText('R$ 222,22')).toBeInTheDocument()
    await act(async () => {
      stale.resolve(
        response(200, overview({ totalContractedAmount: '777.77' })),
      )
      await stale.promise
    })

    expect(screen.queryByText('R$ 777,77')).not.toBeInTheDocument()
    expect(screen.getByText('R$ 222,22')).toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(2)
  })
})
