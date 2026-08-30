import { useEffect, useRef, useState, type FormEvent } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { useAuth } from '../authentication/AuthContext'
import {
  useCurrentOrganization,
  useOrganizationDiscovery,
} from '../organizations/OrganizationContext'
import {
  auditEntityOptions,
  auditEventOptions,
  formatAuditTimestamp,
  getAuditChangedFieldLabel,
  getAuditEntityLabel,
  getAuditEventLabel,
  getAuditRoleLabel,
} from './auditLogFormatting'
import {
  AuditLogRequestError,
  isAuditEntityType,
  isAuditEventType,
  isUsableGuid,
  listAuditLogs,
} from './auditLogService'
import type { AuditLogDetails, AuditLogPageResponse } from './auditLogTypes'

const pageSize = 20
const maximumPageNumber = Math.floor(2_147_483_647 / pageSize) + 1

type ListState =
  | { readonly status: 'loading'; readonly scope: string }
  | {
      readonly status: 'success'
      readonly scope: string
      readonly response: AuditLogPageResponse
    }
  | {
      readonly status: 'invalid' | 'forbidden' | 'error'
      readonly scope: string
    }

function resolvePage(value: string | null): number {
  if (value === null || !/^[1-9]\d*$/.test(value)) return 1
  const page = Number(value)
  return Number.isSafeInteger(page) && page <= maximumPageNumber ? page : 1
}

function isAbortError(error: unknown): boolean {
  return error instanceof DOMException && error.name === 'AbortError'
}

function MembershipValue({ value }: { readonly value: string | null }) {
  return value === null ? (
    <>Não atribuído</>
  ) : (
    <code className="audit-membership-id">{value}</code>
  )
}

function AuditDetails({ details }: { readonly details: AuditLogDetails | null }) {
  if (details === null) return <span>Sem detalhes adicionais.</span>

  switch (details.type) {
    case 'organization.renamed':
      return (
        <dl className="audit-details-list">
          <div><dt>Nome anterior</dt><dd>{details.oldName}</dd></div>
          <div><dt>Novo nome</dt><dd>{details.newName}</dd></div>
        </dl>
      )
    case 'organization_membership.role_changed':
      return (
        <dl className="audit-details-list">
          <div><dt>Papel anterior</dt><dd>{getAuditRoleLabel(details.oldRole)}</dd></div>
          <div><dt>Novo papel</dt><dd>{getAuditRoleLabel(details.newRole)}</dd></div>
        </dl>
      )
    case 'legal_deadline.details_changed':
    case 'legal_task.details_changed':
    case 'calendar_event.updated':
      return (
        <span>
          Campos alterados: {details.changedFields.map(getAuditChangedFieldLabel).join(', ')}
        </span>
      )
    case 'legal_task.assignee_changed':
    case 'calendar_event.assignee_changed':
      return (
        <dl className="audit-details-list">
          <div>
            <dt>Responsável anterior</dt>
            <dd><MembershipValue value={details.oldAssigneeMembershipId} /></dd>
          </div>
          <div>
            <dt>Novo responsável</dt>
            <dd><MembershipValue value={details.newAssigneeMembershipId} /></dd>
          </div>
        </dl>
      )
    case 'unsupported':
      return <span>Detalhes indisponíveis para este tipo de evento.</span>
  }
}

export function AuditLogPage() {
  const { currentOrganization } = useCurrentOrganization()
  return <OrganizationAuditLogPage key={currentOrganization.id} />
}

function OrganizationAuditLogPage() {
  const { currentOrganization } = useCurrentOrganization()
  const { refreshOrganizations } = useOrganizationDiscovery()
  const { handleUnauthorized } = useAuth()
  const [searchParams, setSearchParams] = useSearchParams()
  const rawEventType = searchParams.get('eventType') || undefined
  const eventType = rawEventType && isAuditEventType(rawEventType)
    ? rawEventType
    : undefined
  const rawEntityType = searchParams.get('entityType') || undefined
  const entityType = rawEntityType && isAuditEntityType(rawEntityType)
    ? rawEntityType
    : undefined
  const rawEntityId = searchParams.get('entityId') || undefined
  const entityId = rawEntityId && isUsableGuid(rawEntityId)
    ? rawEntityId
    : undefined
  const rawPage = searchParams.get('page')
  const page = resolvePage(rawPage)
  const isAuthorized = currentOrganization.role !== 'Member'
  const invalidFilters =
    (rawEventType !== undefined && eventType === undefined) ||
    (rawEntityType !== undefined && entityType === undefined) ||
    (rawEntityId !== undefined && entityId === undefined) ||
    (entityType === undefined) !== (entityId === undefined)
  const queryScope = `${currentOrganization.id}:${currentOrganization.membershipId}:${currentOrganization.role}:${eventType ?? ''}:${entityType ?? ''}:${entityId ?? ''}:${page}`
  const [listState, setListState] = useState<ListState>({
    status: 'loading',
    scope: queryScope,
  })
  const [refreshVersion, setRefreshVersion] = useState(0)
  const [validationError, setValidationError] = useState<string>()
  const requestIdRef = useRef(0)

  useEffect(() => {
    if (!isAuthorized || invalidFilters) return

    const controller = new AbortController()
    const requestId = ++requestIdRef.current

    void listAuditLogs(
      currentOrganization.id,
      { eventType, entityType, entityId, pageNumber: page, pageSize },
      handleUnauthorized,
      controller.signal,
    )
      .then((response) => {
        if (!controller.signal.aborted && requestId === requestIdRef.current) {
          setListState({ status: 'success', scope: queryScope, response })
        }
      })
      .catch((error: unknown) => {
        if (
          controller.signal.aborted ||
          requestId !== requestIdRef.current ||
          isAbortError(error) ||
          (error instanceof AuditLogRequestError && error.failure === 'unauthorized')
        ) {
          return
        }

        setListState({
          status:
            error instanceof AuditLogRequestError
              ? error.failure === 'forbidden'
                ? 'forbidden'
                : error.failure === 'bad-request'
                  ? 'invalid'
                  : 'error'
              : 'error',
          scope: queryScope,
        })
      })

    return () => controller.abort()
  }, [
    currentOrganization.id,
    entityId,
    entityType,
    eventType,
    handleUnauthorized,
    invalidFilters,
    isAuthorized,
    page,
    queryScope,
    refreshVersion,
  ])

  const currentListState: ListState = invalidFilters
    ? { status: 'invalid', scope: queryScope }
    : listState.scope === queryScope
      ? listState
      : { status: 'loading', scope: queryScope }
  const isFiltered = Boolean(eventType || entityType)

  function applyFilters(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const data = new FormData(event.currentTarget)
    const nextEventType = String(data.get('eventType') ?? '')
    const nextEntityType = String(data.get('entityType') ?? '')
    const nextEntityId = String(data.get('entityId') ?? '').trim()

    if (nextEventType && !isAuditEventType(nextEventType)) {
      setValidationError('Selecione um tipo de evento válido.')
      return
    }
    if ((nextEntityType.length === 0) !== (nextEntityId.length === 0)) {
      setValidationError('Informe o tipo e o identificador da entidade juntos.')
      return
    }
    if (nextEntityType && !isAuditEntityType(nextEntityType)) {
      setValidationError('Selecione um tipo de entidade válido.')
      return
    }
    if (nextEntityId && !isUsableGuid(nextEntityId)) {
      setValidationError('Informe um identificador de entidade válido.')
      return
    }

    setValidationError(undefined)
    const next = new URLSearchParams()
    if (nextEventType) next.set('eventType', nextEventType)
    if (nextEntityType && nextEntityId) {
      next.set('entityType', nextEntityType)
      next.set('entityId', nextEntityId)
    }
    setSearchParams(next)
  }

  function clearFilters() {
    setValidationError(undefined)
    setSearchParams({})
  }

  function navigateToPage(nextPage: number) {
    const next = new URLSearchParams(searchParams)
    if (nextPage === 1) next.delete('page')
    else next.set('page', nextPage.toString())
    setSearchParams(next)
  }

  return (
    <section className="audit-log-page" aria-labelledby="audit-log-title">
      <header className="audit-log-header">
        <p className="eyebrow">Administração</p>
        <h2 id="audit-log-title">Audit Log</h2>
        <p className="audit-log-description">
          Consulte os eventos administrativos registrados nesta organização.
        </p>
      </header>

      {!isAuthorized ? (
        <div className="audit-log-state" role="alert">
          <h3>Acesso negado</h3>
          <p>Somente proprietários e administradores podem consultar o Audit Log.</p>
          <Link className="home-link" to="..">Voltar para a visão geral</Link>
        </div>
      ) : (
        <>
          <form
            key={`${rawEventType ?? ''}:${rawEntityType ?? ''}:${rawEntityId ?? ''}`}
            className="audit-log-filters"
            aria-label="Filtros do Audit Log"
            onSubmit={applyFilters}
          >
            <div className="audit-filter-control">
              <label htmlFor="audit-event-type">Tipo de evento</label>
              <select id="audit-event-type" name="eventType" defaultValue={eventType ?? ''}>
                <option value="">Todos os eventos</option>
                {auditEventOptions.map((option) => (
                  <option key={option.value} value={option.value}>{option.label}</option>
                ))}
              </select>
            </div>
            <div className="audit-filter-control">
              <label htmlFor="audit-entity-type">Tipo de entidade</label>
              <select id="audit-entity-type" name="entityType" defaultValue={entityType ?? ''}>
                <option value="">Todas as entidades</option>
                {auditEntityOptions.map((option) => (
                  <option key={option.value} value={option.value}>{option.label}</option>
                ))}
              </select>
            </div>
            <div className="audit-filter-control">
              <label htmlFor="audit-entity-id">Identificador da entidade</label>
              <input
                id="audit-entity-id"
                name="entityId"
                defaultValue={entityId ?? ''}
                placeholder="UUID da entidade"
                aria-describedby={validationError ? 'audit-filter-error' : 'audit-entity-help'}
                aria-invalid={validationError ? true : undefined}
              />
              <small id="audit-entity-help">Use junto com o tipo de entidade.</small>
            </div>
            <div className="audit-filter-actions">
              <button className="primary-button" type="submit">Aplicar filtros</button>
              <button className="secondary-button" type="button" onClick={clearFilters}>
                Limpar
              </button>
            </div>
            {validationError ? (
              <p id="audit-filter-error" className="form-error audit-filter-error" role="alert">
                {validationError}
              </p>
            ) : null}
          </form>

          {currentListState.status === 'loading' ? (
            <div className="audit-log-state" role="status"><p>Carregando eventos...</p></div>
          ) : null}
          {currentListState.status === 'invalid' ? (
            <div className="audit-log-state" role="alert">
              <h3>Não foi possível aplicar os filtros</h3>
              <p>Revise os filtros informados e tente novamente.</p>
              <button className="secondary-button" type="button" onClick={clearFilters}>
                Limpar filtros
              </button>
            </div>
          ) : null}
          {currentListState.status === 'forbidden' ? (
            <div className="audit-log-state" role="alert">
              <h3>Acesso ao Audit Log negado</h3>
              <p>Seu acesso administrativo à organização pode ter mudado.</p>
              <button className="secondary-button" type="button" onClick={refreshOrganizations}>
                Atualizar acesso
              </button>
            </div>
          ) : null}
          {currentListState.status === 'error' ? (
            <div className="audit-log-state" role="alert">
              <h3>Não foi possível carregar o Audit Log</h3>
              <p>Verifique sua conexão e tente novamente.</p>
              <button
                className="secondary-button"
                type="button"
                onClick={() => setRefreshVersion((version) => version + 1)}
              >
                Tentar novamente
              </button>
            </div>
          ) : null}
          {currentListState.status === 'success' && currentListState.response.items.length === 0 ? (
            <div className="audit-log-state" role="status">
              <h3>
                {currentListState.response.totalCount > 0
                  ? 'Nenhum evento nesta página'
                  : isFiltered
                    ? 'Nenhum evento encontrado'
                    : 'Nenhum evento registrado'}
              </h3>
              <p>
                {currentListState.response.totalCount > 0
                  ? 'Volte para uma página anterior.'
                  : isFiltered
                    ? 'Ajuste os filtros para tentar novamente.'
                    : 'Os eventos administrativos desta organização aparecerão aqui.'}
              </p>
            </div>
          ) : null}

          {currentListState.status === 'success' ? (
            <>
              {currentListState.response.items.length > 0 ? (
                <div className="audit-log-table-wrapper">
                  <table className="audit-log-table">
                    <caption className="visually-hidden">
                      Eventos administrativos da organização {currentOrganization.name}
                    </caption>
                    <thead>
                      <tr>
                        <th scope="col">Evento</th>
                        <th scope="col">Entidade</th>
                        <th scope="col">Data e hora</th>
                        <th scope="col">Papel do ator</th>
                        <th scope="col">Detalhes</th>
                      </tr>
                    </thead>
                    <tbody>
                      {currentListState.response.items.map((item) => (
                        <tr key={item.id}>
                          <td data-label="Evento">{getAuditEventLabel(item.eventType)}</td>
                          <td data-label="Entidade">
                            <span>{getAuditEntityLabel(item.entityType)}</span>
                            <code className="audit-entity-id">{item.entityId}</code>
                          </td>
                          <td data-label="Data e hora">
                            <time dateTime={item.occurredAt}>{formatAuditTimestamp(item.occurredAt)}</time>
                          </td>
                          <td data-label="Papel do ator">{getAuditRoleLabel(item.actorRoleAtOccurrence)}</td>
                          <td data-label="Detalhes"><AuditDetails details={item.details} /></td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              ) : null}

              <nav className="audit-log-pagination" aria-label="Paginação do Audit Log">
                <button
                  className="secondary-button"
                  type="button"
                  disabled={page === 1}
                  onClick={() => navigateToPage(page - 1)}
                >
                  Página anterior
                </button>
                <span aria-current="page">
                  Página {page} de {Math.max(1, Math.ceil(currentListState.response.totalCount / pageSize))}
                </span>
                <span>
                  {currentListState.response.totalCount.toLocaleString('pt-BR')}{' '}
                  {currentListState.response.totalCount === 1 ? 'evento' : 'eventos'} no total
                </span>
                <button
                  className="secondary-button"
                  type="button"
                  disabled={page * pageSize >= currentListState.response.totalCount}
                  onClick={() => navigateToPage(page + 1)}
                >
                  Próxima página
                </button>
              </nav>
            </>
          ) : null}
        </>
      )}
    </section>
  )
}
