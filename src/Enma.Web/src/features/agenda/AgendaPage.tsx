import { useEffect, useMemo, useRef, useState } from 'react'
import { useAuth } from '../authentication/AuthContext'
import {
  useCurrentOrganization,
  useOrganizationDiscovery,
} from '../organizations/OrganizationContext'
import type { OrganizationNavigationItem } from '../organizations/organizationTypes'
import { lookupOrganizationMembers } from '../tasks/organizationMemberLookupService'
import { TaskLookupPicker } from '../tasks/TaskLookupPicker'
import type { OrganizationMemberLookupItem } from '../tasks/legalTaskTypes'
import {
  addCalendarMonths,
  agendaItemOccursOnDate,
  calendarDateFromLocalDate,
  createMonthViewport,
  dateTimeLocalValueFromInstant,
  defaultEventLocalTimes,
  localDateFromCalendarDate,
  parseCalendarDate,
  type CalendarDateParts,
} from './agendaDateTime'
import {
  AgendaRequestError,
  changeCalendarEventAssignee,
  createCalendarEvent,
  deleteCalendarEvent,
  getAgenda,
  getCalendarEvent,
  updateCalendarEvent,
} from './agendaService'
import type {
  AgendaItem,
  AgendaResponse,
  CalendarEventDetail,
  CalendarEventFormValue,
  CreateCalendarEventRequest,
  UpdateCalendarEventRequest,
} from './agendaTypes'
import { CalendarEventForm } from './CalendarEventForm'

const weekdayLabels = ['Dom', 'Seg', 'Ter', 'Qua', 'Qui', 'Sex', 'Sáb']
const visibleItemsPerCell = 3

const monthFormatter = new Intl.DateTimeFormat('pt-BR', {
  month: 'long',
  year: 'numeric',
})
const fullDateFormatter = new Intl.DateTimeFormat('pt-BR', {
  weekday: 'long',
  day: 'numeric',
  month: 'long',
  year: 'numeric',
})
const eventTimeFormatter = new Intl.DateTimeFormat('pt-BR', {
  hour: '2-digit',
  minute: '2-digit',
})
const eventDateTimeFormatter = new Intl.DateTimeFormat('pt-BR', {
  dateStyle: 'short',
  timeStyle: 'short',
})

type AgendaState =
  | { readonly status: 'loading'; readonly scope: string }
  | {
      readonly status: 'success'
      readonly scope: string
      readonly response: AgendaResponse
    }
  | { readonly status: 'forbidden' | 'error'; readonly scope: string }

type Selection =
  | {
      readonly kind: 'item'
      readonly itemKind: AgendaItem['kind']
      readonly itemId: string
    }
  | { readonly kind: 'day'; readonly date: string }

type DetailState =
  | { readonly status: 'idle' }
  | { readonly status: 'loading'; readonly scope: string }
  | {
      readonly status: 'success'
      readonly scope: string
      readonly detail: CalendarEventDetail
    }
  | { readonly status: 'forbidden' | 'not-found' | 'error'; readonly scope: string }

function isAbortError(error: unknown): boolean {
  return error instanceof DOMException && error.name === 'AbortError'
}

function todayMonth(): CalendarDateParts {
  const now = new Date()
  return { year: now.getFullYear(), month: now.getMonth() + 1, day: 1 }
}

function formatMonth(parts: CalendarDateParts): string {
  const value = localDateFromCalendarDate(parts)
  const formatted = monthFormatter.format(value)
  return formatted.charAt(0).toUpperCase() + formatted.slice(1)
}

function formatFullDate(dateKey: string): string {
  const parts = parseCalendarDate(dateKey)
  if (!parts) return dateKey
  return fullDateFormatter.format(localDateFromCalendarDate(parts))
}

function getKindLabel(kind: AgendaItem['kind']): string {
  if (kind === 'deadline') return 'Prazo'
  if (kind === 'task') return 'Tarefa'
  return 'Evento'
}

function itemDateKey(item: AgendaItem): string | undefined {
  if (item.kind !== 'calendarEvent') return item.date ?? undefined
  if (!item.startsAt) return undefined
  return calendarDateFromLocalDate(new Date(item.startsAt))
}

function formatItemTime(item: AgendaItem, dateKey: string): string | undefined {
  if (item.kind !== 'calendarEvent' || !item.startsAt) return undefined
  if (itemDateKey(item) !== dateKey) return 'Continua'
  return eventTimeFormatter.format(new Date(item.startsAt))
}

function sortItems(items: readonly AgendaItem[]): AgendaItem[] {
  return [...items].sort((left, right) => {
    if (left.kind === 'calendarEvent' && right.kind === 'calendarEvent') {
      return (left.startsAt ?? '').localeCompare(right.startsAt ?? '')
    }
    if (left.kind === 'calendarEvent') return 1
    if (right.kind === 'calendarEvent') return -1
    return left.kind.localeCompare(right.kind) || left.title.localeCompare(right.title)
  })
}

function initialCreateValue(month: CalendarDateParts): CalendarEventFormValue {
  const times = defaultEventLocalTimes(month)
  return {
    title: '',
    description: '',
    startsAt: times.startsAt,
    endsAt: times.endsAt,
    location: '',
    association: 'general',
  }
}

function editValue(
  detail: CalendarEventDetail,
  relatedClientName?: string | null,
): CalendarEventFormValue {
  return {
    title: detail.title,
    description: detail.description ?? '',
    startsAt: dateTimeLocalValueFromInstant(detail.startsAt),
    endsAt: dateTimeLocalValueFromInstant(detail.endsAt),
    originalStartsAt: detail.startsAt,
    originalEndsAt: detail.endsAt,
    location: detail.location ?? '',
    association: detail.processId
      ? 'process'
      : detail.clientId
        ? 'client'
        : 'general',
    client:
      detail.clientId && !detail.processId
        ? { id: detail.clientId, name: detail.clientName ?? 'Cliente vinculado' }
        : undefined,
    process: detail.processId
      ? {
          id: detail.processId,
          title: detail.processTitle ?? 'Processo vinculado',
          clientName:
            detail.clientName ?? relatedClientName ?? 'Cliente vinculado',
        }
      : undefined,
  }
}

interface AgendaItemButtonProps {
  readonly item: AgendaItem
  readonly dateKey: string
  readonly onSelect: (item: AgendaItem) => void
}

function AgendaItemButton({ item, dateKey, onSelect }: AgendaItemButtonProps) {
  const time = formatItemTime(item, dateKey)
  const completed = item.completedAt !== null
  return (
    <button
      className={`agenda-item agenda-item-${item.kind}${completed ? ' is-completed' : ''}`}
      type="button"
      onClick={() => onSelect(item)}
      aria-label={`${getKindLabel(item.kind)}: ${item.title}${completed ? ', concluído' : ''}`}
    >
      <span className="agenda-item-kind">{getKindLabel(item.kind)}</span>
      {time ? <time>{time}</time> : null}
      <span className="agenda-item-title">{item.title}</span>
      {completed ? <span className="agenda-item-completed">Concluído</span> : null}
    </button>
  )
}

export function AgendaPage() {
  const { currentOrganization } = useCurrentOrganization()

  return (
    <OrganizationAgendaPage
      key={currentOrganization.id}
      currentOrganization={currentOrganization}
    />
  )
}

interface OrganizationAgendaPageProps {
  readonly currentOrganization: OrganizationNavigationItem
}

function OrganizationAgendaPage({
  currentOrganization,
}: OrganizationAgendaPageProps) {
  const { refreshOrganizations } = useOrganizationDiscovery()
  const { handleUnauthorized } = useAuth()
  const [month, setMonth] = useState<CalendarDateParts>(todayMonth)
  const viewport = useMemo(() => createMonthViewport(month), [month])
  const [refreshVersion, setRefreshVersion] = useState(0)
  const agendaScope = `${currentOrganization.id}:${viewport.from}:${viewport.to}:${refreshVersion}`
  const [agendaState, setAgendaState] = useState<AgendaState>({
    status: 'loading',
    scope: agendaScope,
  })
  const agendaRequestRef = useRef(0)
  const [selection, setSelection] = useState<Selection>()
  const [isCreating, setIsCreating] = useState(false)
  const [isEditing, setIsEditing] = useState(false)
  const [isDeleteConfirmationOpen, setIsDeleteConfirmationOpen] = useState(false)
  const [isAssignmentOpen, setIsAssignmentOpen] = useState(false)
  const [assignmentMode, setAssignmentMode] = useState<'unassigned' | 'self' | 'other'>('unassigned')
  const [selectedMember, setSelectedMember] = useState<OrganizationMemberLookupItem>()
  const [detailState, setDetailState] = useState<DetailState>({ status: 'idle' })
  const [detailRefreshVersion, setDetailRefreshVersion] = useState(0)
  const [mutationError, setMutationError] = useState<string>()
  const [successMessage, setSuccessMessage] = useState<string>()
  const [isMutating, setIsMutating] = useState(false)
  const mutationRef = useRef(0)
  const mutationControllerRef = useRef<AbortController | undefined>(undefined)

  useEffect(() => {
    const controller = new AbortController()
    const requestId = ++agendaRequestRef.current
    void getAgenda(
      currentOrganization.id,
      viewport.from,
      viewport.to,
      handleUnauthorized,
      controller.signal,
    )
      .then((response) => {
        if (!controller.signal.aborted && requestId === agendaRequestRef.current) {
          setSelection((current) => {
            if (
              current?.kind === 'item' &&
              !response.items.some(
                (item) =>
                  item.kind === current.itemKind && item.id === current.itemId,
              )
            ) {
              return undefined
            }

            return current
          })
          setAgendaState({ status: 'success', scope: agendaScope, response })
        }
      })
      .catch((error: unknown) => {
        if (
          controller.signal.aborted ||
          requestId !== agendaRequestRef.current ||
          isAbortError(error) ||
          (error instanceof AgendaRequestError && error.failure === 'unauthorized')
        ) {
          return
        }
        setAgendaState({
          status:
            error instanceof AgendaRequestError && error.failure === 'forbidden'
              ? 'forbidden'
              : 'error',
          scope: agendaScope,
        })
      })
    return () => controller.abort()
  }, [agendaScope, currentOrganization.id, handleUnauthorized, viewport.from, viewport.to])

  const currentAgendaState: AgendaState =
    agendaState.scope === agendaScope
      ? agendaState
      : { status: 'loading', scope: agendaScope }
  const items =
    currentAgendaState.status === 'success'
      ? currentAgendaState.response.items
      : []
  const selectedItem =
    selection?.kind === 'item'
      ? items.find(
          (item) =>
            item.kind === selection.itemKind && item.id === selection.itemId,
        )
      : undefined
  const selectedCalendarEvent =
    selectedItem?.kind === 'calendarEvent' ? selectedItem : undefined
  const detailScope = selectedCalendarEvent
    ? `${currentOrganization.id}:${selectedCalendarEvent.id}:${detailRefreshVersion}`
    : undefined

  useEffect(() => {
    if (!selectedCalendarEvent || !detailScope) {
      return
    }
    const controller = new AbortController()
    void getCalendarEvent(
      currentOrganization.id,
      selectedCalendarEvent.id,
      handleUnauthorized,
      controller.signal,
    )
      .then((detail) => {
        if (!controller.signal.aborted) {
          setDetailState({ status: 'success', scope: detailScope, detail })
        }
      })
      .catch((error: unknown) => {
        if (
          controller.signal.aborted ||
          isAbortError(error) ||
          (error instanceof AgendaRequestError && error.failure === 'unauthorized')
        ) {
          return
        }
        setDetailState({
          status:
            error instanceof AgendaRequestError && error.failure === 'forbidden'
              ? 'forbidden'
              : error instanceof AgendaRequestError && error.failure === 'not-found'
                ? 'not-found'
                : 'error',
          scope: detailScope,
        })
      })
    return () => controller.abort()
  }, [currentOrganization.id, detailScope, handleUnauthorized, selectedCalendarEvent])

  useEffect(() => {
    return () => {
      agendaRequestRef.current += 1
      mutationRef.current += 1
      mutationControllerRef.current?.abort()
    }
  }, [])

  function itemsForDate(dateKey: string): AgendaItem[] {
    return sortItems(items.filter((item) => agendaItemOccursOnDate(item, dateKey)))
  }

  function closeSurfaces() {
    mutationControllerRef.current?.abort()
    mutationRef.current += 1
    setSelection(undefined)
    setIsCreating(false)
    setIsEditing(false)
    setIsDeleteConfirmationOpen(false)
    setIsAssignmentOpen(false)
    setSelectedMember(undefined)
    setMutationError(undefined)
    setIsMutating(false)
  }

  function navigateMonth(offset: number) {
    closeSurfaces()
    setSuccessMessage(undefined)
    setMonth((current) => addCalendarMonths(current, offset))
  }

  function selectItem(item: AgendaItem) {
    setIsCreating(false)
    setIsEditing(false)
    setIsDeleteConfirmationOpen(false)
    setIsAssignmentOpen(false)
    setMutationError(undefined)
    setSelection({ kind: 'item', itemKind: item.kind, itemId: item.id })
  }

  function mutationMessage(error: unknown): string {
    if (error instanceof AgendaRequestError) {
      if (error.failure === 'forbidden') {
        return 'Você não tem permissão para realizar esta ação.'
      }
      if (error.failure === 'not-found') {
        return 'O evento não está mais disponível. Atualize a Agenda.'
      }
      if (error.failure === 'related-assignee-unavailable') {
        return 'A pessoa selecionada não está mais disponível.'
      }
      if (error.failure === 'bad-request') {
        return 'Verifique os dados do evento e tente novamente.'
      }
    }
    return 'Não foi possível concluir a ação. Tente novamente.'
  }

  async function runMutation(
    action: (signal: AbortSignal) => Promise<void>,
    onSuccess: () => void,
  ) {
    if (isMutating) return
    const operation = ++mutationRef.current
    const controller = new AbortController()
    mutationControllerRef.current = controller
    setIsMutating(true)
    setMutationError(undefined)
    setSuccessMessage(undefined)
    try {
      await action(controller.signal)
      if (!controller.signal.aborted && operation === mutationRef.current) {
        onSuccess()
      }
    } catch (error) {
      if (
        controller.signal.aborted ||
        operation !== mutationRef.current ||
        isAbortError(error) ||
        (error instanceof AgendaRequestError && error.failure === 'unauthorized')
      ) {
        return
      }
      setMutationError(mutationMessage(error))
    } finally {
      if (!controller.signal.aborted && operation === mutationRef.current) {
        mutationControllerRef.current = undefined
        setIsMutating(false)
      }
    }
  }

  function submitCreate(request: CreateCalendarEventRequest | UpdateCalendarEventRequest) {
    const createRequest = request as CreateCalendarEventRequest
    void runMutation(
      async (signal) => {
        await createCalendarEvent(
          currentOrganization.id,
          createRequest,
          handleUnauthorized,
          signal,
        )
      },
      () => {
        setIsCreating(false)
        setSuccessMessage('Evento criado com sucesso.')
        setRefreshVersion((version) => version + 1)
      },
    )
  }

  const currentDetail =
    detailScope && detailState.status === 'success' && detailState.scope === detailScope
      ? detailState.detail
      : undefined
  const currentDetailState: DetailState =
    !selectedCalendarEvent || !detailScope
      ? { status: 'idle' }
      : detailState.status !== 'idle' && detailState.scope === detailScope
        ? detailState
        : { status: 'loading', scope: detailScope }
  const canMutateDetail =
    currentDetail !== undefined &&
    (currentOrganization.role === 'Owner' ||
      currentOrganization.role === 'Administrator' ||
      currentDetail.createdByMembershipId.toLowerCase() ===
        currentOrganization.membershipId.toLowerCase())

  function submitEdit(request: CreateCalendarEventRequest | UpdateCalendarEventRequest) {
    if (!currentDetail) return
    const updateRequest = request as UpdateCalendarEventRequest
    void runMutation(
      (signal) =>
        updateCalendarEvent(
          currentOrganization.id,
          currentDetail.id,
          updateRequest,
          handleUnauthorized,
          signal,
        ),
      () => {
        setIsEditing(false)
        setSuccessMessage('Evento atualizado com sucesso.')
        setDetailRefreshVersion((version) => version + 1)
        setRefreshVersion((version) => version + 1)
      },
    )
  }

  function submitAssignment() {
    if (!currentDetail) return
    const assigneeMembershipId =
      assignmentMode === 'self'
        ? currentOrganization.membershipId
        : assignmentMode === 'other'
          ? selectedMember?.id
          : null
    if (assigneeMembershipId === undefined) {
      setMutationError('Selecione uma pessoa responsável.')
      return
    }
    void runMutation(
      (signal) =>
        changeCalendarEventAssignee(
          currentOrganization.id,
          currentDetail.id,
          assigneeMembershipId,
          handleUnauthorized,
          signal,
        ),
      () => {
        setIsAssignmentOpen(false)
        setSelectedMember(undefined)
        setSuccessMessage('Responsável atualizado com sucesso.')
        setDetailRefreshVersion((version) => version + 1)
        setRefreshVersion((version) => version + 1)
      },
    )
  }

  const hasItems = items.length > 0

  return (
    <section className="agenda-page" aria-labelledby="agenda-title">
      <div className="agenda-header">
        <div>
          <p className="eyebrow">Compromissos da organização</p>
          <h2 id="agenda-title">Agenda</h2>
          <p className="agenda-description">
            Prazos, tarefas e eventos em uma única visão mensal.
          </p>
        </div>
        {!isCreating ? (
          <button
            className="primary-button"
            type="button"
            onClick={() => {
              closeSurfaces()
              setSuccessMessage(undefined)
              setIsCreating(true)
            }}
          >
            Novo evento
          </button>
        ) : null}
      </div>

      {successMessage ? (
        <p className="success-message" role="status">{successMessage}</p>
      ) : null}

      {isCreating ? (
        <section className="calendar-event-panel" aria-labelledby="new-event-title">
          <h3 id="new-event-title">Novo evento</h3>
          <CalendarEventForm
            key={`${currentOrganization.id}:${month.year}:${month.month}:create`}
            organizationId={currentOrganization.id}
            currentMembershipId={currentOrganization.membershipId}
            organizationRole={currentOrganization.role}
            initialValue={initialCreateValue(month)}
            submitLabel="Criar evento"
            submittingLabel="Criando..."
            isSubmitting={isMutating}
            includeAssignee
            serverError={mutationError}
            onUnauthorized={handleUnauthorized}
            onCancel={() => {
              setIsCreating(false)
              setMutationError(undefined)
            }}
            onSubmit={submitCreate}
          />
        </section>
      ) : null}

      <div className="agenda-toolbar">
        <div className="agenda-month-navigation">
          <button
            className="secondary-button"
            type="button"
            aria-label="Mês anterior"
            onClick={() => navigateMonth(-1)}
          >
            ‹
          </button>
          <button
            className="secondary-button agenda-today-button"
            type="button"
            onClick={() => {
              closeSurfaces()
              setSuccessMessage(undefined)
              setMonth(todayMonth())
            }}
          >
            Hoje
          </button>
          <button
            className="secondary-button"
            type="button"
            aria-label="Próximo mês"
            onClick={() => navigateMonth(1)}
          >
            ›
          </button>
        </div>
        <h3 aria-live="polite">{formatMonth(month)}</h3>
        <div className="agenda-legend" aria-label="Tipos de item">
          <span className="agenda-legend-deadline">Prazo</span>
          <span className="agenda-legend-task">Tarefa</span>
          <span className="agenda-legend-calendarEvent">Evento</span>
        </div>
      </div>

      {currentAgendaState.status === 'loading' ? (
        <p className="agenda-loading" role="status">Atualizando Agenda...</p>
      ) : null}
      {currentAgendaState.status === 'forbidden' ? (
        <div className="agenda-state" role="alert">
          <p>Não foi possível acessar a Agenda desta organização.</p>
          <button className="secondary-button" type="button" onClick={refreshOrganizations}>
            Atualizar acesso
          </button>
        </div>
      ) : null}
      {currentAgendaState.status === 'error' ? (
        <div className="agenda-state" role="alert">
          <p>Não foi possível carregar a Agenda. Tente novamente.</p>
          <button
            className="secondary-button"
            type="button"
            onClick={() => setRefreshVersion((version) => version + 1)}
          >
            Tentar novamente
          </button>
        </div>
      ) : null}
      {currentAgendaState.status === 'success' && !hasItems ? (
        <p className="agenda-empty" role="status">Nenhum item neste período.</p>
      ) : null}

      <div
        className="agenda-calendar"
        aria-busy={currentAgendaState.status === 'loading'}
        aria-label={`Calendário de ${formatMonth(month)}`}
      >
        <div className="agenda-weekdays" aria-hidden="true">
          {weekdayLabels.map((label) => <span key={label}>{label}</span>)}
        </div>
        <div className="agenda-grid">
          {viewport.dates.map((dateKey) => {
            const parts = parseCalendarDate(dateKey)!
            const dateItems = itemsForDate(dateKey)
            const overflowCount = dateItems.length - visibleItemsPerCell
            const isAdjacent = parts.month !== month.month
            const isToday = dateKey === calendarDateFromLocalDate(new Date())
            return (
              <section
                className={`agenda-day${isAdjacent ? ' is-adjacent' : ''}${isToday ? ' is-today' : ''}`}
                key={dateKey}
                aria-label={formatFullDate(dateKey)}
              >
                <time dateTime={dateKey} className="agenda-day-number">{parts.day}</time>
                <div className="agenda-day-items">
                  {dateItems.slice(0, visibleItemsPerCell).map((item) => (
                    <AgendaItemButton
                      key={`${item.kind}:${item.id}`}
                      item={item}
                      dateKey={dateKey}
                      onSelect={selectItem}
                    />
                  ))}
                </div>
                {overflowCount > 0 ? (
                  <button
                    className="agenda-more-button"
                    type="button"
                    onClick={() => {
                      setSelection({ kind: 'day', date: dateKey })
                      setIsCreating(false)
                    }}
                    aria-label={`Ver mais ${overflowCount} ${overflowCount === 1 ? 'item' : 'itens'} de ${formatFullDate(dateKey)}`}
                  >
                    +{overflowCount} mais
                  </button>
                ) : null}
              </section>
            )
          })}
        </div>
      </div>

      <div className="agenda-mobile-list" aria-label={`Lista de ${formatMonth(month)}`}>
        {viewport.dates.map((dateKey) => {
          const dateItems = itemsForDate(dateKey)
          if (dateItems.length === 0) return null
          return (
            <section className="agenda-mobile-day" key={dateKey}>
              <h4>{formatFullDate(dateKey)}</h4>
              {dateItems.map((item) => (
                <AgendaItemButton
                  key={`${item.kind}:${item.id}`}
                  item={item}
                  dateKey={dateKey}
                  onSelect={selectItem}
                />
              ))}
            </section>
          )
        })}
      </div>

      {selection?.kind === 'day' ? (
        <section className="agenda-detail-panel" aria-labelledby="agenda-day-detail-title">
          <div className="agenda-detail-header">
            <h3 id="agenda-day-detail-title">{formatFullDate(selection.date)}</h3>
            <button className="text-button" type="button" onClick={() => setSelection(undefined)}>
              Fechar
            </button>
          </div>
          <div className="agenda-day-detail-items">
            {itemsForDate(selection.date).map((item) => (
              <AgendaItemButton
                key={`${item.kind}:${item.id}`}
                item={item}
                dateKey={selection.date}
                onSelect={selectItem}
              />
            ))}
          </div>
        </section>
      ) : null}

      {selectedItem && selectedItem.kind !== 'calendarEvent' ? (
        <section className="agenda-detail-panel" aria-labelledby="agenda-item-detail-title">
          <div className="agenda-detail-header">
            <div>
              <p className="eyebrow">{getKindLabel(selectedItem.kind)}</p>
              <h3 id="agenda-item-detail-title">{selectedItem.title}</h3>
            </div>
            <button className="text-button" type="button" onClick={() => setSelection(undefined)}>
              Fechar
            </button>
          </div>
          <dl className="agenda-properties">
            <div><dt>Data</dt><dd>{selectedItem.date ? formatFullDate(selectedItem.date) : 'Sem data'}</dd></div>
            <div><dt>Estado</dt><dd>{selectedItem.completedAt ? 'Concluído' : 'Pendente'}</dd></div>
            <div><dt>Processo</dt><dd>{selectedItem.processTitle ?? 'Sem processo'}</dd></div>
            <div><dt>Cliente</dt><dd>{selectedItem.clientName ?? 'Sem cliente'}</dd></div>
            {selectedItem.kind === 'task' ? (
              <div><dt>Responsável</dt><dd>{selectedItem.assigneeDisplayName ?? 'Não atribuída'}</dd></div>
            ) : null}
          </dl>
        </section>
      ) : null}

      {selectedCalendarEvent ? (
        <section className="agenda-detail-panel" aria-labelledby="calendar-event-detail-title">
          <div className="agenda-detail-header">
            <div>
              <p className="eyebrow">Evento</p>
              <h3 id="calendar-event-detail-title">{currentDetail?.title ?? selectedCalendarEvent.title}</h3>
            </div>
            <button className="text-button" type="button" onClick={() => setSelection(undefined)}>
              Fechar
            </button>
          </div>
          {currentDetailState.status === 'loading' || currentDetailState.status === 'idle' ? (
            <p role="status">Carregando evento...</p>
          ) : null}
          {currentDetailState.status === 'forbidden' ? (
            <p className="form-error" role="alert">Não foi possível acessar este evento.</p>
          ) : null}
          {currentDetailState.status === 'not-found' ? (
            <p className="form-error" role="alert">O evento não está disponível.</p>
          ) : null}
          {currentDetailState.status === 'error' ? (
            <div role="alert">
              <p>Não foi possível carregar o evento.</p>
              <button className="secondary-button" type="button" onClick={() => setDetailRefreshVersion((version) => version + 1)}>
                Tentar novamente
              </button>
            </div>
          ) : null}

          {currentDetail ? (
            <>
              {successMessage ? <p className="success-message" role="status">{successMessage}</p> : null}
              {mutationError && !isEditing ? <p className="form-error" role="alert">{mutationError}</p> : null}
              {isEditing ? (
                <CalendarEventForm
                  key={`${currentDetail.id}:${detailRefreshVersion}:edit`}
                  organizationId={currentOrganization.id}
                  currentMembershipId={currentOrganization.membershipId}
                  organizationRole={currentOrganization.role}
                  initialValue={editValue(currentDetail, selectedCalendarEvent.clientName)}
                  submitLabel="Salvar alterações"
                  submittingLabel="Salvando..."
                  isSubmitting={isMutating}
                  includeAssignee={false}
                  serverError={mutationError}
                  onUnauthorized={handleUnauthorized}
                  onCancel={() => {
                    setIsEditing(false)
                    setMutationError(undefined)
                  }}
                  onSubmit={submitEdit}
                />
              ) : (
                <>
                  <dl className="agenda-properties">
                    <div><dt>Início</dt><dd>{eventDateTimeFormatter.format(new Date(currentDetail.startsAt))}</dd></div>
                    <div><dt>Término</dt><dd>{eventDateTimeFormatter.format(new Date(currentDetail.endsAt))}</dd></div>
                    <div><dt>Local</dt><dd>{currentDetail.location ?? 'Sem local'}</dd></div>
                    <div><dt>Processo</dt><dd>{currentDetail.processTitle ?? 'Sem processo'}</dd></div>
                    <div><dt>Cliente</dt><dd>{currentDetail.clientName ?? selectedCalendarEvent.clientName ?? 'Sem cliente'}</dd></div>
                    <div><dt>Responsável</dt><dd>{currentDetail.assigneeDisplayName ?? 'Não atribuído'}</dd></div>
                    <div><dt>Criado por</dt><dd>{currentDetail.createdByDisplayName}</dd></div>
                    <div className="agenda-description-property"><dt>Descrição</dt><dd>{currentDetail.description ?? 'Sem descrição'}</dd></div>
                  </dl>
                  {canMutateDetail ? (
                    <div className="agenda-detail-actions">
                      <button className="secondary-button" type="button" onClick={() => { setIsEditing(true); setMutationError(undefined) }} disabled={isMutating}>
                        Editar evento
                      </button>
                      <button className="secondary-button" type="button" onClick={() => {
                        const isSelf = currentDetail.assigneeMembershipId?.toLowerCase() === currentOrganization.membershipId.toLowerCase()
                        const isOther = currentDetail.assigneeMembershipId !== null && !isSelf
                        setAssignmentMode(isSelf ? 'self' : isOther ? 'other' : 'unassigned')
                        setSelectedMember(
                          isOther && currentDetail.assigneeMembershipId
                            ? {
                                id: currentDetail.assigneeMembershipId,
                                displayName: currentDetail.assigneeDisplayName ?? 'Responsável atual',
                              }
                            : undefined,
                        )
                        setIsAssignmentOpen(true)
                        setMutationError(undefined)
                      }} disabled={isMutating}>
                        Alterar responsável
                      </button>
                      <button className="danger-button" type="button" onClick={() => setIsDeleteConfirmationOpen(true)} disabled={isMutating}>
                        Excluir evento
                      </button>
                    </div>
                  ) : null}
                </>
              )}

              {isAssignmentOpen && canMutateDetail && !isEditing ? (
                <div className="calendar-event-assignment">
                  <h4>Alterar responsável</h4>
                  <label htmlFor="calendar-event-assignee-mode">Nova atribuição</label>
                  <select
                    id="calendar-event-assignee-mode"
                    value={assignmentMode}
                    onChange={(event) => {
                      setAssignmentMode(event.target.value as 'unassigned' | 'self' | 'other')
                      setSelectedMember(undefined)
                      setMutationError(undefined)
                    }}
                    disabled={isMutating}
                  >
                    <option value="unassigned">Não atribuído</option>
                    <option value="self">Eu</option>
                    {currentOrganization.role !== 'Member' ? <option value="other">Outra pessoa</option> : null}
                  </select>
                  {assignmentMode === 'other' && currentOrganization.role !== 'Member' ? (
                    <TaskLookupPicker
                      organizationId={currentOrganization.id}
                      searchLabel="Buscar novo responsável"
                      resultsLabel="Responsáveis encontrados para o evento"
                      loadingMessage="Carregando responsáveis..."
                      emptyMessage="Não há responsáveis disponíveis."
                      noResultsMessage="Nenhum responsável encontrado para esta busca."
                      errorMessage="Não foi possível carregar os responsáveis. Tente novamente."
                      selectedId={selectedMember?.id}
                      disabled={isMutating}
                      load={lookupOrganizationMembers}
                      onUnauthorized={handleUnauthorized}
                      onSelect={setSelectedMember}
                      renderItem={(item) => <span>{item.displayName}</span>}
                    />
                  ) : null}
                  <div className="calendar-event-form-actions">
                    <button className="secondary-button" type="button" onClick={() => setIsAssignmentOpen(false)} disabled={isMutating}>Cancelar</button>
                    <button className="primary-button" type="button" onClick={submitAssignment} disabled={isMutating}>
                      {isMutating ? 'Salvando...' : 'Salvar responsável'}
                    </button>
                  </div>
                </div>
              ) : null}

              {isDeleteConfirmationOpen && canMutateDetail && !isEditing ? (
                <section className="calendar-event-confirmation" aria-labelledby="delete-calendar-event-title">
                  <h4 id="delete-calendar-event-title">Excluir este evento?</h4>
                  <p>Esta ação remove o evento da Agenda e não pode ser desfeita.</p>
                  <div className="calendar-event-form-actions">
                    <button className="secondary-button" type="button" onClick={() => setIsDeleteConfirmationOpen(false)} disabled={isMutating} autoFocus>Cancelar</button>
                    <button
                      className="danger-button"
                      type="button"
                      disabled={isMutating}
                      onClick={() => void runMutation(
                        (signal) => deleteCalendarEvent(currentOrganization.id, currentDetail.id, handleUnauthorized, signal),
                        () => {
                          setSelection(undefined)
                          setIsDeleteConfirmationOpen(false)
                          setSuccessMessage('Evento excluído com sucesso.')
                          setRefreshVersion((version) => version + 1)
                        },
                      )}
                    >
                      {isMutating ? 'Excluindo...' : 'Confirmar exclusão'}
                    </button>
                  </div>
                </section>
              ) : null}
            </>
          ) : null}
        </section>
      ) : null}
    </section>
  )
}
