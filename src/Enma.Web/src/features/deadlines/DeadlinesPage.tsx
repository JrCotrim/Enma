import { useEffect, useRef, useState, type FormEvent } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { useAuth } from '../authentication/AuthContext'
import {
  useCurrentOrganization,
  useOrganizationDiscovery,
} from '../organizations/OrganizationContext'
import {
  formatLegalDeadlineDueDate,
  isValidDateOnly,
} from './legalDeadlineFormatting'
import {
  createLegalDeadline,
  LegalDeadlineRequestError,
  listLegalDeadlines,
} from './legalDeadlineService'
import type {
  LegalDeadlineListResponse,
  LegalProcessLookupItem,
} from './legalDeadlineTypes'
import { lookupLegalProcesses } from './legalProcessLookupService'

const pageSize = 20
const maximumPageNumber = 2_147_483_647
const maximumTitleLength = 150
const genericListError =
  'Não foi possível carregar os prazos. Tente novamente.'
const genericLookupError =
  'Não foi possível carregar os processos. Tente novamente.'
const genericCreateError =
  'Não foi possível cadastrar o prazo. Tente novamente.'
const createPermissionError =
  'Você não tem permissão para cadastrar prazos nesta organização.'
const selectedProcessUnavailableError =
  'O processo selecionado não está disponível para este cadastro.'
const createValidationError =
  'Não foi possível validar o cadastro. Verifique os dados e tente novamente.'

type ListState =
  | { readonly status: 'loading'; readonly scope: string }
  | {
      readonly status: 'success'
      readonly scope: string
      readonly response: LegalDeadlineListResponse
    }
  | { readonly status: 'forbidden'; readonly scope: string }
  | { readonly status: 'error'; readonly scope: string }

interface FormContext {
  readonly organizationId: string
  readonly sessionId: number
}

interface LookupRequest {
  readonly id: number
  readonly context: FormContext
  readonly organizationId: string
  readonly search: string
  readonly pageNumber: number
  readonly append: boolean
  readonly baseItems: readonly LegalProcessLookupItem[]
}

type LookupState =
  | { readonly status: 'idle' }
  | {
      readonly status: 'loading'
      readonly context: FormContext
      readonly search: string
      readonly pageNumber: number
      readonly append: boolean
      readonly items: readonly LegalProcessLookupItem[]
    }
  | {
      readonly status: 'success'
      readonly context: FormContext
      readonly search: string
      readonly pageNumber: number
      readonly items: readonly LegalProcessLookupItem[]
      readonly hasNext: boolean
    }
  | {
      readonly status: 'forbidden' | 'error'
      readonly context: FormContext
      readonly search: string
      readonly pageNumber: number
      readonly append: boolean
      readonly items: readonly LegalProcessLookupItem[]
    }

function resolvePage(value: string | null): number {
  if (value === null || !/^[1-9]\d*$/.test(value)) {
    return 1
  }

  const page = Number(value)
  return Number.isSafeInteger(page) && page <= maximumPageNumber ? page : 1
}

function isCanonicalPage(value: string | null, page: number): boolean {
  return value === null || value === page.toString()
}

function isAbortError(error: unknown): boolean {
  return error instanceof DOMException && error.name === 'AbortError'
}

function isSameFormContext(
  left: FormContext | undefined,
  right: FormContext,
): boolean {
  return (
    left?.organizationId === right.organizationId &&
    left.sessionId === right.sessionId
  )
}

function deduplicateProcesses(
  existing: readonly LegalProcessLookupItem[],
  additional: readonly LegalProcessLookupItem[],
): readonly LegalProcessLookupItem[] {
  const processes = new Map(
    existing.map((legalProcess) => [legalProcess.id.toLowerCase(), legalProcess]),
  )

  for (const legalProcess of additional) {
    processes.set(legalProcess.id.toLowerCase(), legalProcess)
  }

  return [...processes.values()]
}

function getDeadlineStateLabel(state: 'Pending' | 'Completed'): string {
  return state === 'Pending' ? 'Pendente' : 'Concluído'
}

export function DeadlinesPage() {
  const { currentOrganization } = useCurrentOrganization()
  const { refreshOrganizations } = useOrganizationDiscovery()
  const { handleUnauthorized } = useAuth()
  const [searchParams, setSearchParams] = useSearchParams()
  const pageParameter = searchParams.get('page')
  const page = resolvePage(pageParameter)
  const [refreshVersion, setRefreshVersion] = useState(0)
  const listScope = `${currentOrganization.id}:${page}:${refreshVersion}`
  const [listState, setListState] = useState<ListState>({
    status: 'loading',
    scope: listScope,
  })
  const listRequestVersionRef = useRef(0)
  const currentOrganizationIdRef = useRef(currentOrganization.id)
  const mountedRef = useRef(true)

  const [isCreateOpen, setIsCreateOpen] = useState(false)
  const [formContext, setFormContext] = useState<FormContext>()
  const formContextRef = useRef<FormContext | undefined>(undefined)
  const formSessionRef = useRef(0)
  const [title, setTitle] = useState('')
  const [titleError, setTitleError] = useState<string>()
  const [dueDate, setDueDate] = useState('')
  const [dueDateError, setDueDateError] = useState<string>()
  const [selectedProcess, setSelectedProcess] =
    useState<LegalProcessLookupItem>()
  const [processError, setProcessError] = useState<string>()
  const [createError, setCreateError] = useState<string>()
  const [successMessage, setSuccessMessage] = useState<{
    readonly organizationId: string
    readonly message: string
  }>()
  const [isSubmitting, setIsSubmitting] = useState(false)
  const isSubmittingRef = useRef(false)
  const createControllerRef = useRef<AbortController | undefined>(undefined)
  const createOperationRef = useRef(0)

  const [searchInput, setSearchInput] = useState('')
  const [lookupState, setLookupState] = useState<LookupState>({
    status: 'idle',
  })
  const [lookupRequest, setLookupRequest] = useState<LookupRequest>()
  const lookupRequestIdRef = useRef(0)
  const canCreate =
    currentOrganization.role === 'Owner' ||
    currentOrganization.role === 'Administrator'

  useEffect(() => {
    currentOrganizationIdRef.current = currentOrganization.id
  }, [currentOrganization.id])

  useEffect(() => {
    if (!isCanonicalPage(pageParameter, page)) {
      const normalized = new URLSearchParams(searchParams)
      normalized.delete('page')
      setSearchParams(normalized, { replace: true })
    }
  }, [page, pageParameter, searchParams, setSearchParams])

  useEffect(() => {
    const controller = new AbortController()
    const requestVersion = ++listRequestVersionRef.current

    void listLegalDeadlines(
      currentOrganization.id,
      page,
      pageSize,
      handleUnauthorized,
      controller.signal,
    )
      .then((response) => {
        if (
          !controller.signal.aborted &&
          requestVersion === listRequestVersionRef.current &&
          currentOrganizationIdRef.current === currentOrganization.id
        ) {
          setListState({ status: 'success', scope: listScope, response })
        }
      })
      .catch((error: unknown) => {
        if (
          controller.signal.aborted ||
          requestVersion !== listRequestVersionRef.current ||
          currentOrganizationIdRef.current !== currentOrganization.id ||
          isAbortError(error) ||
          (error instanceof LegalDeadlineRequestError &&
            error.failure === 'unauthorized')
        ) {
          return
        }

        setListState({
          status:
            error instanceof LegalDeadlineRequestError &&
            error.failure === 'forbidden'
              ? 'forbidden'
              : 'error',
          scope: listScope,
        })
      })

    return () => {
      controller.abort()
    }
  }, [
    currentOrganization.id,
    handleUnauthorized,
    listScope,
    page,
    refreshVersion,
  ])

  useEffect(() => {
    if (!lookupRequest) {
      return
    }

    const controller = new AbortController()
    let active = true

    void lookupLegalProcesses(
      lookupRequest.organizationId,
      lookupRequest.search,
      lookupRequest.pageNumber,
      pageSize,
      handleUnauthorized,
      controller.signal,
    )
      .then((response) => {
        if (
          !active ||
          controller.signal.aborted ||
          lookupRequest.id !== lookupRequestIdRef.current ||
          !isSameFormContext(formContextRef.current, lookupRequest.context) ||
          currentOrganizationIdRef.current !== lookupRequest.organizationId
        ) {
          return
        }

        const items = lookupRequest.append
          ? deduplicateProcesses(lookupRequest.baseItems, response.items)
          : response.items

        setLookupState({
          status: 'success',
          context: lookupRequest.context,
          search: lookupRequest.search,
          pageNumber: response.pageNumber,
          items,
          hasNext: response.hasNext,
        })
      })
      .catch((error: unknown) => {
        if (
          !active ||
          controller.signal.aborted ||
          lookupRequest.id !== lookupRequestIdRef.current ||
          !isSameFormContext(formContextRef.current, lookupRequest.context) ||
          currentOrganizationIdRef.current !== lookupRequest.organizationId ||
          isAbortError(error) ||
          (error instanceof LegalDeadlineRequestError &&
            error.failure === 'unauthorized')
        ) {
          return
        }

        setLookupState({
          status:
            error instanceof LegalDeadlineRequestError &&
            error.failure === 'forbidden'
              ? 'forbidden'
              : 'error',
          context: lookupRequest.context,
          search: lookupRequest.search,
          pageNumber: lookupRequest.pageNumber,
          append: lookupRequest.append,
          items: lookupRequest.baseItems,
        })
      })

    return () => {
      active = false
      controller.abort()
    }
  }, [handleUnauthorized, lookupRequest])

  useEffect(
    () => {
      mountedRef.current = true

      return () => {
        mountedRef.current = false
        lookupRequestIdRef.current += 1
        createOperationRef.current += 1
        createControllerRef.current?.abort()
      }
    },
    [],
  )

  const currentListState: ListState =
    listState.scope === listScope
      ? listState
      : { status: 'loading', scope: listScope }
  const currentLookupState =
    formContext &&
    lookupState.status !== 'idle' &&
    isSameFormContext(lookupState.context, formContext)
      ? lookupState
      : undefined

  function navigateToPage(nextPage: number) {
    const nextSearchParams = new URLSearchParams(searchParams)

    if (nextPage === 1) {
      nextSearchParams.delete('page')
    } else {
      nextSearchParams.set('page', nextPage.toString())
    }

    setSearchParams(nextSearchParams)
  }

  function startLookup(
    context: FormContext,
    search: string,
    pageNumber: number,
    append: boolean,
    baseItems: readonly LegalProcessLookupItem[],
  ) {
    const id = ++lookupRequestIdRef.current
    setLookupState({
      status: 'loading',
      context,
      search,
      pageNumber,
      append,
      items: baseItems,
    })
    setLookupRequest({
      id,
      context,
      organizationId: context.organizationId,
      search,
      pageNumber,
      append,
      baseItems,
    })
  }

  function openCreate() {
    const context = {
      organizationId: currentOrganization.id,
      sessionId: ++formSessionRef.current,
    }
    formContextRef.current = context
    setFormContext(context)
    setIsCreateOpen(true)
    setTitle('')
    setTitleError(undefined)
    setDueDate('')
    setDueDateError(undefined)
    setSelectedProcess(undefined)
    setProcessError(undefined)
    setCreateError(undefined)
    setSearchInput('')
    startLookup(context, '', 1, false, [])
  }

  function resetCreateState() {
    lookupRequestIdRef.current += 1
    setLookupRequest(undefined)
    formContextRef.current = undefined
    setFormContext(undefined)
    setIsCreateOpen(false)
    setTitle('')
    setTitleError(undefined)
    setDueDate('')
    setDueDateError(undefined)
    setSelectedProcess(undefined)
    setProcessError(undefined)
    setCreateError(undefined)
    setSearchInput('')
    setLookupState({ status: 'idle' })
  }

  function closeCreate() {
    if (!isSubmittingRef.current) {
      resetCreateState()
    }
  }

  function handleProcessSearch(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()

    const context = formContextRef.current
    if (!context || context.organizationId !== currentOrganization.id) {
      return
    }

    startLookup(context, searchInput.trim(), 1, false, [])
  }

  function loadMoreProcesses() {
    const context = formContextRef.current
    if (
      !context ||
      !currentLookupState ||
      currentLookupState.status !== 'success' ||
      !currentLookupState.hasNext
    ) {
      return
    }

    startLookup(
      context,
      currentLookupState.search,
      currentLookupState.pageNumber + 1,
      true,
      currentLookupState.items,
    )
  }

  function retryLookup() {
    const context = formContextRef.current
    if (
      !context ||
      !currentLookupState ||
      (currentLookupState.status !== 'error' &&
        currentLookupState.status !== 'forbidden')
    ) {
      return
    }

    startLookup(
      context,
      currentLookupState.search,
      currentLookupState.pageNumber,
      currentLookupState.append,
      currentLookupState.items,
    )
  }

  async function handleCreate(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()

    if (isSubmittingRef.current) {
      return
    }

    const context = formContextRef.current
    const trimmedTitle = title.trim()
    let isValid = true

    if (trimmedTitle.length === 0) {
      setTitleError('Informe o título do prazo.')
      isValid = false
    } else if (trimmedTitle.length > maximumTitleLength) {
      setTitleError(
        `O título deve ter no máximo ${maximumTitleLength} caracteres.`,
      )
      isValid = false
    }

    if (!selectedProcess) {
      setProcessError('Selecione um processo.')
      isValid = false
    }

    if (!isValidDateOnly(dueDate)) {
      setDueDateError('Informe uma data do prazo válida.')
      isValid = false
    }

    if (
      !isValid ||
      !selectedProcess ||
      !context ||
      context.organizationId !== currentOrganization.id
    ) {
      return
    }

    const operationId = ++createOperationRef.current
    const selectedProcessId = selectedProcess.id
    const literalDueDate = dueDate
    const controller = new AbortController()
    createControllerRef.current = controller
    isSubmittingRef.current = true
    setIsSubmitting(true)
    setTitleError(undefined)
    setProcessError(undefined)
    setDueDateError(undefined)
    setCreateError(undefined)
    setSuccessMessage(undefined)

    const isCurrentOperation = () =>
      mountedRef.current &&
      !controller.signal.aborted &&
      operationId === createOperationRef.current &&
      isSameFormContext(formContextRef.current, context) &&
      currentOrganizationIdRef.current === context.organizationId

    try {
      await createLegalDeadline(
        context.organizationId,
        selectedProcessId,
        trimmedTitle,
        literalDueDate,
        handleUnauthorized,
        controller.signal,
      )

      if (!isCurrentOperation()) {
        return
      }

      createControllerRef.current = undefined
      isSubmittingRef.current = false
      setIsSubmitting(false)
      resetCreateState()
      setSuccessMessage({
        organizationId: context.organizationId,
        message: 'Prazo cadastrado com sucesso.',
      })
      setRefreshVersion((version) => version + 1)
    } catch (error) {
      if (
        !isCurrentOperation() ||
        isAbortError(error) ||
        (error instanceof LegalDeadlineRequestError &&
          error.failure === 'unauthorized')
      ) {
        return
      }

      if (
        error instanceof LegalDeadlineRequestError &&
        error.failure === 'forbidden'
      ) {
        setCreateError(createPermissionError)
      } else if (
        error instanceof LegalDeadlineRequestError &&
        error.failure === 'not-found'
      ) {
        setSelectedProcess(undefined)
        setCreateError(selectedProcessUnavailableError)
      } else if (
        error instanceof LegalDeadlineRequestError &&
        error.failure === 'bad-request'
      ) {
        setCreateError(createValidationError)
      } else {
        setCreateError(genericCreateError)
      }
    } finally {
      if (isCurrentOperation()) {
        createControllerRef.current = undefined
        isSubmittingRef.current = false
        setIsSubmitting(false)
      }
    }
  }

  const lookupItems =
    currentLookupState && currentLookupState.status !== 'forbidden'
      ? currentLookupState.items
      : []

  return (
    <section className="deadlines-page" aria-labelledby="deadlines-title">
      <div className="deadlines-header">
        <div>
          <p className="eyebrow">Gestão de prazos</p>
          <h2 id="deadlines-title">Prazos</h2>
          <p className="deadlines-description">
            Consulte os prazos vinculados a esta organização.
          </p>
        </div>
        {canCreate &&
        (!isCreateOpen ||
          formContext?.organizationId !== currentOrganization.id) ? (
          <button className="primary-button" type="button" onClick={openCreate}>
            Cadastrar prazo
          </button>
        ) : null}
      </div>

      {successMessage?.organizationId === currentOrganization.id ? (
        <p className="success-message" role="status">
          {successMessage.message}
        </p>
      ) : null}

      {isCreateOpen &&
      formContext?.organizationId === currentOrganization.id &&
      canCreate ? (
        <div className="deadline-create-panel">
          <h3>Novo prazo</h3>

          <form className="process-lookup-form" onSubmit={handleProcessSearch}>
            <label htmlFor="deadline-process-search">Buscar processo</label>
            <div className="process-lookup-search-row">
              <input
                id="deadline-process-search"
                name="processSearch"
                value={searchInput}
                onChange={(event) => setSearchInput(event.target.value)}
                disabled={isSubmitting}
                autoFocus
              />
              <button
                className="secondary-button"
                type="submit"
                disabled={isSubmitting}
              >
                Buscar
              </button>
            </div>
          </form>

          {selectedProcess ? (
            <div className="selected-process">
              <p role="status">
                Processo selecionado: <strong>{selectedProcess.title}</strong>
                <span> — Cliente: {selectedProcess.clientName}</span>
              </p>
              <button
                className="text-button"
                type="button"
                onClick={() => {
                  setSelectedProcess(undefined)
                  setCreateError(undefined)
                }}
                disabled={isSubmitting}
              >
                Limpar seleção
              </button>
            </div>
          ) : null}

          {currentLookupState?.status === 'loading' ? (
            <p className="process-lookup-status" role="status">
              Carregando processos...
            </p>
          ) : null}

          {currentLookupState?.status === 'forbidden' ? (
            <div className="process-lookup-status" role="alert">
              <p>Não foi possível acessar os processos desta organização.</p>
              <button
                className="secondary-button"
                type="button"
                onClick={retryLookup}
                disabled={isSubmitting}
              >
                Tentar novamente
              </button>
            </div>
          ) : null}

          {currentLookupState?.status === 'error' ? (
            <div className="process-lookup-status" role="alert">
              <p>{genericLookupError}</p>
              <button
                className="secondary-button"
                type="button"
                onClick={retryLookup}
                disabled={isSubmitting}
              >
                Tentar novamente
              </button>
            </div>
          ) : null}

          {currentLookupState?.status === 'success' &&
          currentLookupState.items.length === 0 ? (
            <div className="process-lookup-status" role="status">
              {currentLookupState.search.length === 0 ? (
                <p>
                  Não há processo disponível para cadastrar um prazo nesta
                  organização.
                </p>
              ) : (
                <p>Nenhum processo encontrado para esta busca.</p>
              )}
            </div>
          ) : null}

          {lookupItems.length > 0 ? (
            <div
              className="process-lookup-results"
              aria-label="Processos encontrados"
              aria-describedby={processError ? 'deadline-process-error' : undefined}
            >
              <ul>
                {lookupItems.map((legalProcess) => (
                  <li key={legalProcess.id}>
                    <button
                      className="process-result-button"
                      type="button"
                      onClick={() => {
                        setSelectedProcess(legalProcess)
                        setProcessError(undefined)
                        setCreateError(undefined)
                      }}
                      aria-pressed={selectedProcess?.id === legalProcess.id}
                      disabled={isSubmitting}
                    >
                      <span>{legalProcess.title}</span>
                      <small>Cliente: {legalProcess.clientName}</small>
                    </button>
                  </li>
                ))}
              </ul>
            </div>
          ) : null}

          {currentLookupState?.status === 'success' &&
          currentLookupState.hasNext ? (
            <button
              className="secondary-button load-more-processes"
              type="button"
              onClick={loadMoreProcesses}
              disabled={isSubmitting}
            >
              Carregar mais
            </button>
          ) : null}

          {processError ? (
            <p id="deadline-process-error" className="form-error" role="alert">
              {processError}
            </p>
          ) : null}

          <form className="deadline-create-form" onSubmit={handleCreate}>
            <label htmlFor="deadline-title">Título</label>
            <input
              id="deadline-title"
              name="title"
              value={title}
              maxLength={maximumTitleLength}
              onChange={(event) => {
                setTitle(event.target.value)
                setTitleError(undefined)
              }}
              aria-describedby={titleError ? 'deadline-title-error' : undefined}
              aria-invalid={titleError ? true : undefined}
              disabled={isSubmitting}
              required
            />
            {titleError ? (
              <p id="deadline-title-error" className="form-error" role="alert">
                {titleError}
              </p>
            ) : null}

            <label htmlFor="deadline-due-date">Data do prazo</label>
            <input
              id="deadline-due-date"
              name="dueDate"
              type="date"
              value={dueDate}
              onChange={(event) => {
                setDueDate(event.target.value)
                setDueDateError(undefined)
              }}
              aria-describedby={
                dueDateError ? 'deadline-due-date-error' : undefined
              }
              aria-invalid={dueDateError ? true : undefined}
              disabled={isSubmitting}
              required
            />
            {dueDateError ? (
              <p
                id="deadline-due-date-error"
                className="form-error"
                role="alert"
              >
                {dueDateError}
              </p>
            ) : null}

            {createError ? (
              <div className="deadline-create-error">
                <p className="form-error" role="alert">
                  {createError}
                </p>
                {createError === createPermissionError ? (
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

            <div className="deadline-form-actions">
              <button
                className="secondary-button"
                type="button"
                onClick={closeCreate}
                disabled={isSubmitting}
              >
                Cancelar
              </button>
              <button
                className="primary-button"
                type="submit"
                disabled={isSubmitting}
              >
                {isSubmitting ? 'Cadastrando...' : 'Cadastrar'}
              </button>
            </div>
          </form>
        </div>
      ) : null}

      {currentListState.status === 'loading' ? (
        <p className="deadlines-state" role="status">
          Carregando prazos...
        </p>
      ) : null}

      {currentListState.status === 'forbidden' ? (
        <div className="deadlines-state" role="alert">
          <h3>Acesso indisponível</h3>
          <p>Não foi possível acessar os prazos desta organização.</p>
          <div className="deadlines-state-actions">
            <button
              className="secondary-button"
              type="button"
              onClick={refreshOrganizations}
            >
              Atualizar acesso
            </button>
            <Link className="home-link" to="/organizations">
              Voltar para organizações
            </Link>
          </div>
        </div>
      ) : null}

      {currentListState.status === 'error' ? (
        <div className="deadlines-state" role="alert">
          <p>{genericListError}</p>
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
            <div className="deadlines-state" role="status">
              <p>
                {page === 1
                  ? 'Nenhum prazo cadastrado nesta organização.'
                  : 'Nenhum prazo encontrado nesta página.'}
              </p>
              {page === 1 && canCreate && !isCreateOpen ? (
                <button
                  className="secondary-button"
                  type="button"
                  onClick={openCreate}
                >
                  Cadastrar primeiro prazo
                </button>
              ) : null}
            </div>
          ) : (
            <div className="deadlines-table-wrapper">
              <table className="deadlines-table">
                <caption className="visually-hidden">
                  Prazos da organização {currentOrganization.name}
                </caption>
                <thead>
                  <tr>
                    <th scope="col">Título</th>
                    <th scope="col">Processo</th>
                    <th scope="col">Cliente</th>
                    <th scope="col">Vencimento</th>
                    <th scope="col">Estado</th>
                  </tr>
                </thead>
                <tbody>
                  {currentListState.response.items.map((deadline) => (
                    <tr key={deadline.id}>
                      <td data-label="Título">{deadline.title}</td>
                      <td data-label="Processo">{deadline.processTitle}</td>
                      <td data-label="Cliente">{deadline.clientName}</td>
                      <td data-label="Vencimento">
                        {formatLegalDeadlineDueDate(deadline.dueDate)}
                      </td>
                      <td data-label="Estado">
                        <span
                          className={`deadline-status is-${deadline.state.toLowerCase()}`}
                        >
                          {getDeadlineStateLabel(deadline.state)}
                        </span>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}

          <nav
            className="deadlines-pagination"
            aria-label="Paginação de prazos"
          >
            <button
              className="secondary-button"
              type="button"
              onClick={() => navigateToPage(page - 1)}
              disabled={page === 1}
              aria-label="Página anterior de prazos"
            >
              Anterior
            </button>
            <span aria-current="page">Página {page}</span>
            <button
              className="secondary-button"
              type="button"
              onClick={() => navigateToPage(page + 1)}
              disabled={
                page === maximumPageNumber ||
                currentListState.response.items.length < pageSize
              }
              aria-label="Próxima página de prazos"
            >
              Próxima
            </button>
          </nav>
        </>
      ) : null}
    </section>
  )
}
