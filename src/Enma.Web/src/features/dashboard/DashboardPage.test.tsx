import { act, fireEvent, render, screen } from '@testing-library/react'
import { StrictMode, useState, type ReactNode } from 'react'
import {
  createMemoryRouter,
  Outlet,
  RouterProvider,
} from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { AuthContext, type AuthContextValue } from '../authentication/AuthContext'
import {
  CurrentOrganizationContext,
  OrganizationDiscoveryContext,
} from '../organizations/OrganizationContext'
import type { OrganizationNavigationItem } from '../organizations/organizationTypes'
import { DashboardPage } from './DashboardPage'
import {
  DashboardRequestError,
  getDashboard,
} from './dashboardService'
import type { DashboardResponse } from './dashboardTypes'
import {
  getDashboardSupplementaryData,
  type DashboardSupplementaryData,
} from './dashboardSupplementaryService'

vi.mock('./dashboardService', async () => {
  const actual = await vi.importActual<typeof import('./dashboardService')>(
    './dashboardService',
  )
  return { ...actual, getDashboard: vi.fn() }
})

vi.mock('./dashboardSupplementaryService', async () => {
  const actual = await vi.importActual<
    typeof import('./dashboardSupplementaryService')
  >('./dashboardSupplementaryService')
  return { ...actual, getDashboardSupplementaryData: vi.fn() }
})

const organizationA: OrganizationNavigationItem = {
  id: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
  membershipId: '11111111-1111-4111-8111-111111111111',
  name: 'Organização Alfa',
  role: 'Member',
}
const organizationB: OrganizationNavigationItem = {
  id: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',
  membershipId: '22222222-2222-4222-8222-222222222222',
  name: 'Organização Beta',
  role: 'Owner',
}
const deadlineId = '33333333-3333-4333-8333-333333333333'
const taskId = '44444444-4444-4444-8444-444444444444'
const eventId = '55555555-5555-4555-8555-555555555555'

function dashboard(overrides: Partial<DashboardResponse> = {}): DashboardResponse {
  return {
    referenceDate: '2026-08-24',
    summary: {
      activeClients: 12,
      totalLegalProcesses: 18,
      pendingDeadlines: 5,
      pendingTasks: 9,
    },
    attention: {
      deadlines: { overdue: 1, dueToday: 2, dueInNextSevenDays: 2 },
      tasks: { overdue: 2, dueToday: 1, dueInNextSevenDays: 3 },
    },
    upcoming: {
      throughDate: '2026-08-31',
      deadlines: [],
      tasks: [],
      calendarEvents: [],
    },
    ...overrides,
  }
}

function supplementary(
  overrides: Partial<DashboardSupplementaryData> = {},
): DashboardSupplementaryData {
  return {
    processes: { status: 'success', items: [] },
    documents: { status: 'success', items: [] },
    invitations: null,
    auditLogs: null,
    team: { status: 'success', items: [], totalCount: 0 },
    ...overrides,
  }
}

function deferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (reason?: unknown) => void
  const promise = new Promise<T>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise
    reject = rejectPromise
  })
  return { promise, resolve, reject }
}

const authValue: AuthContextValue = {
  state: 'authenticated',
  login: vi.fn(),
  logout: vi.fn(),
  retrySessionCheck: vi.fn(),
  handleUnauthorized: vi.fn(),
}

function Providers({
  children,
  enableSwitch = false,
  refreshOrganizations = vi.fn(),
}: {
  readonly children: ReactNode
  readonly enableSwitch?: boolean
  readonly refreshOrganizations?: () => void
}) {
  const [currentOrganization, setCurrentOrganization] = useState(organizationA)
  return (
    <AuthContext.Provider value={authValue}>
      <OrganizationDiscoveryContext.Provider
        value={{
          state: {
            status: 'success',
            organizations: [organizationA, organizationB],
          },
          refreshOrganizations,
        }}
      >
        <CurrentOrganizationContext.Provider
          value={{
            currentOrganization,
            organizations: [organizationA, organizationB],
          }}
        >
          {enableSwitch ? (
            <button
              type="button"
              onClick={() => setCurrentOrganization(organizationB)}
            >
              Trocar organização
            </button>
          ) : null}
          {children}
        </CurrentOrganizationContext.Provider>
      </OrganizationDiscoveryContext.Provider>
    </AuthContext.Provider>
  )
}

function renderDashboard(options?: {
  readonly enableSwitch?: boolean
  readonly refreshOrganizations?: () => void
  readonly strictMode?: boolean
}) {
  const router = createMemoryRouter(
    [
      {
        path: '/organizations/:organizationId',
        element: (
          <Providers {...options}>
            <Outlet />
          </Providers>
        ),
        children: [{ index: true, element: <DashboardPage /> }],
      },
    ],
    { initialEntries: [`/organizations/${organizationA.id}`] },
  )
  const tree = <RouterProvider router={router} />
  return render(options?.strictMode ? <StrictMode>{tree}</StrictMode> : tree)
}

beforeEach(() => {
  vi.mocked(getDashboard).mockReset()
  vi.mocked(getDashboardSupplementaryData).mockReset()
  vi.mocked(getDashboardSupplementaryData).mockResolvedValue(supplementary())
})

afterEach(() => {
  vi.clearAllMocks()
})

describe('DashboardPage', () => {
  it('InitialFetch_ShowsAnnouncedLoadingAndIssuesOneScopedRequest', () => {
    vi.mocked(getDashboard).mockReturnValue(new Promise(() => undefined))

    renderDashboard()

    expect(screen.getByRole('status')).toHaveTextContent('Carregando visão geral…')
    expect(screen.queryByText('0')).not.toBeInTheDocument()
    expect(getDashboard).toHaveBeenCalledOnce()
    expect(getDashboard).toHaveBeenCalledWith(
      organizationA.id,
      authValue.handleUnauthorized,
      expect.any(AbortSignal),
    )
  })

  it('Success_RendersFourActionableKpisWithExactProcessLabelAndRoutes', async () => {
    vi.mocked(getDashboard).mockResolvedValue(dashboard())

    renderDashboard()

    expect(await screen.findByRole('heading', { name: 'Visão geral' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /Clientes ativos: 12/ })).toHaveAttribute(
      'href',
      `/organizations/${organizationA.id}/clients`,
    )
    expect(
      screen.getByRole('link', { name: /Processos cadastrados: 18/ }),
    ).toHaveAttribute('href', `/organizations/${organizationA.id}/processes`)
    expect(screen.queryByText('Processos ativos')).not.toBeInTheDocument()
    expect(screen.getByRole('link', { name: /Prazos pendentes: 5/ })).toHaveAttribute(
      'href',
      `/organizations/${organizationA.id}/deadlines`,
    )
    expect(screen.getByRole('link', { name: /Tarefas pendentes: 9/ })).toHaveAttribute(
      'href',
      `/organizations/${organizationA.id}/tasks`,
    )
  })

  it('Attention_IsIntegratedIntoDeadlineAndTaskKpis', async () => {
    vi.mocked(getDashboard).mockResolvedValue(dashboard())

    renderDashboard()

    const deadlines = await screen.findByRole('link', {
      name: /Prazos pendentes: 5\. 1 vencido/,
    })
    const tasks = screen.getByRole('link', {
      name: /Tarefas pendentes: 9\. 2 vencidas/,
    })

    expect(deadlines).toHaveAttribute(
      'href',
      `/organizations/${organizationA.id}/deadlines`,
    )
    expect(tasks).toHaveAttribute(
      'href',
      `/organizations/${organizationA.id}/tasks`,
    )
    expect(screen.queryByRole('heading', { name: 'Pontos de atenção' })).not.toBeInTheDocument()
  })

  it('ZeroValues_AreRenderedAsValidMetricsWithoutError', async () => {
    vi.mocked(getDashboard).mockResolvedValue(
      dashboard({
        summary: {
          activeClients: 0,
          totalLegalProcesses: 0,
          pendingDeadlines: 0,
          pendingTasks: 0,
        },
        attention: {
          deadlines: { overdue: 0, dueToday: 0, dueInNextSevenDays: 0 },
          tasks: { overdue: 0, dueToday: 0, dueInNextSevenDays: 0 },
        },
      }),
    )

    renderDashboard()

    expect(await screen.findByRole('link', { name: /Clientes ativos: 0/ })).toBeInTheDocument()
    expect(screen.getAllByText('0')).toHaveLength(4)
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('EmptyUpcoming_UsesCompactPerModuleEmptyStates', async () => {
    vi.mocked(getDashboard).mockResolvedValue(dashboard())

    renderDashboard()

    expect(await screen.findByText('Nenhum evento no período.')).toBeInTheDocument()
    expect(screen.getByText('Nenhum prazo no período.')).toBeInTheDocument()
    expect(screen.getByText('Nenhuma tarefa no período.')).toBeInTheDocument()
    expect(screen.queryByText(/Até 31\/08\/2026/)).not.toBeInTheDocument()
  })

  it('UpcomingDeadline_RendersTemporalBadgeMetadataAndDetailNavigation', async () => {
    vi.mocked(getDashboard).mockResolvedValue(
      dashboard({
        upcoming: {
          throughDate: '2026-08-31',
          deadlines: [
            {
              id: deadlineId,
              title: 'Apresentar contestação',
              dueDate: '2026-08-25',
              clientName: 'Cliente Alfa',
              processTitle: 'Ação contratual',
            },
          ],
          tasks: [],
          calendarEvents: [],
        },
      }),
    )

    renderDashboard()

    const link = await screen.findByRole('link', { name: /Apresentar contestação/ })
    expect(link).toHaveAttribute(
      'href',
      `/organizations/${organizationA.id}/deadlines/${deadlineId}`,
    )
    expect(link).toHaveTextContent('Amanhã')
    expect(link).toHaveTextContent('Cliente Alfa')
    expect(link).toHaveTextContent('Ação contratual')
  })

  it('UpcomingTask_OmitsAbsentOptionalMetadataAndNavigatesToDetail', async () => {
    vi.mocked(getDashboard).mockResolvedValue(
      dashboard({
        upcoming: {
          throughDate: '2026-08-31',
          deadlines: [],
          tasks: [
            {
              id: taskId,
              title: 'Protocolar petição',
              dueDate: '2026-08-26',
              clientName: null,
              processTitle: null,
              assigneeDisplayName: null,
            },
          ],
          calendarEvents: [],
        },
      }),
    )

    renderDashboard()

    const link = await screen.findByRole('link', { name: /Protocolar petição/ })
    expect(link).toHaveAttribute(
      'href',
      `/organizations/${organizationA.id}/tasks/${taskId}`,
    )
    expect(link).toHaveTextContent('26/08')
    expect(link).not.toHaveTextContent('-')
    expect(link).not.toHaveTextContent('Responsável:')
  })

  it('UpcomingEvent_UsesCompactLocalAgendaFormattingAndNavigatesToAgenda', async () => {
    const startsAt = '2026-08-27T13:00:00Z'
    const endsAt = '2026-08-27T14:00:00Z'
    vi.mocked(getDashboard).mockResolvedValue(
      dashboard({
        upcoming: {
          throughDate: '2026-08-31',
          deadlines: [],
          tasks: [],
          calendarEvents: [
            {
              id: eventId,
              title: 'Audiência',
              startsAt,
              endsAt,
              clientName: 'Cliente Beta',
              processTitle: null,
              assigneeDisplayName: 'Ana Lima',
            },
          ],
        },
      }),
    )

    renderDashboard()

    const link = await screen.findByRole('link', { name: /Audiência/ })
    const dayFormatter = new Intl.DateTimeFormat('pt-BR', {
      day: '2-digit',
      month: '2-digit',
    })
    const timeFormatter = new Intl.DateTimeFormat('pt-BR', {
      hour: '2-digit',
      minute: '2-digit',
    })
    expect(link).toHaveAttribute('href', `/organizations/${organizationA.id}/agenda`)
    expect(link).toHaveTextContent(dayFormatter.format(new Date(startsAt)))
    expect(link).toHaveTextContent(timeFormatter.format(new Date(startsAt)))
    expect(link).toHaveTextContent(timeFormatter.format(new Date(endsAt)))
    expect(link).toHaveTextContent('Cliente Beta')
    expect(link).toHaveTextContent('Ana Lima')
  })

  it('OperationalCards_ProvideFeatureNavigation', async () => {
    vi.mocked(getDashboard).mockResolvedValue(dashboard())

    renderDashboard()

    expect(await screen.findByRole('link', { name: 'Ver agenda' })).toHaveAttribute(
      'href',
      `/organizations/${organizationA.id}/agenda`,
    )
    expect(screen.getByRole('link', { name: 'Ver todos os prazos' })).toHaveAttribute(
      'href',
      `/organizations/${organizationA.id}/deadlines`,
    )
    expect(screen.getByRole('link', { name: 'Ver todas as tarefas' })).toHaveAttribute(
      'href',
      `/organizations/${organizationA.id}/tasks`,
    )
    expect(screen.getByRole('link', { name: 'Ver todos os processos' })).toHaveAttribute(
      'href',
      `/organizations/${organizationA.id}/processes`,
    )
    expect(screen.getByRole('link', { name: 'Ver todos os documentos' })).toHaveAttribute(
      'href',
      `/organizations/${organizationA.id}/documents`,
    )
    expect(screen.getByRole('link', { name: 'Ver equipe' })).toHaveAttribute(
      'href',
      `/organizations/${organizationA.id}/team`,
    )
  })

  it('SupplementaryModules_RenderRealMemberDataWithoutAdminSurfaces', async () => {
    vi.mocked(getDashboard).mockResolvedValue(dashboard())
    vi.mocked(getDashboardSupplementaryData).mockResolvedValue(
      supplementary({
        processes: {
          status: 'success',
          items: [
            {
              id: '66666666-6666-4666-8666-666666666666',
              title: 'Ação indenizatória',
              clientId: '77777777-7777-4777-8777-777777777777',
              clientName: 'Cliente Gama',
              createdAt: '2026-08-24T12:00:00Z',
            },
          ],
        },
        documents: {
          status: 'success',
          items: [
            {
              id: '88888888-8888-4888-8888-888888888888',
              clientId: null,
              processId: null,
              originalFileName: 'Contrato.pdf',
              contentType: 'application/pdf',
              sizeBytes: 1024,
              createdAt: '2026-08-24T13:00:00Z',
            },
          ],
        },
        team: {
          status: 'success',
          items: [
            {
              id: organizationA.membershipId,
              name: 'João Silva',
              role: 'Member',
            },
          ],
          totalCount: 1,
        },
      }),
    )

    renderDashboard()

    expect(
      await screen.findByRole('heading', { name: 'Processos recentes' }),
    ).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /Ação indenizatória/ })).toHaveAttribute(
      'href',
      `/organizations/${organizationA.id}/processes/66666666-6666-4666-8666-666666666666`,
    )
    expect(screen.getByRole('link', { name: /Contrato\.pdf/ })).toHaveAttribute(
      'href',
      `/organizations/${organizationA.id}/documents/88888888-8888-4888-8888-888888888888`,
    )
    expect(screen.getByRole('link', { name: 'João Silva, Membro' })).toHaveAttribute(
      'href',
      `/organizations/${organizationA.id}/team`,
    )
    expect(
      screen.queryByRole('heading', { name: 'Convites pendentes' }),
    ).not.toBeInTheDocument()
    expect(
      screen.queryByRole('heading', { name: 'Auditoria recente' }),
    ).not.toBeInTheDocument()
  })

  it('RecoverableError_ShowsSafeInlineAlertWithoutStaleData', async () => {
    vi.mocked(getDashboard).mockRejectedValue(new Error('private backend detail'))

    renderDashboard()

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Não foi possível carregar a visão geral. Tente novamente.',
    )
    expect(screen.queryByText('private backend detail')).not.toBeInTheDocument()
    expect(screen.queryByText('Clientes ativos')).not.toBeInTheDocument()
  })

  it('Retry_PerformsFreshRequestAndRecovers', async () => {
    vi.mocked(getDashboard)
      .mockRejectedValueOnce(new Error('network'))
      .mockResolvedValueOnce(dashboard())

    renderDashboard()

    fireEvent.click(await screen.findByRole('button', { name: 'Tentar novamente' }))

    expect(await screen.findByRole('link', { name: /Clientes ativos: 12/ })).toBeInTheDocument()
    expect(getDashboard).toHaveBeenCalledTimes(2)
  })

  it('Forbidden_UsesEstablishedOrganizationAccessRefreshBehavior', async () => {
    const refreshOrganizations = vi.fn()
    vi.mocked(getDashboard).mockRejectedValue(new DashboardRequestError('forbidden'))

    renderDashboard({ refreshOrganizations })

    fireEvent.click(await screen.findByRole('button', { name: 'Atualizar acesso' }))
    expect(refreshOrganizations).toHaveBeenCalledOnce()
  })

  it('TenantSwitch_StaleOldResponseCannotPopulateNewOrganization', async () => {
    const oldRequest = deferred<DashboardResponse>()
    const newRequest = deferred<DashboardResponse>()
    vi.mocked(getDashboard).mockImplementation((organizationId) =>
      organizationId === organizationA.id ? oldRequest.promise : newRequest.promise,
    )
    renderDashboard({ enableSwitch: true })

    fireEvent.click(screen.getByRole('button', { name: 'Trocar organização' }))
    await act(async () => oldRequest.resolve(dashboard()))

    expect(screen.queryByText('Clientes ativos')).not.toBeInTheDocument()
    expect(screen.getByRole('status')).toHaveTextContent('Carregando visão geral…')

    await act(async () => newRequest.resolve(dashboard({ summary: {
      activeClients: 7,
      totalLegalProcesses: 8,
      pendingDeadlines: 9,
      pendingTasks: 10,
    } })))
    expect(screen.getByRole('link', { name: /Clientes ativos: 7/ })).toBeInTheDocument()
  })

  it('TenantSwitch_StaleOldErrorCannotReplaceNewOrganizationState', async () => {
    const oldRequest = deferred<DashboardResponse>()
    vi.mocked(getDashboard).mockImplementation((organizationId) =>
      organizationId === organizationA.id
        ? oldRequest.promise
        : Promise.resolve(dashboard()),
    )
    renderDashboard({ enableSwitch: true })

    fireEvent.click(screen.getByRole('button', { name: 'Trocar organização' }))
    expect(await screen.findByRole('link', { name: /Clientes ativos: 12/ })).toBeInTheDocument()
    await act(async () => oldRequest.reject(new Error('stale tenant error')))

    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    expect(screen.getByRole('link', { name: /Clientes ativos: 12/ })).toBeInTheDocument()
  })

  it('TenantSwitch_ClearsOldTenantDataImmediatelyWhileNewRequestLoads', async () => {
    const newRequest = deferred<DashboardResponse>()
    vi.mocked(getDashboard).mockImplementation((organizationId) =>
      organizationId === organizationA.id
        ? Promise.resolve(dashboard())
        : newRequest.promise,
    )
    renderDashboard({ enableSwitch: true })

    expect(await screen.findByRole('link', { name: /Clientes ativos: 12/ })).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Trocar organização' }))

    expect(screen.queryByText('Clientes ativos')).not.toBeInTheDocument()
    expect(screen.getByRole('status')).toHaveTextContent('Carregando visão geral…')
  })

  it('StrictMode_AbortedFirstEffectCannotWinAndFreshRequestPopulatesDashboard', async () => {
    const firstRequest = deferred<DashboardResponse>()
    const freshRequest = deferred<DashboardResponse>()
    const signals: AbortSignal[] = []
    vi.mocked(getDashboard)
      .mockImplementationOnce((_organizationId, _handler, signal) => {
        signals.push(signal!)
        return firstRequest.promise
      })
      .mockImplementationOnce((_organizationId, _handler, signal) => {
        signals.push(signal!)
        return freshRequest.promise
      })

    renderDashboard({ strictMode: true })

    expect(getDashboard).toHaveBeenCalledTimes(2)
    expect(signals[0]?.aborted).toBe(true)
    expect(signals[1]?.aborted).toBe(false)

    await act(async () =>
      firstRequest.resolve(
        dashboard({
          summary: {
            activeClients: 99,
            totalLegalProcesses: 99,
            pendingDeadlines: 99,
            pendingTasks: 99,
          },
        }),
      ),
    )
    expect(screen.queryByRole('link', { name: /Clientes ativos: 99/ })).not.toBeInTheDocument()
    expect(screen.getByRole('status')).toHaveTextContent('Carregando visão geral…')

    await act(async () => freshRequest.resolve(dashboard()))
    expect(screen.getByRole('link', { name: /Clientes ativos: 12/ })).toBeInTheDocument()
  })

  it('Unmount_AbortsPendingDashboardRequest', () => {
    let signal: AbortSignal | undefined
    vi.mocked(getDashboard).mockImplementation((_organizationId, _handler, requestSignal) => {
      signal = requestSignal
      return new Promise(() => undefined)
    })
    const view = renderDashboard()

    expect(signal?.aborted).toBe(false)
    view.unmount()
    expect(signal?.aborted).toBe(true)
  })
})
