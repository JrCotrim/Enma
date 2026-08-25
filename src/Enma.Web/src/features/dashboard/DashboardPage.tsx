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

function isAbortError(error: unknown): boolean {
  return error instanceof DOMException && error.name === 'AbortError'
}

interface AttentionCardProps {
  readonly title: string
  readonly destination: string
  readonly bucket: DashboardAttentionBucket
}

function AttentionCard({ title, destination, bucket }: AttentionCardProps) {
  return (
    <Link className="dashboard-attention-card" to={destination}>
      <h4>{title}</h4>
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
    <section className="dashboard-page" aria-busy={currentState.status === 'loading'}>
      <header className="dashboard-header">
        <div>
          <h2>Visão geral</h2>
          <p>Resumo operacional de toda a organização.</p>
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

function DashboardContent({ dashboard }: { readonly dashboard: DashboardResponse }) {
  const { summary, attention, upcoming } = dashboard
  const hasUpcoming =
    upcoming.deadlines.length > 0 ||
    upcoming.tasks.length > 0 ||
    upcoming.calendarEvents.length > 0

  const kpis = [
    { label: 'Clientes ativos', value: summary.activeClients, destination: 'clients' },
    {
      label: 'Processos cadastrados',
      value: summary.totalLegalProcesses,
      destination: 'processes',
    },
    {
      label: 'Prazos pendentes',
      value: summary.pendingDeadlines,
      destination: 'deadlines',
    },
    { label: 'Tarefas pendentes', value: summary.pendingTasks, destination: 'tasks' },
  ] as const

  return (
    <>
      <section className="dashboard-kpis" aria-label="Resumo da organização">
        {kpis.map((kpi) => (
          <Link
            className="dashboard-kpi-card"
            to={kpi.destination}
            key={kpi.label}
            aria-label={`${kpi.label}: ${kpi.value}. Abrir ${kpi.label.toLowerCase()}.`}
          >
            <span>{kpi.label}</span>
            <strong>{kpi.value}</strong>
          </Link>
        ))}
      </section>

      <section className="dashboard-section" aria-labelledby="dashboard-attention-title">
        <h3 id="dashboard-attention-title">Pontos de atenção</h3>
        <div className="dashboard-attention-grid">
          <AttentionCard title="Prazos" destination="deadlines" bucket={attention.deadlines} />
          <AttentionCard title="Tarefas" destination="tasks" bucket={attention.tasks} />
        </div>
      </section>

      <section className="dashboard-section" aria-labelledby="dashboard-upcoming-title">
        <div className="dashboard-section-header">
          <div>
            <h3 id="dashboard-upcoming-title">Próximos compromissos</h3>
            <p>Até {formatDashboardDateOnly(upcoming.throughDate)}</p>
          </div>
          <Link className="dashboard-section-link" to="agenda">
            Ver Agenda
          </Link>
        </div>

        {!hasUpcoming ? (
          <p className="dashboard-upcoming-empty">
            Nenhum compromisso pendente para hoje e os próximos 7 dias.
          </p>
        ) : (
          <div className="dashboard-upcoming-groups">
            {upcoming.deadlines.length > 0 ? (
              <section className="dashboard-upcoming-group" aria-labelledby="upcoming-deadlines-title">
                <h4 id="upcoming-deadlines-title">Prazos</h4>
                <ul>
                  {upcoming.deadlines.map((deadline) => (
                    <li key={deadline.id}>
                      <Link to={`deadlines/${deadline.id}`}>
                        <strong>{deadline.title}</strong>
                        <time dateTime={deadline.dueDate}>
                          {formatDashboardDateOnly(deadline.dueDate)}
                        </time>
                        <OptionalMetadata>{deadline.clientName}</OptionalMetadata>
                        <OptionalMetadata>{deadline.processTitle}</OptionalMetadata>
                      </Link>
                    </li>
                  ))}
                </ul>
              </section>
            ) : null}

            {upcoming.tasks.length > 0 ? (
              <section className="dashboard-upcoming-group" aria-labelledby="upcoming-tasks-title">
                <h4 id="upcoming-tasks-title">Tarefas</h4>
                <ul>
                  {upcoming.tasks.map((task) => (
                    <li key={task.id}>
                      <Link to={`tasks/${task.id}`}>
                        <strong>{task.title}</strong>
                        <time dateTime={task.dueDate}>
                          {formatDashboardDateOnly(task.dueDate)}
                        </time>
                        {task.processTitle ? <OptionalMetadata>{task.processTitle}</OptionalMetadata> : null}
                        {task.clientName ? <OptionalMetadata>{task.clientName}</OptionalMetadata> : null}
                        {task.assigneeDisplayName ? (
                          <OptionalMetadata>Responsável: {task.assigneeDisplayName}</OptionalMetadata>
                        ) : null}
                      </Link>
                    </li>
                  ))}
                </ul>
              </section>
            ) : null}

            {upcoming.calendarEvents.length > 0 ? (
              <section className="dashboard-upcoming-group" aria-labelledby="upcoming-events-title">
                <h4 id="upcoming-events-title">Eventos</h4>
                <ul>
                  {upcoming.calendarEvents.map((event) => (
                    <li key={event.id}>
                      <Link to="agenda">
                        <strong>{event.title}</strong>
                        <time dateTime={event.startsAt}>
                          {formatDashboardEventInterval(event.startsAt, event.endsAt)}
                        </time>
                        {event.processTitle ? <OptionalMetadata>{event.processTitle}</OptionalMetadata> : null}
                        {event.clientName ? <OptionalMetadata>{event.clientName}</OptionalMetadata> : null}
                        {event.assigneeDisplayName ? (
                          <OptionalMetadata>Responsável: {event.assigneeDisplayName}</OptionalMetadata>
                        ) : null}
                      </Link>
                    </li>
                  ))}
                </ul>
              </section>
            ) : null}
          </div>
        )}
      </section>
    </>
  )
}
