import { useId, useState, type FormEvent } from 'react'
import type { UnauthorizedHandler } from '../authentication/sessionClient'
import { lookupLegalProcesses } from '../deadlines/legalProcessLookupService'
import { lookupActiveClients } from '../processes/activeClientLookupService'
import type { OrganizationRole } from '../organizations/organizationTypes'
import { lookupOrganizationMembers } from '../tasks/organizationMemberLookupService'
import { TaskLookupPicker } from '../tasks/TaskLookupPicker'
import type { ActiveClientLookupItem } from '../processes/legalProcessTypes'
import type {
  LegalProcessLookupItem,
  OrganizationMemberLookupItem,
} from '../tasks/legalTaskTypes'
import { dateTimeOffsetFromLocalInput } from './agendaDateTime'
import type {
  CalendarEventFormValue,
  CreateCalendarEventRequest,
  UpdateCalendarEventRequest,
} from './agendaTypes'

interface CalendarEventFormProps {
  readonly organizationId: string
  readonly currentMembershipId: string
  readonly organizationRole: OrganizationRole
  readonly initialValue: CalendarEventFormValue
  readonly submitLabel: string
  readonly submittingLabel: string
  readonly isSubmitting: boolean
  readonly includeAssignee: boolean
  readonly serverError?: string
  readonly onUnauthorized: UnauthorizedHandler
  readonly onCancel: () => void
  readonly onSubmit: (
    request: CreateCalendarEventRequest | UpdateCalendarEventRequest,
  ) => void
}

const maximumTitleLength = 150
const maximumDescriptionLength = 2_000
const maximumLocationLength = 255

export function CalendarEventForm({
  organizationId,
  currentMembershipId,
  organizationRole,
  initialValue,
  submitLabel,
  submittingLabel,
  isSubmitting,
  includeAssignee,
  serverError,
  onUnauthorized,
  onCancel,
  onSubmit,
}: CalendarEventFormProps) {
  const prefix = useId()
  const [title, setTitle] = useState(initialValue.title)
  const [description, setDescription] = useState(initialValue.description)
  const [startsAt, setStartsAt] = useState(initialValue.startsAt)
  const [endsAt, setEndsAt] = useState(initialValue.endsAt)
  const [location, setLocation] = useState(initialValue.location)
  const [association, setAssociation] = useState(initialValue.association)
  const [client, setClient] = useState<ActiveClientLookupItem | undefined>(
    initialValue.client,
  )
  const [process, setProcess] = useState<LegalProcessLookupItem | undefined>(
    initialValue.process,
  )
  const [lookupOpen, setLookupOpen] = useState(false)
  const [assigneeMode, setAssigneeMode] = useState<
    'unassigned' | 'self' | 'other'
  >('unassigned')
  const [member, setMember] = useState<OrganizationMemberLookupItem>()
  const [memberLookupOpen, setMemberLookupOpen] = useState(false)
  const [validationError, setValidationError] = useState<string>()

  const canAssignOther =
    organizationRole === 'Owner' || organizationRole === 'Administrator'

  function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (isSubmitting) return
    const trimmedTitle = title.trim()
    const trimmedDescription = description.trim()
    const trimmedLocation = location.trim()
    const serializedStart =
      startsAt === initialValue.startsAt && initialValue.originalStartsAt
        ? initialValue.originalStartsAt
        : dateTimeOffsetFromLocalInput(startsAt)
    const serializedEnd =
      endsAt === initialValue.endsAt && initialValue.originalEndsAt
        ? initialValue.originalEndsAt
        : dateTimeOffsetFromLocalInput(endsAt)

    if (trimmedTitle.length === 0) {
      setValidationError('Informe o título do evento.')
      return
    }
    if (trimmedTitle.length > maximumTitleLength) {
      setValidationError(
        `O título deve ter no máximo ${maximumTitleLength} caracteres.`,
      )
      return
    }
    if (trimmedDescription.length > maximumDescriptionLength) {
      setValidationError(
        `A descrição deve ter no máximo ${maximumDescriptionLength} caracteres.`,
      )
      return
    }
    if (trimmedLocation.length > maximumLocationLength) {
      setValidationError(
        `O local deve ter no máximo ${maximumLocationLength} caracteres.`,
      )
      return
    }
    if (!serializedStart || !serializedEnd) {
      setValidationError('Informe datas e horários locais válidos.')
      return
    }
    if (new Date(serializedEnd).getTime() <= new Date(serializedStart).getTime()) {
      setValidationError('O término deve ser posterior ao início.')
      return
    }
    if (association === 'client' && !client) {
      setValidationError('Selecione um cliente.')
      return
    }
    if (association === 'process' && !process) {
      setValidationError('Selecione um processo.')
      return
    }
    if (includeAssignee && assigneeMode === 'other' && !member) {
      setValidationError('Selecione uma pessoa responsável.')
      return
    }

    const fields: UpdateCalendarEventRequest = {
      title: trimmedTitle,
      description: trimmedDescription.length > 0 ? trimmedDescription : null,
      startsAt: serializedStart,
      endsAt: serializedEnd,
      location: trimmedLocation.length > 0 ? trimmedLocation : null,
      clientId: association === 'client' ? (client?.id ?? null) : null,
      processId: association === 'process' ? (process?.id ?? null) : null,
    }
    if (!includeAssignee) {
      onSubmit(fields)
      return
    }
    const assigneeMembershipId =
      assigneeMode === 'self'
        ? currentMembershipId
        : assigneeMode === 'other'
          ? (member?.id ?? null)
          : null
    onSubmit({ ...fields, assigneeMembershipId })
  }

  return (
    <form
      className="calendar-event-form"
      onSubmit={handleSubmit}
      aria-busy={isSubmitting}
    >
      <label htmlFor={`${prefix}-title`}>Título</label>
      <input
        id={`${prefix}-title`}
        value={title}
        maxLength={maximumTitleLength}
        onChange={(event) => {
          setTitle(event.target.value)
          setValidationError(undefined)
        }}
        disabled={isSubmitting}
        required
        autoFocus
      />

      <label htmlFor={`${prefix}-description`}>Descrição</label>
      <textarea
        id={`${prefix}-description`}
        value={description}
        maxLength={maximumDescriptionLength}
        onChange={(event) => {
          setDescription(event.target.value)
          setValidationError(undefined)
        }}
        disabled={isSubmitting}
      />

      <div className="calendar-event-time-fields">
        <label>
          <span>Início</span>
          <input
            aria-label="Início"
            type="datetime-local"
            value={startsAt}
            onChange={(event) => {
              setStartsAt(event.target.value)
              setValidationError(undefined)
            }}
            disabled={isSubmitting}
            required
          />
        </label>
        <label>
          <span>Término</span>
          <input
            aria-label="Término"
            type="datetime-local"
            value={endsAt}
            onChange={(event) => {
              setEndsAt(event.target.value)
              setValidationError(undefined)
            }}
            disabled={isSubmitting}
            required
          />
        </label>
      </div>

      <label htmlFor={`${prefix}-location`}>Local</label>
      <input
        id={`${prefix}-location`}
        value={location}
        maxLength={maximumLocationLength}
        onChange={(event) => {
          setLocation(event.target.value)
          setValidationError(undefined)
        }}
        disabled={isSubmitting}
      />

      <fieldset className="calendar-event-association">
        <legend>Associação</legend>
        <label>
          <input
            type="radio"
            name={`${prefix}-association`}
            value="general"
            checked={association === 'general'}
            onChange={() => {
              setAssociation('general')
              setClient(undefined)
              setProcess(undefined)
              setLookupOpen(false)
              setValidationError(undefined)
            }}
            disabled={isSubmitting}
          />
          Geral
        </label>
        <label>
          <input
            type="radio"
            name={`${prefix}-association`}
            value="client"
            checked={association === 'client'}
            onChange={() => {
              setAssociation('client')
              setClient(undefined)
              setProcess(undefined)
              setLookupOpen(true)
              setValidationError(undefined)
            }}
            disabled={isSubmitting}
          />
          Cliente
        </label>
        <label>
          <input
            type="radio"
            name={`${prefix}-association`}
            value="process"
            checked={association === 'process'}
            onChange={() => {
              setAssociation('process')
              setClient(undefined)
              setProcess(undefined)
              setLookupOpen(true)
              setValidationError(undefined)
            }}
            disabled={isSubmitting}
          />
          Processo
        </label>
      </fieldset>

      {association === 'client' ? (
        <div className="calendar-event-lookup">
          {client ? (
            <p className="calendar-event-selection">
              Cliente selecionado: <strong>{client.name}</strong>
            </p>
          ) : null}
          <button
            className="secondary-button"
            type="button"
            onClick={() => setLookupOpen((open) => !open)}
            disabled={isSubmitting}
          >
            {lookupOpen ? 'Fechar busca de cliente' : 'Selecionar cliente'}
          </button>
          {lookupOpen ? (
            <TaskLookupPicker
              organizationId={organizationId}
              searchLabel="Buscar cliente para o evento"
              resultsLabel="Clientes encontrados para o evento"
              loadingMessage="Carregando clientes..."
              emptyMessage="Não há clientes ativos disponíveis."
              noResultsMessage="Nenhum cliente encontrado para esta busca."
              errorMessage="Não foi possível carregar os clientes. Tente novamente."
              selectedId={client?.id}
              disabled={isSubmitting}
              load={lookupActiveClients}
              onUnauthorized={onUnauthorized}
              onSelect={(item) => {
                setClient(item)
                setLookupOpen(false)
                setValidationError(undefined)
              }}
              renderItem={(item) => <span>{item.name}</span>}
            />
          ) : null}
        </div>
      ) : null}

      {association === 'process' ? (
        <div className="calendar-event-lookup">
          {process ? (
            <p className="calendar-event-selection">
              Processo selecionado: <strong>{process.title}</strong>
              <span>Cliente: {process.clientName}</span>
            </p>
          ) : null}
          <button
            className="secondary-button"
            type="button"
            onClick={() => setLookupOpen((open) => !open)}
            disabled={isSubmitting}
          >
            {lookupOpen ? 'Fechar busca de processo' : 'Selecionar processo'}
          </button>
          {lookupOpen ? (
            <TaskLookupPicker
              organizationId={organizationId}
              searchLabel="Buscar processo para o evento"
              resultsLabel="Processos encontrados para o evento"
              loadingMessage="Carregando processos..."
              emptyMessage="Não há processos disponíveis."
              noResultsMessage="Nenhum processo encontrado para esta busca."
              errorMessage="Não foi possível carregar os processos. Tente novamente."
              selectedId={process?.id}
              disabled={isSubmitting}
              load={lookupLegalProcesses}
              onUnauthorized={onUnauthorized}
              onSelect={(item) => {
                setProcess(item)
                setLookupOpen(false)
                setValidationError(undefined)
              }}
              renderItem={(item) => (
                <>
                  <span>{item.title}</span>
                  <small>Cliente: {item.clientName}</small>
                </>
              )}
            />
          ) : null}
        </div>
      ) : null}

      {includeAssignee ? (
        <fieldset className="calendar-event-association">
          <legend>Responsável</legend>
          <label>
            <input
              type="radio"
              name={`${prefix}-assignee`}
              checked={assigneeMode === 'unassigned'}
              onChange={() => {
                setAssigneeMode('unassigned')
                setMember(undefined)
                setMemberLookupOpen(false)
              }}
              disabled={isSubmitting}
            />
            Não atribuído
          </label>
          <label>
            <input
              type="radio"
              name={`${prefix}-assignee`}
              checked={assigneeMode === 'self'}
              onChange={() => {
                setAssigneeMode('self')
                setMember(undefined)
                setMemberLookupOpen(false)
              }}
              disabled={isSubmitting}
            />
            Eu
          </label>
          {canAssignOther ? (
            <label>
              <input
                type="radio"
                name={`${prefix}-assignee`}
                checked={assigneeMode === 'other'}
                onChange={() => {
                  setAssigneeMode('other')
                  setMember(undefined)
                  setMemberLookupOpen(true)
                }}
                disabled={isSubmitting}
              />
              Outra pessoa
            </label>
          ) : null}
          {assigneeMode === 'other' && canAssignOther ? (
            <div className="calendar-event-lookup">
              {member ? (
                <p className="calendar-event-selection">
                  Responsável: <strong>{member.displayName}</strong>
                </p>
              ) : null}
              <button
                className="secondary-button"
                type="button"
                onClick={() => setMemberLookupOpen((open) => !open)}
                disabled={isSubmitting}
              >
                {memberLookupOpen ? 'Fechar busca' : 'Selecionar pessoa'}
              </button>
              {memberLookupOpen ? (
                <TaskLookupPicker
                  organizationId={organizationId}
                  searchLabel="Buscar responsável pelo evento"
                  resultsLabel="Responsáveis encontrados para o evento"
                  loadingMessage="Carregando responsáveis..."
                  emptyMessage="Não há responsáveis disponíveis."
                  noResultsMessage="Nenhum responsável encontrado para esta busca."
                  errorMessage="Não foi possível carregar os responsáveis. Tente novamente."
                  selectedId={member?.id}
                  disabled={isSubmitting}
                  load={lookupOrganizationMembers}
                  onUnauthorized={onUnauthorized}
                  onSelect={(item) => {
                    setMember(item)
                    setMemberLookupOpen(false)
                    setValidationError(undefined)
                  }}
                  renderItem={(item) => <span>{item.displayName}</span>}
                />
              ) : null}
            </div>
          ) : null}
        </fieldset>
      ) : null}

      {validationError ? (
        <p className="form-error" role="alert">
          {validationError}
        </p>
      ) : null}
      {serverError ? (
        <p className="form-error" role="alert">
          {serverError}
        </p>
      ) : null}
      <div className="calendar-event-form-actions">
        <button
          className="secondary-button"
          type="button"
          onClick={onCancel}
          disabled={isSubmitting}
        >
          Cancelar
        </button>
        <button className="primary-button" type="submit" disabled={isSubmitting}>
          {isSubmitting ? submittingLabel : submitLabel}
        </button>
      </div>
    </form>
  )
}
