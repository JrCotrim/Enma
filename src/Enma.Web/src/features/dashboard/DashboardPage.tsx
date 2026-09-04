import { useEffect, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import { useAuth } from '../authentication/AuthContext'
import type { UnauthorizedHandler } from '../authentication/sessionClient'
import {
  formatAuditTimestamp,
  getAuditEntityLabel,
  getAuditEventLabel,
} from '../audit-log/auditLogFormatting'
import { getDocumentContextLabel } from '../documents/documentFormatting'
import {
  useCurrentOrganization,
  useOrganizationDiscovery,
} from '../organizations/OrganizationContext'
import {
  getOrganizationRoleLabel,
  type OrganizationNavigationItem,
} from '../organizations/organizationTypes'
import {
  formatDashboardEventDay,
  formatDashboardEventTimeRange,
  formatDashboardShortDateOnly,
} from './dashboardFormatting'
import {
  DashboardRequestError,
  getDashboard,
} from './dashboardService'
import type {
  DashboardAttentionBucket,
  DashboardResponse,
} from './dashboardTypes'
import {
  getDashboardSupplementaryData,
  type DashboardSupplementaryData,
} from './dashboardSupplementaryService'

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
  | 'documents'
  | 'invitations'
  | 'audit'
  | 'team'

type AttentionKind = 'deadline' | 'task'
type AttentionTone = 'danger' | 'warning' | 'neutral'
type DateBadgeTone = 'overdue' | 'today' | 'tomorrow' | 'future'

interface AttentionContext {
  readonly label: string
  readonly tone: AttentionTone
}

interface DateBadge {
  readonly label: string
  readonly tone: DateBadgeTone
}

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

  if (name === 'clients' || name === 'team') {
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

  if (name === 'documents') {
    return (
      <svg {...common}>
        <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8Z" />
        <path d="M14 2v6h6M8 13h8M8 17h6" />
      </svg>
    )
  }

  if (name === 'invitations') {
    return (
      <svg {...common}>
        <rect x="3" y="5" width="18" height="14" rx="2" />
        <path d="m3 7 9 6 9-6" />
      </svg>
    )
  }

  if (name === 'audit') {
    return (
      <svg {...common}>
        <path d="M20 13c0 5-3.5 7.5-7.66 8.95a1 1 0 0 1-.67-.01C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.24-2.72a1.17 1.17 0 0 1 1.52 0C14.51 3.81 17 5 19 5a1 1 0 0 1 1 1z" />
        <path d="m9 12 2 2 4-4" />
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

function getAttentionContext(
  bucket: DashboardAttentionBucket,
  kind: AttentionKind,
): AttentionContext {
  if (bucket.overdue > 0) {
    let noun: string

    if (kind === 'task') {
      noun = bucket.overdue === 1 ? 'vencida' : 'vencidas'
    } else {
      noun = bucket.overdue === 1 ? 'vencido' : 'vencidos'
    }

    return {
      label: `${bucket.overdue} ${noun}`,
      tone: 'danger',
    }
  }

  if (bucket.dueToday > 0) {
    return {
      label: `${bucket.dueToday} para hoje`,
      tone: 'warning',
    }
  }

  if (bucket.dueInNextSevenDays > 0) {
    return {
      label: `${bucket.dueInNextSevenDays} nos próximos 7 dias`,
      tone: 'neutral',
    }
  }

  return {
    label: 'Sem urgências',
    tone: 'neutral',
  }
}

function dateOnlyToUtcMilliseconds(value: string): number {
  const year = Number(value.slice(0, 4))
  const month = Number(value.slice(5, 7))
  const day = Number(value.slice(8, 10))
  return Date.UTC(year, month - 1, day)
}

function getDateBadge(referenceDate: string, dueDate: string): DateBadge {
  const dayDifference = Math.round(
    (dateOnlyToUtcMilliseconds(dueDate) -
      dateOnlyToUtcMilliseconds(referenceDate)) /
      86_400_000,
  )

  if (dayDifference < 0) {
    return { label: 'Vencido', tone: 'overdue' }
  }

  if (dayDifference === 0) {
    return { label: 'Hoje', tone: 'today' }
  }

  if (dayDifference === 1) {
    return { label: 'Amanhã', tone: 'tomorrow' }
  }

  return {
    label: formatDashboardShortDateOnly(dueDate),
    tone: 'future',
  }
}

function joinMetadata(
  ...values: readonly (string | null | undefined)[]
): string | null {
  const populated = values.filter(
    (value): value is string =>
      typeof value === 'string' && value.trim().length > 0,
  )

  return populated.length > 0 ? populated.join(' · ') : null
}

const supplementaryDateFormatter = new Intl.DateTimeFormat('pt-BR', {
  day: '2-digit',
  month: '2-digit',
})

function formatSupplementaryDate(value: string): string {
  return supplementaryDateFormatter.format(new Date(value))
}

function getInitials(name: string): string {
  return name
    .trim()
    .split(/\s+/)
    .slice(0, 2)
    .map((part) => part.charAt(0))
    .join('')
    .toUpperCase()
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
        <DashboardContent
          dashboard={currentState.dashboard}
          currentOrganization={currentOrganization}
          onUnauthorized={handleUnauthorized}
        />
      ) : null}
    </section>
  )
}

function DashboardContent({
  dashboard,
  currentOrganization,
  onUnauthorized,
}: {
  readonly dashboard: DashboardResponse
  readonly currentOrganization: OrganizationNavigationItem
  readonly onUnauthorized: UnauthorizedHandler
}) {
  const { referenceDate, summary, attention, upcoming } = dashboard

  const kpis = [
    {
      label: 'Clientes ativos',
      value: summary.activeClients,
      destination: 'clients',
      icon: 'clients' as const,
      context: null,
    },
    {
      label: 'Processos cadastrados',
      value: summary.totalLegalProcesses,
      destination: 'processes',
      icon: 'processes' as const,
      context: null,
    },
    {
      label: 'Prazos pendentes',
      value: summary.pendingDeadlines,
      destination: 'deadlines',
      icon: 'deadlines' as const,
      context: getAttentionContext(attention.deadlines, 'deadline'),
    },
    {
      label: 'Tarefas pendentes',
      value: summary.pendingTasks,
      destination: 'tasks',
      icon: 'tasks' as const,
      context: getAttentionContext(attention.tasks, 'task'),
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
            aria-label={`${kpi.label}: ${kpi.value}${
              kpi.context ? `. ${kpi.context.label}` : ''
            }. Abrir ${kpi.label.toLowerCase()}.`}
          >
            <span className="dashboard-kpi-icon">
              <DashboardIcon name={kpi.icon} />
            </span>
            <span className="dashboard-kpi-copy">
              <span>{kpi.label}</span>
              <strong>{kpi.value}</strong>
              {kpi.context ? (
                <span
                  className={`dashboard-kpi-context is-${kpi.context.tone}`}
                >
                  {kpi.context.label}
                </span>
              ) : null}
            </span>
          </Link>
        ))}
      </section>

      <section
        className="dashboard-operational-section"
        aria-label="Compromissos e prioridades"
      >
        <div className="dashboard-operational-grid">
          <section
            className="dashboard-operational-card dashboard-agenda-card"
            aria-labelledby="dashboard-agenda-card-title"
          >
            <header className="dashboard-operational-card-header">
              <div>
                <span className="dashboard-mini-icon">
                  <DashboardIcon name="agenda" />
                </span>
                <h3 id="dashboard-agenda-card-title">Agenda</h3>
              </div>
              <Link className="dashboard-section-link" to="agenda">
                Ver agenda
              </Link>
            </header>

            {upcoming.calendarEvents.length > 0 ? (
              <ul className="dashboard-operational-list dashboard-agenda-list">
                {upcoming.calendarEvents.slice(0, 4).map((event) => {
                  const primaryMetadata = joinMetadata(
                    event.processTitle,
                    event.clientName,
                  )

                  return (
                    <li key={event.id}>
                      <Link className="dashboard-event-row" to="agenda">
                        <span className="dashboard-event-slot">
                          <time dateTime={event.startsAt}>
                            {formatDashboardEventTimeRange(
                              event.startsAt,
                              event.endsAt,
                            )}
                          </time>
                          <span>{formatDashboardEventDay(event.startsAt)}</span>
                        </span>
                        <span className="dashboard-row-copy">
                          <strong>{event.title}</strong>
                          {primaryMetadata ? (
                            <span className="dashboard-row-meta">
                              {primaryMetadata}
                            </span>
                          ) : null}
                          {event.assigneeDisplayName ? (
                            <span className="dashboard-row-meta dashboard-row-meta-subtle">
                              {event.assigneeDisplayName}
                            </span>
                          ) : null}
                        </span>
                      </Link>
                    </li>
                  )
                })}
              </ul>
            ) : (
              <p className="dashboard-operational-empty">
                Nenhum evento no período.
              </p>
            )}
          </section>

          <section
            className="dashboard-operational-card"
            aria-labelledby="upcoming-deadlines-title"
          >
            <header className="dashboard-operational-card-header">
              <div>
                <span className="dashboard-mini-icon">
                  <DashboardIcon name="deadlines" />
                </span>
                <h3 id="upcoming-deadlines-title">Próximos prazos</h3>
              </div>
              <Link
                className="dashboard-section-link"
                to="deadlines"
                aria-label="Ver todos os prazos"
              >
                Ver todos
              </Link>
            </header>

            {upcoming.deadlines.length > 0 ? (
              <ul className="dashboard-operational-list dashboard-deadline-list">
                {upcoming.deadlines.slice(0, 4).map((deadline) => {
                  const dateBadge = getDateBadge(referenceDate, deadline.dueDate)
                  const metadata = joinMetadata(
                    deadline.processTitle,
                    deadline.clientName,
                  )

                  return (
                    <li key={deadline.id}>
                      <Link
                        className="dashboard-deadline-row"
                        to={`deadlines/${deadline.id}`}
                      >
                        <time
                          className={`dashboard-date-chip is-${dateBadge.tone}`}
                          dateTime={deadline.dueDate}
                        >
                          {dateBadge.label}
                        </time>
                        <span className="dashboard-row-copy">
                          <strong>{deadline.title}</strong>
                          {metadata ? (
                            <span className="dashboard-row-meta">{metadata}</span>
                          ) : null}
                        </span>
                      </Link>
                    </li>
                  )
                })}
              </ul>
            ) : (
              <p className="dashboard-operational-empty">
                Nenhum prazo no período.
              </p>
            )}
          </section>

          <section
            className="dashboard-operational-card"
            aria-labelledby="upcoming-tasks-title"
          >
            <header className="dashboard-operational-card-header">
              <div>
                <span className="dashboard-mini-icon">
                  <DashboardIcon name="tasks" />
                </span>
                <h3 id="upcoming-tasks-title">Tarefas pendentes</h3>
              </div>
              <Link
                className="dashboard-section-link"
                to="tasks"
                aria-label="Ver todas as tarefas"
              >
                Ver todas
              </Link>
            </header>

            {upcoming.tasks.length > 0 ? (
              <ul className="dashboard-operational-list dashboard-task-list">
                {upcoming.tasks.slice(0, 5).map((task) => {
                  const metadata = joinMetadata(task.processTitle, task.clientName)

                  return (
                    <li key={task.id}>
                      <Link
                        className="dashboard-task-row"
                        to={`tasks/${task.id}`}
                      >
                        <span className="dashboard-task-marker" aria-hidden="true" />
                        <span className="dashboard-row-copy">
                          <strong>{task.title}</strong>
                          {metadata ? (
                            <span className="dashboard-row-meta">{metadata}</span>
                          ) : null}
                          {task.assigneeDisplayName ? (
                            <span className="dashboard-row-meta dashboard-row-meta-subtle">
                              {task.assigneeDisplayName}
                            </span>
                          ) : null}
                        </span>
                        <time
                          className="dashboard-task-date"
                          dateTime={task.dueDate}
                        >
                          {formatDashboardShortDateOnly(task.dueDate)}
                        </time>
                      </Link>
                    </li>
                  )
                })}
              </ul>
            ) : (
              <p className="dashboard-operational-empty">
                Nenhuma tarefa no período.
              </p>
            )}
          </section>
        </div>
      </section>

      <DashboardSupplementaryModules
        key={`${currentOrganization.id}:${currentOrganization.role}`}
        currentOrganization={currentOrganization}
        onUnauthorized={onUnauthorized}
      />
    </>
  )
}

type DashboardSupplementaryState =
  | { readonly status: 'loading' }
  | {
      readonly status: 'success'
      readonly data: DashboardSupplementaryData
    }

function DashboardSupplementaryModules({
  currentOrganization,
  onUnauthorized,
}: {
  readonly currentOrganization: OrganizationNavigationItem
  readonly onUnauthorized: UnauthorizedHandler
}) {
  const [state, setState] = useState<DashboardSupplementaryState>({
    status: 'loading',
  })

  useEffect(() => {
    const controller = new AbortController()

    void getDashboardSupplementaryData(
      currentOrganization.id,
      currentOrganization.role,
      onUnauthorized,
      controller.signal,
    )
      .then((data) => {
        if (!controller.signal.aborted) {
          setState({ status: 'success', data })
        }
      })
      .catch((error: unknown) => {
        if (controller.signal.aborted || isAbortError(error)) {
          return
        }

        setState({
          status: 'success',
          data: {
            processes: { status: 'error', items: [] },
            documents: { status: 'error', items: [] },
            invitations:
              currentOrganization.role === 'Member'
                ? null
                : { status: 'error', items: [] },
            auditLogs:
              currentOrganization.role === 'Member'
                ? null
                : { status: 'error', items: [] },
            team: { status: 'error', items: [] },
          },
        })
      })

    return () => {
      controller.abort()
    }
  }, [
    currentOrganization.id,
    currentOrganization.role,
    onUnauthorized,
  ])

  if (state.status === 'loading') {
    return (
      <section
        className="dashboard-secondary-section"
        aria-label="Atividade recente e administração"
        aria-busy="true"
      >
        <div className="dashboard-secondary-loading" role="status">
          Carregando atividade recente…
        </div>
      </section>
    )
  }

  const { processes, documents, invitations, auditLogs, team } = state.data

  return (
    <section
      className="dashboard-secondary-section"
      aria-label="Atividade recente e administração"
    >
      <div className="dashboard-secondary-grid">
        <section
          className="dashboard-summary-card"
          aria-labelledby="dashboard-recent-processes-title"
        >
          <header className="dashboard-operational-card-header">
            <div>
              <span className="dashboard-mini-icon">
                <DashboardIcon name="processes" />
              </span>
              <h3 id="dashboard-recent-processes-title">Processos recentes</h3>
            </div>
            <Link
              className="dashboard-section-link"
              to="processes"
              aria-label="Ver todos os processos"
            >
              Ver todos
            </Link>
          </header>

          {processes.status === 'error' ? (
            <p className="dashboard-summary-empty">Resumo indisponível.</p>
          ) : processes.items.length === 0 ? (
            <p className="dashboard-summary-empty">Nenhum processo cadastrado.</p>
          ) : (
            <ul className="dashboard-summary-list">
              {processes.items.map((process) => (
                <li key={process.id}>
                  <Link
                    className="dashboard-summary-row"
                    to={`processes/${process.id}`}
                  >
                    <span className="dashboard-row-copy">
                      <strong>{process.title}</strong>
                      <span className="dashboard-row-meta">
                        {process.clientName}
                      </span>
                    </span>
                    <time
                      className="dashboard-summary-date"
                      dateTime={process.createdAt}
                    >
                      {formatSupplementaryDate(process.createdAt)}
                    </time>
                  </Link>
                </li>
              ))}
            </ul>
          )}
        </section>

        <section
          className="dashboard-summary-card"
          aria-labelledby="dashboard-recent-documents-title"
        >
          <header className="dashboard-operational-card-header">
            <div>
              <span className="dashboard-mini-icon">
                <DashboardIcon name="documents" />
              </span>
              <h3 id="dashboard-recent-documents-title">Documentos recentes</h3>
            </div>
            <Link
              className="dashboard-section-link"
              to="documents"
              aria-label="Ver todos os documentos"
            >
              Ver todos
            </Link>
          </header>

          {documents.status === 'error' ? (
            <p className="dashboard-summary-empty">Resumo indisponível.</p>
          ) : documents.items.length === 0 ? (
            <p className="dashboard-summary-empty">Nenhum documento enviado.</p>
          ) : (
            <ul className="dashboard-summary-list">
              {documents.items.map((document) => (
                <li key={document.id}>
                  <Link
                    className="dashboard-summary-row dashboard-document-row"
                    to={`documents/${document.id}`}
                  >
                    <span className="dashboard-document-mark" aria-hidden="true">
                      <DashboardIcon name="documents" />
                    </span>
                    <span className="dashboard-row-copy">
                      <strong>{document.originalFileName}</strong>
                      <span className="dashboard-row-meta">
                        {getDocumentContextLabel(document)}
                      </span>
                    </span>
                    <time
                      className="dashboard-summary-date"
                      dateTime={document.createdAt}
                    >
                      {formatSupplementaryDate(document.createdAt)}
                    </time>
                  </Link>
                </li>
              ))}
            </ul>
          )}
        </section>

        <div className="dashboard-secondary-stack">
          {invitations ? (
            <section
              className="dashboard-summary-card dashboard-summary-card-compact"
              aria-labelledby="dashboard-pending-invitations-title"
            >
              <header className="dashboard-operational-card-header">
                <div>
                  <span className="dashboard-mini-icon">
                    <DashboardIcon name="invitations" />
                  </span>
                  <h3 id="dashboard-pending-invitations-title">
                    Convites pendentes
                  </h3>
                </div>
                <Link
                  className="dashboard-section-link"
                  to="invitations"
                  aria-label="Ver todos os convites"
                >
                  Ver todos
                </Link>
              </header>

              {invitations.status === 'error' ? (
                <p className="dashboard-summary-empty">Resumo indisponível.</p>
              ) : invitations.items.length === 0 ? (
                <p className="dashboard-summary-empty">
                  Nenhum convite pendente nesta visualização.
                </p>
              ) : (
                <ul className="dashboard-summary-list dashboard-summary-list-compact">
                  {invitations.items.map((invitation) => (
                    <li key={invitation.id}>
                      <div className="dashboard-summary-row">
                        <span className="dashboard-row-copy">
                          <strong>{invitation.invitedEmail}</strong>
                          <span className="dashboard-row-meta">
                            {getOrganizationRoleLabel(invitation.role)}
                          </span>
                        </span>
                        <time
                          className="dashboard-summary-date"
                          dateTime={invitation.createdAt}
                        >
                          {formatSupplementaryDate(invitation.createdAt)}
                        </time>
                      </div>
                    </li>
                  ))}
                </ul>
              )}
            </section>
          ) : null}

          {auditLogs ? (
            <section
              className="dashboard-summary-card dashboard-summary-card-compact"
              aria-labelledby="dashboard-recent-audit-title"
            >
              <header className="dashboard-operational-card-header">
                <div>
                  <span className="dashboard-mini-icon">
                    <DashboardIcon name="audit" />
                  </span>
                  <h3 id="dashboard-recent-audit-title">Auditoria recente</h3>
                </div>
                <Link
                  className="dashboard-section-link"
                  to="audit-log"
                  aria-label="Ver toda a auditoria"
                >
                  Ver todos
                </Link>
              </header>

              {auditLogs.status === 'error' ? (
                <p className="dashboard-summary-empty">Resumo indisponível.</p>
              ) : auditLogs.items.length === 0 ? (
                <p className="dashboard-summary-empty">
                  Nenhuma atividade registrada.
                </p>
              ) : (
                <ul className="dashboard-summary-list dashboard-summary-list-compact">
                  {auditLogs.items.map((item) => (
                    <li key={item.id}>
                      <div className="dashboard-audit-row">
                        <span className="dashboard-row-copy">
                          <strong>{getAuditEventLabel(item.eventType)}</strong>
                          <span className="dashboard-row-meta">
                            {getAuditEntityLabel(item.entityType)}
                          </span>
                        </span>
                        <time
                          className="dashboard-audit-time"
                          dateTime={item.occurredAt}
                        >
                          {formatAuditTimestamp(item.occurredAt)}
                        </time>
                      </div>
                    </li>
                  ))}
                </ul>
              )}
            </section>
          ) : null}

          <section
            className="dashboard-summary-card dashboard-team-summary-card"
            aria-labelledby="dashboard-team-title"
          >
            <header className="dashboard-operational-card-header">
              <div>
                <span className="dashboard-mini-icon">
                  <DashboardIcon name="team" />
                </span>
                <h3 id="dashboard-team-title">Equipe</h3>
              </div>
              <Link
                className="dashboard-section-link"
                to="team"
                aria-label="Ver equipe"
              >
                Ver equipe
              </Link>
            </header>

            {team.status === 'error' ? (
              <p className="dashboard-summary-empty">Resumo indisponível.</p>
            ) : team.items.length === 0 ? (
              <p className="dashboard-summary-empty">
                Nenhum integrante ativo.
              </p>
            ) : (
              <div className="dashboard-team-list">
                {team.items.map((member) => (
                  <Link
                    className="dashboard-team-member"
                    to="team"
                    key={member.id}
                    aria-label={`${member.name}, ${getOrganizationRoleLabel(member.role)}`}
                  >
                    <span className="dashboard-team-avatar" aria-hidden="true">
                      {getInitials(member.name)}
                    </span>
                    <strong>{member.name}</strong>
                    <span>{getOrganizationRoleLabel(member.role)}</span>
                  </Link>
                ))}

                {(team.totalCount ?? team.items.length) > team.items.length ? (
                  <Link
                    className="dashboard-team-member dashboard-team-member-more"
                    to="team"
                    aria-label={`Ver mais ${(team.totalCount ?? team.items.length) - team.items.length} integrantes`}
                  >
                    <span className="dashboard-team-avatar" aria-hidden="true">
                      +{(team.totalCount ?? team.items.length) - team.items.length}
                    </span>
                    <strong>Mais</strong>
                    <span>integrantes</span>
                  </Link>
                ) : null}
              </div>
            )}
          </section>
        </div>
      </div>
    </section>
  )
}
