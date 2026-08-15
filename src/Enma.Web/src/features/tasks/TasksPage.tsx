import { useEffect, useRef, useState, type FormEvent } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { useAuth } from '../authentication/AuthContext'
import { isValidDateOnly, isValidGuid } from '../deadlines/legalDeadlineFormatting'
import { LegalDeadlineRequestError } from '../deadlines/legalDeadlineService'
import { lookupLegalProcesses } from '../deadlines/legalProcessLookupService'
import {
  useCurrentOrganization,
  useOrganizationDiscovery,
} from '../organizations/OrganizationContext'
import { formatLegalTaskDueDate } from './legalTaskFormatting'
import {
  createLegalTask,
  LegalTaskRequestError,
  listLegalTasks,
} from './legalTaskService'
import type {
  LegalProcessLookupItem,
  LegalTaskAssigneeFilter,
  LegalTaskListResponse,
  LegalTaskMembershipAssigneeFilter,
  LegalTaskState,
  LegalTaskStateFilter,
  OrganizationMemberLookupItem,
} from './legalTaskTypes'
import { lookupOrganizationMembers } from './organizationMemberLookupService'
import { TaskLookupPicker } from './TaskLookupPicker'

const pageSize = 20
const maximumPageNumber = 2_147_483_647
const maximumTitleLength = 150
const maximumDescriptionLength = 2000

type ListState =
  | { readonly status: 'loading'; readonly scope: string }
  | {
      readonly status: 'success'
      readonly scope: string
      readonly response: LegalTaskListResponse
    }
  | { readonly status: 'forbidden' | 'error'; readonly scope: string }

type CreateAssigneeMode = 'unassigned' | 'self' | 'other'

function resolvePage(value: string | null): number {
  if (value === null || !/^[1-9]\d*$/.test(value)) return 1
  const page = Number(value)
  return Number.isSafeInteger(page) && page <= maximumPageNumber ? page : 1
}

function resolveState(value: string | null): LegalTaskStateFilter {
  return value === 'completed' ? 'completed' : 'pending'
}

function resolveAssignee(value: string | null): LegalTaskAssigneeFilter {
  if (value === 'self' || value === 'unassigned') return value
  return value !== null && isValidGuid(value)
    ? (value as LegalTaskMembershipAssigneeFilter)
    : 'any'
}

function isAbortError(error: unknown): boolean {
  return error instanceof DOMException && error.name === 'AbortError'
}

function isUnauthorizedError(error: unknown): boolean {
  return (
    (error instanceof LegalTaskRequestError ||
      error instanceof LegalDeadlineRequestError) &&
    error.failure === 'unauthorized'
  )
}

function getStateLabel(state: LegalTaskState): string {
  return state === 'pending' ? 'Pendente' : 'Concluída'
}

export function TasksPage() {
  const { currentOrganization } = useCurrentOrganization()
  const { refreshOrganizations } = useOrganizationDiscovery()
  const { handleUnauthorized } = useAuth()
  const [searchParams, setSearchParams] = useSearchParams()
  const state = resolveState(searchParams.get('state'))
  const processParameter = searchParams.get('processId')
  const processId =
    processParameter !== null && isValidGuid(processParameter)
      ? processParameter
      : undefined
  const assignee = resolveAssignee(searchParams.get('assignee'))
  const page = resolvePage(searchParams.get('page'))
  const [refreshVersion, setRefreshVersion] = useState(0)
  const listScope = `${currentOrganization.id}:${state}:${processId ?? ''}:${assignee}:${page}:${refreshVersion}`
  const [listState, setListState] = useState<ListState>({
    status: 'loading',
    scope: listScope,
  })
  const listRequestRef = useRef(0)

  const [selectedFilterProcess, setSelectedFilterProcess] =
    useState<LegalProcessLookupItem>()
  const [isProcessFilterOpen, setIsProcessFilterOpen] = useState(false)
  const [selectedFilterMember, setSelectedFilterMember] =
    useState<OrganizationMemberLookupItem>()
  const [isMemberFilterOpen, setIsMemberFilterOpen] = useState(false)

  const [isCreateOpen, setIsCreateOpen] = useState(false)
  const [title, setTitle] = useState('')
  const [description, setDescription] = useState('')
  const [dueDate, setDueDate] = useState('')
  const [selectedCreateProcess, setSelectedCreateProcess] =
    useState<LegalProcessLookupItem>()
  const [isCreateProcessOpen, setIsCreateProcessOpen] = useState(false)
  const [createAssigneeMode, setCreateAssigneeMode] =
    useState<CreateAssigneeMode>('unassigned')
  const [selectedCreateMember, setSelectedCreateMember] =
    useState<OrganizationMemberLookupItem>()
  const [titleError, setTitleError] = useState<string>()
  const [descriptionError, setDescriptionError] = useState<string>()
  const [dueDateError, setDueDateError] = useState<string>()
  const [assigneeError, setAssigneeError] = useState<string>()
  const [createError, setCreateError] = useState<string>()
  const [successMessage, setSuccessMessage] = useState<string>()
  const [isSubmitting, setIsSubmitting] = useState(false)
  const isSubmittingRef = useRef(false)
  const createControllerRef = useRef<AbortController | undefined>(undefined)
  const createOperationRef = useRef(0)
  const mountedRef = useRef(true)

  const canSelectOtherCreateAssignee =
    currentOrganization.role === 'Owner' ||
    currentOrganization.role === 'Administrator'

  useEffect(() => {
    const normalized = new URLSearchParams(searchParams)
    let changed = false
    const stateParameter = searchParams.get('state')
    if (stateParameter !== null && stateParameter !== 'completed') {
      normalized.delete('state')
      changed = true
    }
    if (processParameter !== null && !processId) {
      normalized.delete('processId')
      changed = true
    }
    const assigneeParameter = searchParams.get('assignee')
    if (
      assigneeParameter !== null &&
      assigneeParameter !== 'self' &&
      assigneeParameter !== 'unassigned' &&
      !isValidGuid(assigneeParameter)
    ) {
      normalized.delete('assignee')
      changed = true
    }
    const pageParameter = searchParams.get('page')
    if (pageParameter !== null && (page === 1 || pageParameter !== page.toString())) {
      normalized.delete('page')
      changed = true
    }
    if (changed) setSearchParams(normalized, { replace: true })
  }, [page, processId, processParameter, searchParams, setSearchParams])

  useEffect(() => {
    const controller = new AbortController()
    const requestId = ++listRequestRef.current

    void listLegalTasks(
      currentOrganization.id,
      { state, processId, assignee, pageNumber: page, pageSize },
      handleUnauthorized,
      controller.signal,
    )
      .then((response) => {
        if (!controller.signal.aborted && requestId === listRequestRef.current) {
          setListState({ status: 'success', scope: listScope, response })
        }
      })
      .catch((error: unknown) => {
        if (
          controller.signal.aborted ||
          requestId !== listRequestRef.current ||
          isAbortError(error) ||
          isUnauthorizedError(error)
        ) {
          return
        }
        setListState({
          status:
            error instanceof LegalTaskRequestError &&
            error.failure === 'forbidden'
              ? 'forbidden'
              : 'error',
          scope: listScope,
        })
      })

    return () => controller.abort()
  }, [assignee, currentOrganization.id, handleUnauthorized, listScope, page, processId, refreshVersion, state])

  useEffect(() => {
    mountedRef.current = true
    return () => {
      mountedRef.current = false
      listRequestRef.current += 1
      createOperationRef.current += 1
      createControllerRef.current?.abort()
    }
  }, [])

  const currentListState: ListState =
    listState.scope === listScope
      ? listState
      : { status: 'loading', scope: listScope }

  function updateFilters(changes: Record<string, string | undefined>) {
    const next = new URLSearchParams(searchParams)
    for (const [key, value] of Object.entries(changes)) {
      if (value === undefined) next.delete(key)
      else next.set(key, value)
    }
    next.delete('page')
    setSearchParams(next)
  }

  function navigateToPage(nextPage: number) {
    const next = new URLSearchParams(searchParams)
    if (nextPage === 1) next.delete('page')
    else next.set('page', nextPage.toString())
    setSearchParams(next)
  }

  function resetCreate() {
    setIsCreateOpen(false)
    setTitle('')
    setDescription('')
    setDueDate('')
    setSelectedCreateProcess(undefined)
    setIsCreateProcessOpen(false)
    setCreateAssigneeMode('unassigned')
    setSelectedCreateMember(undefined)
    setTitleError(undefined)
    setDescriptionError(undefined)
    setDueDateError(undefined)
    setAssigneeError(undefined)
    setCreateError(undefined)
  }

  function openCreate() {
    resetCreate()
    setSuccessMessage(undefined)
    setIsCreateOpen(true)
  }

  async function handleCreate(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (isSubmittingRef.current) return

    const trimmedTitle = title.trim()
    const trimmedDescription = description.trim()
    let valid = true
    if (trimmedTitle.length === 0) {
      setTitleError('Informe o título da tarefa.')
      valid = false
    } else if (trimmedTitle.length > maximumTitleLength) {
      setTitleError(`O título deve ter no máximo ${maximumTitleLength} caracteres.`)
      valid = false
    }
    if (trimmedDescription.length > maximumDescriptionLength) {
      setDescriptionError(
        `A descrição deve ter no máximo ${maximumDescriptionLength} caracteres.`,
      )
      valid = false
    }
    if (dueDate.length > 0 && !isValidDateOnly(dueDate)) {
      setDueDateError('Informe uma data válida.')
      valid = false
    }
    if (createAssigneeMode === 'other' && !selectedCreateMember) {
      setAssigneeError('Selecione uma pessoa responsável.')
      valid = false
    }
    if (!valid) return

    const organizationId = currentOrganization.id
    const operationId = ++createOperationRef.current
    const controller = new AbortController()
    createControllerRef.current = controller
    isSubmittingRef.current = true
    setIsSubmitting(true)
    setTitleError(undefined)
    setDescriptionError(undefined)
    setDueDateError(undefined)
    setAssigneeError(undefined)
    setCreateError(undefined)

    const isCurrent = () =>
      mountedRef.current &&
      !controller.signal.aborted &&
      operationId === createOperationRef.current

    const assigneeMembershipId =
      createAssigneeMode === 'self'
        ? currentOrganization.membershipId
        : createAssigneeMode === 'other'
          ? (selectedCreateMember?.id ?? null)
          : null

    try {
      await createLegalTask(
        organizationId,
        {
          title: trimmedTitle,
          description: trimmedDescription.length > 0 ? trimmedDescription : null,
          dueDate: dueDate.length > 0 ? dueDate : null,
          processId: selectedCreateProcess?.id ?? null,
          assigneeMembershipId,
        },
        handleUnauthorized,
        controller.signal,
      )
      if (!isCurrent()) return

      isSubmittingRef.current = false
      setIsSubmitting(false)
      resetCreate()
      setSuccessMessage('Tarefa cadastrada com sucesso.')
      if (page === 1) {
        setRefreshVersion((version) => version + 1)
      } else {
        const next = new URLSearchParams(searchParams)
        next.delete('page')
        setSearchParams(next)
      }
    } catch (error) {
      if (!isCurrent() || isAbortError(error) || isUnauthorizedError(error)) return

      if (error instanceof LegalTaskRequestError) {
        if (error.failure === 'forbidden') {
          setCreateError(
            'Você não tem permissão para cadastrar tarefas nesta organização.',
          )
        } else if (error.failure === 'not-found') {
          setSelectedCreateProcess(undefined)
          setCreateError('O processo selecionado não está mais disponível.')
        } else if (error.failure === 'related-assignee-unavailable') {
          setSelectedCreateMember(undefined)
          setCreateAssigneeMode('unassigned')
          setCreateError('O responsável selecionado não está mais disponível.')
        } else if (error.failure === 'bad-request') {
          setCreateError('Verifique os dados da tarefa e tente novamente.')
        } else {
          setCreateError('Não foi possível cadastrar a tarefa. Tente novamente.')
        }
      } else {
        setCreateError('Não foi possível cadastrar a tarefa. Tente novamente.')
      }
    } finally {
      if (isCurrent()) {
        createControllerRef.current = undefined
        isSubmittingRef.current = false
        setIsSubmitting(false)
      }
    }
  }

  const hasSpecificAssignee =
    assignee !== 'any' && assignee !== 'self' && assignee !== 'unassigned'
  const currentFilterProcess =
    selectedFilterProcess?.id === processId ? selectedFilterProcess : undefined
  const currentFilterMember =
    selectedFilterMember?.id === assignee ? selectedFilterMember : undefined
  const isFiltered = Boolean(processId) || assignee !== 'any'

  return (
    <section className="tasks-page" aria-labelledby="tasks-title">
      <div className="tasks-header">
        <div>
          <p className="eyebrow">Gestão de tarefas</p>
          <h2 id="tasks-title">Tarefas</h2>
          <p className="tasks-description">
            Acompanhe as tarefas desta organização.
          </p>
        </div>
        {!isCreateOpen ? (
          <button className="primary-button" type="button" onClick={openCreate}>
            Nova tarefa
          </button>
        ) : null}
      </div>

      {successMessage ? (
        <p className="success-message" role="status">{successMessage}</p>
      ) : null}

      {isCreateOpen ? (
        <div className="task-create-panel">
          <h3>Nova tarefa</h3>
          <form className="task-create-form" onSubmit={handleCreate}>
            <label htmlFor="task-title">Título</label>
            <input
              id="task-title"
              value={title}
              onChange={(event) => {
                setTitle(event.target.value)
                setTitleError(undefined)
              }}
              maxLength={maximumTitleLength}
              required
              disabled={isSubmitting}
              aria-invalid={titleError ? true : undefined}
            />
            {titleError ? <p className="form-error" role="alert">{titleError}</p> : null}

            <label htmlFor="task-description">Descrição</label>
            <textarea
              id="task-description"
              value={description}
              onChange={(event) => {
                setDescription(event.target.value)
                setDescriptionError(undefined)
              }}
              maxLength={maximumDescriptionLength}
              disabled={isSubmitting}
              aria-invalid={descriptionError ? true : undefined}
            />
            {descriptionError ? (
              <p className="form-error" role="alert">{descriptionError}</p>
            ) : null}

            <label htmlFor="task-due-date">Prazo</label>
            <input
              id="task-due-date"
              type="date"
              value={dueDate}
              onChange={(event) => {
                setDueDate(event.target.value)
                setDueDateError(undefined)
              }}
              disabled={isSubmitting}
              aria-invalid={dueDateError ? true : undefined}
            />
            {dueDateError ? <p className="form-error" role="alert">{dueDateError}</p> : null}

            <fieldset className="task-relation-fieldset">
              <legend>Processo</legend>
              {selectedCreateProcess ? (
                <div className="task-selected-relation">
                  <p>
                    <strong>{selectedCreateProcess.title}</strong>
                    <span>Cliente: {selectedCreateProcess.clientName}</span>
                  </p>
                  <button
                    className="text-button"
                    type="button"
                    onClick={() => setSelectedCreateProcess(undefined)}
                    disabled={isSubmitting}
                  >
                    Sem processo
                  </button>
                </div>
              ) : (
                <p>Sem processo</p>
              )}
              <button
                className="secondary-button"
                type="button"
                onClick={() => setIsCreateProcessOpen((open) => !open)}
                disabled={isSubmitting}
              >
                {isCreateProcessOpen ? 'Fechar busca de processo' : 'Selecionar processo'}
              </button>
              {isCreateProcessOpen ? (
                <TaskLookupPicker
                  organizationId={currentOrganization.id}
                  searchLabel="Buscar processo para tarefa"
                  resultsLabel="Processos encontrados para a tarefa"
                  loadingMessage="Carregando processos..."
                  emptyMessage="Não há processos disponíveis."
                  noResultsMessage="Nenhum processo encontrado para esta busca."
                  errorMessage="Não foi possível carregar os processos. Tente novamente."
                  selectedId={selectedCreateProcess?.id}
                  disabled={isSubmitting}
                  load={lookupLegalProcesses}
                  onUnauthorized={handleUnauthorized}
                  onSelect={(item) => {
                    setSelectedCreateProcess(item)
                    setIsCreateProcessOpen(false)
                    setCreateError(undefined)
                  }}
                  renderItem={(item) => (
                    <><span>{item.title}</span><small>Cliente: {item.clientName}</small></>
                  )}
                />
              ) : null}
            </fieldset>

            <label htmlFor="task-assignee">Responsável</label>
            <select
              id="task-assignee"
              aria-label="Responsável da tarefa"
              value={createAssigneeMode}
              onChange={(event) => {
                const mode = event.target.value as CreateAssigneeMode
                setCreateAssigneeMode(mode)
                setSelectedCreateMember(undefined)
                setAssigneeError(undefined)
              }}
              disabled={isSubmitting}
            >
              <option value="unassigned">Não atribuída</option>
              <option value="self">Eu</option>
              {canSelectOtherCreateAssignee ? (
                <option value="other">Outra pessoa</option>
              ) : null}
            </select>
            {createAssigneeMode === 'other' && canSelectOtherCreateAssignee ? (
              <TaskLookupPicker
                organizationId={currentOrganization.id}
                searchLabel="Buscar responsável para tarefa"
                resultsLabel="Responsáveis encontrados para a tarefa"
                loadingMessage="Carregando responsáveis..."
                emptyMessage="Não há responsáveis disponíveis."
                noResultsMessage="Nenhum responsável encontrado para esta busca."
                errorMessage="Não foi possível carregar os responsáveis. Tente novamente."
                selectedId={selectedCreateMember?.id}
                disabled={isSubmitting}
                load={lookupOrganizationMembers}
                onUnauthorized={handleUnauthorized}
                onSelect={(item) => {
                  setSelectedCreateMember(item)
                  setAssigneeError(undefined)
                  setCreateError(undefined)
                }}
                renderItem={(item) => <span>{item.displayName}</span>}
              />
            ) : null}
            {selectedCreateMember && createAssigneeMode === 'other' ? (
              <p className="task-selected-member" role="status">
                Responsável selecionado: <strong>{selectedCreateMember.displayName}</strong>
              </p>
            ) : null}
            {assigneeError ? <p className="form-error" role="alert">{assigneeError}</p> : null}

            {createError ? (
              <div className="task-create-error">
                <p className="form-error" role="alert">{createError}</p>
                {createError.includes('permissão') ? (
                  <button
                    className="text-button"
                    type="button"
                    onClick={refreshOrganizations}
                    disabled={isSubmitting}
                  >
                    Atualizar acesso
                  </button>
                ) : null}
              </div>
            ) : null}

            <div className="task-form-actions">
              <button
                className="secondary-button"
                type="button"
                onClick={resetCreate}
                disabled={isSubmitting}
              >
                Cancelar
              </button>
              <button className="primary-button" type="submit" disabled={isSubmitting}>
                {isSubmitting ? 'Cadastrando...' : 'Cadastrar tarefa'}
              </button>
            </div>
          </form>
        </div>
      ) : null}

      <div className="task-filters" aria-label="Filtros de tarefas">
        <fieldset className="task-state-filter">
          <legend>Estado</legend>
          <button
            type="button"
            className="secondary-button"
            aria-pressed={state === 'pending'}
            onClick={() => updateFilters({ state: undefined })}
          >
            Pendentes
          </button>
          <button
            type="button"
            className="secondary-button"
            aria-pressed={state === 'completed'}
            onClick={() => updateFilters({ state: 'completed' })}
          >
            Concluídas
          </button>
        </fieldset>

        <div className="task-filter-control">
          <span>Processo</span>
          {processId ? (
            <div className="task-active-filter">
              <span>
                {currentFilterProcess
                  ? `${currentFilterProcess.title} — ${currentFilterProcess.clientName}`
                  : 'Processo selecionado'}
              </span>
              <button
                className="text-button"
                type="button"
                onClick={() => {
                  setSelectedFilterProcess(undefined)
                  setIsProcessFilterOpen(false)
                  updateFilters({ processId: undefined })
                }}
              >
                Limpar processo
              </button>
            </div>
          ) : (
            <span>Todos os processos</span>
          )}
          <button
            className="secondary-button"
            type="button"
            onClick={() => setIsProcessFilterOpen((open) => !open)}
          >
            {isProcessFilterOpen ? 'Fechar busca' : 'Escolher processo'}
          </button>
          {isProcessFilterOpen ? (
            <TaskLookupPicker
              organizationId={currentOrganization.id}
              searchLabel="Buscar processo para filtro"
              resultsLabel="Processos encontrados para o filtro"
              loadingMessage="Carregando processos..."
              emptyMessage="Não há processos disponíveis."
              noResultsMessage="Nenhum processo encontrado para esta busca."
              errorMessage="Não foi possível carregar os processos. Tente novamente."
              selectedId={processId}
              load={lookupLegalProcesses}
              onUnauthorized={handleUnauthorized}
              onSelect={(item) => {
                setSelectedFilterProcess(item)
                setIsProcessFilterOpen(false)
                updateFilters({ processId: item.id })
              }}
              renderItem={(item) => (
                <><span>{item.title}</span><small>Cliente: {item.clientName}</small></>
              )}
            />
          ) : null}
        </div>

        <div className="task-filter-control">
          <label htmlFor="task-assignee-filter">Responsável</label>
          <select
            id="task-assignee-filter"
            value={isMemberFilterOpen ? 'specific' : hasSpecificAssignee ? 'specific' : assignee}
            onChange={(event) => {
              const value = event.target.value
              setSelectedFilterMember(undefined)
              if (value === 'specific') {
                setIsMemberFilterOpen(true)
              } else {
                setIsMemberFilterOpen(false)
                updateFilters({ assignee: value === 'any' ? undefined : value })
              }
            }}
          >
            <option value="any">Todos</option>
            <option value="self">Minhas</option>
            <option value="unassigned">Não atribuídas</option>
            <option value="specific">Pessoa específica</option>
          </select>
          {hasSpecificAssignee ? (
            <div className="task-active-filter">
              <span>{currentFilterMember?.displayName ?? 'Pessoa selecionada'}</span>
              <button
                className="text-button"
                type="button"
                onClick={() => {
                  setSelectedFilterMember(undefined)
                  setIsMemberFilterOpen(false)
                  updateFilters({ assignee: undefined })
                }}
              >
                Limpar responsável
              </button>
            </div>
          ) : null}
          {hasSpecificAssignee && !isMemberFilterOpen ? (
            <button
              className="secondary-button"
              type="button"
              onClick={() => setIsMemberFilterOpen(true)}
            >
              Alterar pessoa
            </button>
          ) : null}
          {isMemberFilterOpen ? (
            <TaskLookupPicker
              organizationId={currentOrganization.id}
              searchLabel="Buscar pessoa para filtro"
              resultsLabel="Pessoas encontradas para o filtro"
              loadingMessage="Carregando pessoas..."
              emptyMessage="Não há pessoas disponíveis."
              noResultsMessage="Nenhuma pessoa encontrada para esta busca."
              errorMessage="Não foi possível carregar as pessoas. Tente novamente."
              selectedId={hasSpecificAssignee ? assignee : undefined}
              load={lookupOrganizationMembers}
              onUnauthorized={handleUnauthorized}
              onSelect={(item) => {
                setSelectedFilterMember(item)
                setIsMemberFilterOpen(false)
                updateFilters({ assignee: item.id })
              }}
              renderItem={(item) => <span>{item.displayName}</span>}
            />
          ) : null}
        </div>
      </div>

      {currentListState.status === 'loading' ? (
        <p className="tasks-state" role="status">Carregando tarefas...</p>
      ) : null}
      {currentListState.status === 'forbidden' ? (
        <div className="tasks-state" role="alert">
          <h3>Acesso indisponível</h3>
          <p>Não foi possível acessar as tarefas desta organização.</p>
          <div className="tasks-state-actions">
            <button className="secondary-button" type="button" onClick={refreshOrganizations}>
              Atualizar acesso
            </button>
            <Link className="home-link" to="/organizations">Voltar para organizações</Link>
          </div>
        </div>
      ) : null}
      {currentListState.status === 'error' ? (
        <div className="tasks-state" role="alert">
          <p>Não foi possível carregar as tarefas. Tente novamente.</p>
          <button
            className="secondary-button"
            type="button"
            onClick={() => setRefreshVersion((version) => version + 1)}
          >
            Tentar novamente
          </button>
        </div>
      ) : null}

      {currentListState.status === 'success' ? (
        <>
          {currentListState.response.items.length === 0 ? (
            <div className="tasks-state" role="status">
              <p>
                {page > 1 || isFiltered
                  ? 'Nenhuma tarefa corresponde aos filtros atuais.'
                  : state === 'pending'
                    ? 'Não há tarefas pendentes.'
                    : 'Não há tarefas concluídas.'}
              </p>
            </div>
          ) : (
            <div className="tasks-table-wrapper">
              <table className="tasks-table">
                <caption className="visually-hidden">
                  Tarefas da organização {currentOrganization.name}
                </caption>
                <thead>
                  <tr>
                    <th scope="col">Título</th>
                    <th scope="col">Prazo</th>
                    <th scope="col">Processo</th>
                    <th scope="col">Responsável</th>
                    <th scope="col">Estado</th>
                  </tr>
                </thead>
                <tbody>
                  {currentListState.response.items.map((task) => (
                    <tr key={task.id}>
                      <td data-label="Título">
                        <Link
                          className="task-detail-link"
                          to={`/organizations/${currentOrganization.id}/tasks/${task.id}`}
                        >
                          {task.title}
                        </Link>
                      </td>
                      <td data-label="Prazo">{formatLegalTaskDueDate(task.dueDate)}</td>
                      <td data-label="Processo">
                        {task.processTitle ?? 'Tarefa geral'}
                        {task.clientName ? <small>Cliente: {task.clientName}</small> : null}
                      </td>
                      <td data-label="Responsável">
                        {task.assigneeDisplayName ?? 'Não atribuída'}
                      </td>
                      <td data-label="Estado">
                        <span className={`task-status is-${task.state}`}>
                          {getStateLabel(task.state)}
                        </span>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}

          <nav className="tasks-pagination" aria-label="Paginação de tarefas">
            <button
              className="secondary-button"
              type="button"
              onClick={() => navigateToPage(page - 1)}
              disabled={page === 1}
              aria-label="Página anterior de tarefas"
            >
              Anterior
            </button>
            <span aria-current="page">Página {page}</span>
            <button
              className="secondary-button"
              type="button"
              onClick={() => navigateToPage(page + 1)}
              disabled={page === maximumPageNumber || !currentListState.response.hasNext}
              aria-label="Próxima página de tarefas"
            >
              Próxima
            </button>
          </nav>
        </>
      ) : null}
    </section>
  )
}
