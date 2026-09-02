import { useEffect, useRef, useState, type ReactNode } from 'react'
import { Link } from 'react-router-dom'
import { useAuth } from '../authentication/AuthContext'
import {
  useCurrentOrganization,
  useOrganizationDiscovery,
} from '../organizations/OrganizationContext'
import type { OrganizationNavigationItem } from '../organizations/organizationTypes'
import {
  formatDashboardDateOnly,
  formatDashboardEventInterval,
} from './dashboardFormatting'
import {
  DashboardRequestError,
  getDashboard,
} from './dashboardService'
import type {
  DashboardAttentionBucket,
  DashboardResponse,
} from './dashboardTypes'

type DashboardState =
  | { readonly status: 'loading'; readonly scope: string }
  | {
      readonly status: 'success'
      readonly scope: string
      readonly dashboard: DashboardResponse
    }
  | { readonly status: 'forbidden' | 'error'; readonly scope: string }

type DashboardIconName =
  | 'clients'
  | 'processes'
  | 'deadlines'
  | 'tasks'
  | 'agenda'

function isAbortError(error: unknown): boolean {
  return error instanceof DOMException && error.name === 'AbortError'
}

function DashboardIcon({
  name,
}: {
  readonly name: DashboardIconName
}) {
  const common = {
    width: 22,
    height: 22,
    viewBox: '0 0 24 24',
    fill: 'none',
    stroke: 'currentColor',
    strokeWidth: 1.8,
    strokeLinecap: 'round' as const,
    strokeLinejoin: 'round' as const,
    'aria-hidden': true,
  }

  if (name === 'clients') {
    return (
      <svg {...common}>
        <path d="M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2" />
        <circle cx="9" cy="7" r="4" />
        <path d="M22 21v-2a4 4 0 0 0-3-3.87M16 3.13a4 4 0 0 1 0 7.75" />
      </svg>
    )
  }

  if (name === 'processes') {
    return (
      <svg {...common}>
        <rect x="3" y="7" width="18" height="13" rx="2" />
        <path d="M8 7V5a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2M3 12h18" />
      </svg>
    )
  }

  if (name === 'deadlines') {
    return (
      <svg {...common}>
        <circle cx="12" cy="12" r="9" />
        <path d="M12 7v5l3 2" />
      </svg>
    )
  }

  if (name === 'tasks') {
    return (
      <svg {...common}>
        <rect x="5" y="3" width="14" height="18" rx="2" />
        <path d="M9 3.8h6M8.5 12l2 2 5-5" />
      </svg>
    )
  }

  return (
    <svg {...common}>
      <rect x="3" y="5" width="18" height="16" rx="2" />
      <path d="M16 3v4M8 3v4M3 10h18" />
    </svg>
  )
}

interface AttentionCardProps {
  readonly title: string
  readonly destination: string
  readonly bucket: DashboardAttentionBucket
  readonly icon: Extract<DashboardIconName, 'deadlines' | 'tasks'>
}

function AttentionCard({
  title,
  destination,
  bucket,
  icon,
}: AttentionCardProps) {
  return (
    <Link className="dashboard-attention-card" to={destination}>
      <div className="dashboard-attention-card-title">
        <span className="dashboard-mini-icon">
          <DashboardIcon name={icon} />
        </span>
        <h4>{title}</h4>
      </div>
      <dl>
        <div className={bucket.overdue > 0 ? 'has-overdue' : undefined}>
          <dt>Vencidos</dt>
          <dd>{bucket.overdue}</dd>
        </div>
        <div className={bucket.dueToday > 0 ? 'has-due-today' : undefined}>
          <dt>Hoje</dt>
          <dd>{bucket.dueToday}</dd>
        </div>
        <div>
          <dt>Próximos 7 dias</dt>
          <dd>{bucket.dueInNextSevenDays}</dd>
        </div>
      </dl>
    </Link>
  )
}

function OptionalMetadata({ children }: { readonly children: ReactNode }) {
  return <span className="dashboard-upcoming-metadata">{children}</span>
}

export function DashboardPage() {
  const { currentOrganization } = useCurrentOrganization()

  return (
    <OrganizationDashboardPage
      key={currentOrganization.id}
      currentOrganization={currentOrganization}
    />
  )
}

function OrganizationDashboardPage({
  currentOrganization,
}: {
  readonly currentOrganization: OrganizationNavigationItem
}) {
  const { handleUnauthorized } = useAuth()
  const { refreshOrganizations } = useOrganizationDiscovery()
  const [retryVersion, setRetryVersion] = useState(0)
  const scope = `${currentOrganization.id}:${retryVersion}`
  const [state, setState] = useState<DashboardState>({
    status: 'loading',
    scope,
  })
  const requestVersionRef = useRef(0)

  useEffect(() => {
    const controller = new AbortController()
    const requestVersion = ++requestVersionRef.current
    void getDashboard(
      currentOrganization.id,
      handleUnauthorized,
      controller.signal,
    )
      .then((dashboard) => {
        if (
          !controller.signal.aborted &&
          requestVersion === requestVersionRef.current
        ) {
          setState({ status: 'success', scope, dashboard })
        }
      })
      .catch((error: unknown) => {
        if (
          controller.signal.aborted ||
          requestVersion !== requestVersionRef.current ||
          isAbortError(error) ||
          (error instanceof DashboardRequestError &&
            error.failure === 'unauthorized')
        ) {
          return
        }
        setState({
          status:
            error instanceof DashboardRequestError &&
            error.failure === 'forbidden'
              ? 'forbidden'
              : 'error',
          scope,
        })
      })

    return () => {
      requestVersionRef.current += 1
      controller.abort()
    }
  }, [currentOrganization.id, handleUnauthorized, scope])

  const currentState: DashboardState =
    state.scope === scope ? state : { status: 'loading', scope }

  return (
    <section
      className="dashboard-page dashboard-page-phase3e"
      aria-busy={currentState.status === 'loading'}
    >
      <header className="dashboard-header dashboard-header-phase3e workspace-page-header">
        <div className="workspace-page-heading">
          <span className="dashboard-eyebrow workspace-page-eyebrow">
            PAINEL DA ORGANIZAÇÃO
          </span>
          <h2 className="workspace-page-title">Visão geral</h2>
          <p className="workspace-page-subtitle">Painel de controle do escritório.</p>
        </div>
      </header>

      {currentState.status === 'loading' ? (
        <p className="dashboard-state" role="status" aria-live="polite">
          Carregando visão geral…
        </p>
      ) : null}

      {currentState.status === 'forbidden' ? (
        <div className="dashboard-state dashboard-error" role="alert">
          <p>Não foi possível acessar a visão geral desta organização.</p>
          <button
            className="secondary-button"
            type="button"
            onClick={refreshOrganizations}
          >
            Atualizar acesso
          </button>
        </div>
      ) : null}

      {currentState.status === 'error' ? (
        <div className="dashboard-state dashboard-error" role="alert">
          <p>Não foi possível carregar a visão geral. Tente novamente.</p>
          <button
            className="secondary-button"
            type="button"
            onClick={() => setRetryVersion((version) => version + 1)}
          >
            Tentar novamente
          </button>
        </div>
      ) : null}

      {currentState.status === 'success' ? (
        <DashboardContent dashboard={currentState.dashboard} />
      ) : null}
    </section>
  )
}

function DashboardContent({
  dashboard,
}: {
  readonly dashboard: DashboardResponse
}) {
  const { summary, attention, upcoming } = dashboard
  const hasUpcoming =
    upcoming.deadlines.length > 0 ||
    upcoming.tasks.length > 0 ||
    upcoming.calendarEvents.length > 0

  const kpis = [
    {
      label: 'Clientes ativos',
      value: summary.activeClients,
      destination: 'clients',
      icon: 'clients' as const,
    },
    {
      label: 'Processos cadastrados',
      value: summary.totalLegalProcesses,
      destination: 'processes',
      icon: 'processes' as const,
    },
    {
      label: 'Prazos pendentes',
      value: summary.pendingDeadlines,
      destination: 'deadlines',
      icon: 'deadlines' as const,
    },
    {
      label: 'Tarefas pendentes',
      value: summary.pendingTasks,
      destination: 'tasks',
      icon: 'tasks' as const,
    },
  ] as const

  return (
    <>
      <section
        className="dashboard-kpis dashboard-kpis-phase3e"
        aria-label="Resumo da organização"
      >
        {kpis.map((kpi) => (
          <Link
            className="dashboard-kpi-card dashboard-kpi-card-phase3e"
            to={kpi.destination}
            key={kpi.label}
            aria-label={`${kpi.label}: ${kpi.value}. Abrir ${kpi.label.toLowerCase()}.`}
          >
            <span className="dashboard-kpi-icon">
              <DashboardIcon name={kpi.icon} />
            </span>
            <span className="dashboard-kpi-copy">
              <span>{kpi.label}</span>
              <strong>{kpi.value}</strong>
              <small>Ver detalhes</small>
            </span>
          </Link>
        ))}
      </section>

      <section
        className="dashboard-section dashboard-focus-section"
        aria-labelledby="dashboard-upcoming-title"
      >
        <div className="dashboard-focus-heading">
          <h3 id="dashboard-upcoming-title">Próximos compromissos</h3>
          <p>Até {formatDashboardDateOnly(upcoming.throughDate)}</p>
        </div>

        {!hasUpcoming ? (
          <p className="dashboard-upcoming-empty dashboard-upcoming-empty-phase3e">
            Nenhum compromisso pendente para hoje e os próximos 7 dias.
          </p>
        ) : null}

        <div className="dashboard-focus-grid">
          <section
            className="dashboard-focus-card"
            aria-labelledby="dashboard-agenda-card-title"
          >
            <header className="dashboard-focus-card-header">
              <div>
                <span className="dashboard-mini-icon">
                  <DashboardIcon name="agenda" />
                </span>
                <h4 id="dashboard-agenda-card-title">Agenda</h4>
              </div>
              <Link className="dashboard-section-link" to="agenda">
                Ver Agenda
              </Link>
            </header>

            {upcoming.calendarEvents.length > 0 ? (
              <ul className="dashboard-focus-list dashboard-agenda-list">
                {upcoming.calendarEvents.slice(0, 4).map((event) => (
                  <li key={event.id}>
                    <Link to="agenda">
                      <strong>{event.title}</strong>
                      <time dateTime={event.startsAt}>
                        {formatDashboardEventInterval(
                          event.startsAt,
                          event.endsAt,
                        )}
                      </time>
                      {event.processTitle ? (
                        <OptionalMetadata>
                          {event.processTitle}
                        </OptionalMetadata>
                      ) : null}
                      {event.clientName ? (
                        <OptionalMetadata>
                          {event.clientName}
                        </OptionalMetadata>
                      ) : null}
                      {event.assigneeDisplayName ? (
                        <OptionalMetadata>
                          Responsável: {event.assigneeDisplayName}
                        </OptionalMetadata>
                      ) : null}
                    </Link>
                  </li>
                ))}
              </ul>
            ) : (
              <p className="dashboard-focus-empty">
                Nenhum evento no período.
              </p>
            )}
          </section>

          <section
            className="dashboard-focus-card"
            aria-labelledby="upcoming-deadlines-title"
          >
            <header className="dashboard-focus-card-header">
              <div>
                <span className="dashboard-mini-icon">
                  <DashboardIcon name="deadlines" />
                </span>
                <h4 id="upcoming-deadlines-title">Próximos prazos</h4>
              </div>
              <Link className="dashboard-section-link" to="deadlines">
                Ver todos
              </Link>
            </header>

            {upcoming.deadlines.length > 0 ? (
              <ul className="dashboard-focus-list">
                {upcoming.deadlines.slice(0, 4).map((deadline) => (
                  <li key={deadline.id}>
                    <Link to={`deadlines/${deadline.id}`}>
                      <div className="dashboard-focus-list-primary">
                        <strong>{deadline.title}</strong>
                        <time dateTime={deadline.dueDate}>
                          {formatDashboardDateOnly(deadline.dueDate)}
                        </time>
                      </div>
                      <OptionalMetadata>
                        {deadline.clientName}
                      </OptionalMetadata>
                      <OptionalMetadata>
                        {deadline.processTitle}
                      </OptionalMetadata>
                    </Link>
                  </li>
                ))}
              </ul>
            ) : (
              <p className="dashboard-focus-empty">
                Nenhum prazo no período.
              </p>
            )}
          </section>

          <section
            className="dashboard-focus-card"
            aria-labelledby="upcoming-tasks-title"
          >
            <header className="dashboard-focus-card-header">
              <div>
                <span className="dashboard-mini-icon">
                  <DashboardIcon name="tasks" />
                </span>
                <h4 id="upcoming-tasks-title">Tarefas</h4>
              </div>
              <Link className="dashboard-section-link" to="tasks">
                Ver todas
              </Link>
            </header>

            {upcoming.tasks.length > 0 ? (
              <ul className="dashboard-focus-list">
                {upcoming.tasks.slice(0, 4).map((task) => (
                  <li key={task.id}>
                    <Link to={`tasks/${task.id}`}>
                      <div className="dashboard-focus-list-primary">
                        <strong>{task.title}</strong>
                        <time dateTime={task.dueDate}>
                          {formatDashboardDateOnly(task.dueDate)}
                        </time>
                      </div>
                      {task.processTitle ? (
                        <OptionalMetadata>
                          {task.processTitle}
                        </OptionalMetadata>
                      ) : null}
                      {task.clientName ? (
                        <OptionalMetadata>
                          {task.clientName}
                        </OptionalMetadata>
                      ) : null}
                      {task.assigneeDisplayName ? (
                        <OptionalMetadata>
                          Responsável: {task.assigneeDisplayName}
                        </OptionalMetadata>
                      ) : null}
                    </Link>
                  </li>
                ))}
              </ul>
            ) : (
              <p className="dashboard-focus-empty">
                Nenhuma tarefa no período.
              </p>
            )}
          </section>
        </div>
      </section>

      <section
        className="dashboard-section dashboard-attention-section-phase3e"
        aria-labelledby="dashboard-attention-title"
      >
        <div className="dashboard-attention-heading">
          <div>
            <h3 id="dashboard-attention-title">Pontos de atenção</h3>
            <p>Prioridades operacionais que merecem acompanhamento.</p>
          </div>
        </div>
        <div className="dashboard-attention-grid dashboard-attention-grid-phase3e">
          <AttentionCard
            title="Prazos"
            destination="deadlines"
            bucket={attention.deadlines}
            icon="deadlines"
          />
          <AttentionCard
            title="Tarefas"
            destination="tasks"
            bucket={attention.tasks}
            icon="tasks"
          />
        </div>
      </section>
    </>
  )
}
