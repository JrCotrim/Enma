import { useEffect, useRef, useState, type FormEvent } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { useAuth } from '../authentication/AuthContext'
import {
  useCurrentOrganization,
  useOrganizationDiscovery,
} from '../organizations/OrganizationContext'
import { isValidGuid } from '../deadlines/legalDeadlineFormatting'
import type { OrganizationMemberLookupItem } from '../tasks/legalTaskTypes'
import { lookupOrganizationMembers } from '../tasks/organizationMemberLookupService'
import { TaskLookupPicker } from '../tasks/TaskLookupPicker'
import { lookupActiveClients } from './activeClientLookupService'
import {
  formatLegalProcessCreatedAt,
  getLegalProcessStatusLabel,
  getLegalProcessStatusTone,
  isLegalProcessStatus,
} from './legalProcessFormatting'
import {
  createLegalProcess,
  LegalProcessRequestError,
  listLegalProcesses,
} from './legalProcessService'
import {
  legalProcessStatuses,
  type ActiveClientLookupItem,
  type LegalProcessListResponse,
  type LegalProcessStatus,
} from './legalProcessTypes'

const pageSize = 20
const maximumPageNumber = 2_147_483_647
const maximumTitleLength = 150
const maximumSearchLength = 150
const maximumProcessNumberLength = 100
const maximumCourtOrAuthorityLength = 200
const defaultCreateStatus: LegalProcessStatus = 'inProgress'
const genericListError =
  'Não foi possível carregar os processos. Tente novamente.'
const genericLookupError =
  'Não foi possível carregar os clientes ativos. Tente novamente.'
const genericCreateError =
  'Não foi possível cadastrar o processo. Tente novamente.'
const createPermissionError =
  'Você não tem permissão para cadastrar processos nesta organização.'
const selectedClientUnavailableError =
  'O cliente selecionado não está disponível para este cadastro.'
const createValidationError =
  'Não foi possível validar o cadastro. Verifique os dados e tente novamente.'
const duplicateProcessNumberError =
  'Já existe um processo com este número nesta organização.'
const responsibleUnavailableError =
  'O responsável selecionado não está mais disponível. Escolha outra pessoa.'

type CreateResponsibleMode = 'unassigned' | 'self' | 'other'

type ListState =
  | { readonly status: 'loading'; readonly scope: string }
  | {
      readonly status: 'success'
      readonly scope: string
      readonly response: LegalProcessListResponse
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
  readonly baseItems: readonly ActiveClientLookupItem[]
}

type LookupState =
  | { readonly status: 'idle' }
  | {
      readonly status: 'loading'
      readonly context: FormContext
      readonly search: string
      readonly pageNumber: number
      readonly append: boolean
      readonly items: readonly ActiveClientLookupItem[]
    }
  | {
      readonly status: 'success'
      readonly context: FormContext
      readonly search: string
      readonly pageNumber: number
      readonly items: readonly ActiveClientLookupItem[]
      readonly hasNext: boolean
    }
  | {
      readonly status: 'forbidden' | 'error'
      readonly context: FormContext
      readonly search: string
      readonly pageNumber: number
      readonly append: boolean
      readonly items: readonly ActiveClientLookupItem[]
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

function resolveSearch(value: string | null): string {
  return (value ?? '').trim().slice(0, maximumSearchLength)
}

function resolveStatusFilter(
  value: string | null,
): LegalProcessStatus | undefined {
  return isLegalProcessStatus(value) ? value : undefined
}

function resolveResponsibleFilter(value: string | null): string {
  if (value === 'self' || value === 'unassigned') return value
  return value !== null && isValidGuid(value) ? value : 'any'
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

function deduplicateClients(
  existing: readonly ActiveClientLookupItem[],
  additional: readonly ActiveClientLookupItem[],
): readonly ActiveClientLookupItem[] {
  const clients = new Map(
    existing.map((client) => [client.id.toLowerCase(), client]),
  )

  for (const client of additional) {
    clients.set(client.id.toLowerCase(), client)
  }

  return [...clients.values()]
}

export function ProcessesPage() {
  const { currentOrganization } = useCurrentOrganization()
  const { refreshOrganizations } = useOrganizationDiscovery()
  const { handleUnauthorized } = useAuth()
  const [searchParams, setSearchParams] = useSearchParams()
  const pageParameter = searchParams.get('page')
  const page = resolvePage(pageParameter)
  const searchParameter = searchParams.get('search')
  const search = resolveSearch(searchParameter)
  const statusParameter = searchParams.get('status')
  const statusFilter = resolveStatusFilter(statusParameter)
  const responsibleParameter = searchParams.get('responsible')
  const responsibleFilter = resolveResponsibleFilter(responsibleParameter)
  const [refreshVersion, setRefreshVersion] = useState(0)
  const listScope = JSON.stringify([
    currentOrganization.id,
    page,
    search,
    statusFilter ?? '',
    responsibleFilter,
    refreshVersion,
  ])
  const [listState, setListState] = useState<ListState>({
    status: 'loading',
    scope: listScope,
  })
  const listRequestVersionRef = useRef(0)
  const currentOrganizationIdRef = useRef(currentOrganization.id)
  const mountedRef = useRef(true)

  const [selectedFilterMember, setSelectedFilterMember] =
    useState<OrganizationMemberLookupItem>()
  const [isMemberFilterOpen, setIsMemberFilterOpen] = useState(false)

  const [isCreateOpen, setIsCreateOpen] = useState(false)
  const [formContext, setFormContext] = useState<FormContext>()
  const formContextRef = useRef<FormContext | undefined>(undefined)
  const formSessionRef = useRef(0)
  const [title, setTitle] = useState('')
  const [titleError, setTitleError] = useState<string>()
  const [processNumber, setProcessNumber] = useState('')
  const [processNumberError, setProcessNumberError] = useState<string>()
  const [courtOrAuthority, setCourtOrAuthority] = useState('')
  const [createStatus, setCreateStatus] =
    useState<LegalProcessStatus>(defaultCreateStatus)
  const [createResponsibleMode, setCreateResponsibleMode] =
    useState<CreateResponsibleMode>('unassigned')
  const [selectedCreateMember, setSelectedCreateMember] =
    useState<OrganizationMemberLookupItem>()
  const [responsibleError, setResponsibleError] = useState<string>()
  const [selectedClient, setSelectedClient] =
    useState<ActiveClientLookupItem>()
  const [clientError, setClientError] = useState<string>()
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
    const normalized = new URLSearchParams(searchParams)
    let changed = false

    if (!isCanonicalPage(pageParameter, page)) {
      normalized.delete('page')
      changed = true
    }

    if (searchParameter !== null && searchParameter !== search) {
      if (search.length > 0) {
        normalized.set('search', search)
      } else {
        normalized.delete('search')
      }
      changed = true
    }

    if (statusParameter !== null && !statusFilter) {
      normalized.delete('status')
      changed = true
    }

    if (
      responsibleParameter !== null &&
      responsibleParameter !== responsibleFilter
    ) {
      normalized.delete('responsible')
      changed = true
    }

    if (changed) {
      setSearchParams(normalized, { replace: true })
    }
  }, [
    page,
    pageParameter,
    responsibleFilter,
    responsibleParameter,
    search,
    searchParameter,
    searchParams,
    setSearchParams,
    statusFilter,
    statusParameter,
  ])

  useEffect(() => {
    const controller = new AbortController()
    const requestVersion = ++listRequestVersionRef.current

    void listLegalProcesses(
      currentOrganization.id,
      page,
      pageSize,
      handleUnauthorized,
      controller.signal,
      {
        search: search || undefined,
        status: statusFilter,
        responsible: responsibleFilter,
      },
    )
      .then((response) => {
        if (
          !controller.signal.aborted &&
          requestVersion === listRequestVersionRef.current
        ) {
          setListState({ status: 'success', scope: listScope, response })
        }
      })
      .catch((error: unknown) => {
        if (
          controller.signal.aborted ||
          requestVersion !== listRequestVersionRef.current ||
          isAbortError(error) ||
          (error instanceof LegalProcessRequestError &&
            error.failure === 'unauthorized')
        ) {
          return
        }

        setListState({
          status:
            error instanceof LegalProcessRequestError &&
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
    responsibleFilter,
    search,
    statusFilter,
  ])

  useEffect(() => {
    if (!lookupRequest) {
      return
    }

    const controller = new AbortController()
    let active = true

    void lookupActiveClients(
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
          ? deduplicateClients(lookupRequest.baseItems, response.items)
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
          (error instanceof LegalProcessRequestError &&
            error.failure === 'unauthorized')
        ) {
          return
        }

        setLookupState({
          status:
            error instanceof LegalProcessRequestError &&
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

  function updateFilters(changes: Record<string, string | undefined>) {
    const nextSearchParams = new URLSearchParams(searchParams)

    for (const [key, value] of Object.entries(changes)) {
      if (value === undefined) {
        nextSearchParams.delete(key)
      } else {
        nextSearchParams.set(key, value)
      }
    }

    nextSearchParams.delete('page')
    setSearchParams(nextSearchParams)
  }

  function submitSearch(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const value = new FormData(event.currentTarget).get('search')
    const normalizedSearch =
      typeof value === 'string' ? resolveSearch(value) : ''
    updateFilters({ search: normalizedSearch || undefined })
  }

  function clearFilters() {
    setSelectedFilterMember(undefined)
    setIsMemberFilterOpen(false)
    updateFilters({
      search: undefined,
      status: undefined,
      responsible: undefined,
    })
  }

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
    baseItems: readonly ActiveClientLookupItem[],
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

  function resetOperationalFields() {
    setProcessNumber('')
    setProcessNumberError(undefined)
    setCourtOrAuthority('')
    setCreateStatus(defaultCreateStatus)
    setCreateResponsibleMode('unassigned')
    setSelectedCreateMember(undefined)
    setResponsibleError(undefined)
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
    resetOperationalFields()
    setSelectedClient(undefined)
    setClientError(undefined)
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
    resetOperationalFields()
    setSelectedClient(undefined)
    setClientError(undefined)
    setCreateError(undefined)
    setSearchInput('')
    setLookupState({ status: 'idle' })
  }

  function closeCreate() {
    if (isSubmittingRef.current) {
      return
    }

    resetCreateState()
  }

  function handleClientSearch(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()

    const context = formContextRef.current
    if (!context || context.organizationId !== currentOrganization.id) {
      return
    }

    startLookup(context, searchInput.trim(), 1, false, [])
  }

  function loadMoreClients() {
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
      setTitleError('Informe o título do processo.')
      isValid = false
    } else if (trimmedTitle.length > maximumTitleLength) {
      setTitleError(
        `O título deve ter no máximo ${maximumTitleLength} caracteres.`,
      )
      isValid = false
    }

    if (!selectedClient) {
      setClientError('Selecione um cliente ativo.')
      isValid = false
    }

    if (createResponsibleMode === 'other' && !selectedCreateMember) {
      setResponsibleError('Selecione uma pessoa responsável.')
      isValid = false
    }

    if (
      !isValid ||
      !selectedClient ||
      !context ||
      context.organizationId !== currentOrganization.id
    ) {
      return
    }

    const operationId = ++createOperationRef.current
    const selectedClientId = selectedClient.id
    const trimmedProcessNumber = processNumber.trim()
    const trimmedCourtOrAuthority = courtOrAuthority.trim()
    const submittedResponsibleMode = createResponsibleMode
    const responsibleMembershipId =
      submittedResponsibleMode === 'self'
        ? currentOrganization.membershipId
        : submittedResponsibleMode === 'other'
          ? selectedCreateMember?.id
          : undefined
    const controller = new AbortController()
    createControllerRef.current = controller
    isSubmittingRef.current = true
    setIsSubmitting(true)
    setTitleError(undefined)
    setProcessNumberError(undefined)
    setResponsibleError(undefined)
    setClientError(undefined)
    setCreateError(undefined)
    setSuccessMessage(undefined)

    const isCurrentOperation = () =>
      mountedRef.current &&
      !controller.signal.aborted &&
      operationId === createOperationRef.current &&
      isSameFormContext(formContextRef.current, context) &&
      currentOrganizationIdRef.current === context.organizationId

    try {
      await createLegalProcess(
        context.organizationId,
        selectedClientId,
        trimmedTitle,
        handleUnauthorized,
        controller.signal,
        {
          ...(trimmedProcessNumber.length > 0
            ? { processNumber: trimmedProcessNumber }
            : {}),
          ...(createStatus !== defaultCreateStatus
            ? { status: createStatus }
            : {}),
          ...(trimmedCourtOrAuthority.length > 0
            ? { courtOrAuthority: trimmedCourtOrAuthority }
            : {}),
          ...(responsibleMembershipId !== undefined
            ? { responsibleMembershipId }
            : {}),
        },
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
        message: 'Processo cadastrado com sucesso.',
      })
      setRefreshVersion((version) => version + 1)
    } catch (error) {
      if (
        !isCurrentOperation() ||
        isAbortError(error) ||
        (error instanceof LegalProcessRequestError &&
          error.failure === 'unauthorized')
      ) {
        return
      }

      if (
        error instanceof LegalProcessRequestError &&
        error.failure === 'forbidden'
      ) {
        setCreateError(createPermissionError)
      } else if (
        error instanceof LegalProcessRequestError &&
        error.failure === 'not-found'
      ) {
        setSelectedClient(undefined)
        setCreateError(selectedClientUnavailableError)
      } else if (
        error instanceof LegalProcessRequestError &&
        error.failure === 'conflict'
      ) {
        setProcessNumberError(duplicateProcessNumberError)
      } else if (
        error instanceof LegalProcessRequestError &&
        error.failure === 'related-responsible-unavailable'
      ) {
        setSelectedCreateMember(undefined)
        if (submittedResponsibleMode === 'self') {
          setCreateResponsibleMode('unassigned')
        }
        setResponsibleError(responsibleUnavailableError)
      } else if (
        error instanceof LegalProcessRequestError &&
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
  const hasSpecificResponsible =
    responsibleFilter !== 'any' &&
    responsibleFilter !== 'self' &&
    responsibleFilter !== 'unassigned'
  const currentFilterMember =
    selectedFilterMember?.id === responsibleFilter
      ? selectedFilterMember
      : undefined
  const isFiltered =
    search.length > 0 ||
    statusFilter !== undefined ||
    responsibleFilter !== 'any'

  return (
    <section className="processes-page" aria-labelledby="processes-title">
      <div className="processes-header workspace-page-header">
        <div className="workspace-page-heading">
          <p className="eyebrow workspace-page-eyebrow">GESTÃO DE PROCESSOS</p>
          <h2 className="workspace-page-title" id="processes-title">Processos</h2>
          <p className="processes-description workspace-page-subtitle">
            Consulte os processos vinculados a esta organização.
          </p>
        </div>
        {canCreate &&
        (!isCreateOpen || formContext?.organizationId !== currentOrganization.id) ? (
          <button className="primary-button" type="button" onClick={openCreate}>
            Cadastrar processo
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
        <div className="process-create-panel">
          <h3>Novo processo</h3>

          <form className="client-lookup-form" onSubmit={handleClientSearch}>
            <label htmlFor="process-client-search">Buscar cliente</label>
            <div className="client-lookup-search-row">
              <input
                id="process-client-search"
                name="clientSearch"
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

          {selectedClient ? (
            <p className="selected-client" role="status">
              Cliente selecionado: <strong>{selectedClient.name}</strong>
            </p>
          ) : null}

          {currentLookupState?.status === 'loading' ? (
            <p className="client-lookup-status" role="status">
              Carregando clientes ativos...
            </p>
          ) : null}

          {currentLookupState?.status === 'forbidden' ? (
            <div className="client-lookup-status" role="alert">
              <p>Não foi possível acessar os clientes ativos desta organização.</p>
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
            <div className="client-lookup-status" role="alert">
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
            <div className="client-lookup-status" role="status">
              {currentLookupState.search.length === 0 ? (
                <>
                  <p>
                    É necessário ter um cliente ativo para cadastrar um processo.
                  </p>
                  <Link
                    className="home-link"
                    to={`/organizations/${encodeURIComponent(currentOrganization.id)}/clients`}
                  >
                    Ir para clientes
                  </Link>
                </>
              ) : (
                <p>Nenhum cliente encontrado para esta busca.</p>
              )}
            </div>
          ) : null}

          {lookupItems.length > 0 ? (
            <div
              className="client-lookup-results"
              aria-label="Clientes ativos encontrados"
              aria-describedby={clientError ? 'process-client-error' : undefined}
            >
              <ul>
                {lookupItems.map((client) => (
                  <li key={client.id}>
                    <button
                      className="client-result-button"
                      type="button"
                      onClick={() => {
                        setSelectedClient(client)
                        setClientError(undefined)
                        setCreateError(undefined)
                      }}
                      aria-pressed={selectedClient?.id === client.id}
                      disabled={isSubmitting}
                    >
                      Selecionar {client.name}
                    </button>
                  </li>
                ))}
              </ul>
            </div>
          ) : null}

          {currentLookupState?.status === 'success' &&
          currentLookupState.hasNext ? (
            <button
              className="secondary-button load-more-clients"
              type="button"
              onClick={loadMoreClients}
              disabled={isSubmitting}
            >
              Carregar mais
            </button>
          ) : null}

          {clientError ? (
            <p id="process-client-error" className="form-error" role="alert">
              {clientError}
            </p>
          ) : null}

          <form className="process-create-form" onSubmit={handleCreate}>
            <label htmlFor="process-title">Título</label>
            <input
              id="process-title"
              name="title"
              value={title}
              maxLength={maximumTitleLength}
              onChange={(event) => {
                setTitle(event.target.value)
                setTitleError(undefined)
              }}
              aria-describedby={titleError ? 'process-title-error' : undefined}
              aria-invalid={titleError ? true : undefined}
              disabled={isSubmitting}
              required
            />
            {titleError ? (
              <p id="process-title-error" className="form-error" role="alert">
                {titleError}
              </p>
            ) : null}

            <label htmlFor="process-number">Número do processo</label>
            <input
              id="process-number"
              name="processNumber"
              value={processNumber}
              maxLength={maximumProcessNumberLength}
              onChange={(event) => {
                setProcessNumber(event.target.value)
                setProcessNumberError(undefined)
              }}
              aria-describedby={
                processNumberError ? 'process-number-error' : undefined
              }
              aria-invalid={processNumberError ? true : undefined}
              disabled={isSubmitting}
              autoComplete="off"
            />
            {processNumberError ? (
              <p id="process-number-error" className="form-error" role="alert">
                {processNumberError}
              </p>
            ) : null}

            <label htmlFor="process-court">Órgão/tribunal</label>
            <input
              id="process-court"
              name="courtOrAuthority"
              value={courtOrAuthority}
              maxLength={maximumCourtOrAuthorityLength}
              onChange={(event) => setCourtOrAuthority(event.target.value)}
              disabled={isSubmitting}
              autoComplete="off"
            />

            <label htmlFor="process-status">Status</label>
            <select
              id="process-status"
              name="status"
              value={createStatus}
              onChange={(event) => {
                if (isLegalProcessStatus(event.target.value)) {
                  setCreateStatus(event.target.value)
                }
              }}
              disabled={isSubmitting}
            >
              {legalProcessStatuses.map((status) => (
                <option key={status} value={status}>
                  {getLegalProcessStatusLabel(status)}
                </option>
              ))}
            </select>

            <label htmlFor="process-responsible">Responsável</label>
            <select
              id="process-responsible"
              name="responsible"
              value={createResponsibleMode}
              onChange={(event) => {
                setCreateResponsibleMode(
                  event.target.value as CreateResponsibleMode,
                )
                setSelectedCreateMember(undefined)
                setResponsibleError(undefined)
              }}
              aria-describedby={
                responsibleError ? 'process-responsible-error' : undefined
              }
              aria-invalid={responsibleError ? true : undefined}
              disabled={isSubmitting}
            >
              <option value="unassigned">Sem responsável</option>
              <option value="self">Eu</option>
              <option value="other">Outra pessoa</option>
            </select>
            {createResponsibleMode === 'other' ? (
              <TaskLookupPicker
                organizationId={currentOrganization.id}
                searchLabel="Buscar responsável para processo"
                resultsLabel="Responsáveis encontrados para o processo"
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
                  setResponsibleError(undefined)
                  setCreateError(undefined)
                }}
                renderItem={(item) => <span>{item.displayName}</span>}
              />
            ) : null}
            {selectedCreateMember && createResponsibleMode === 'other' ? (
              <p className="process-selected-responsible" role="status">
                Responsável selecionado:{' '}
                <strong>{selectedCreateMember.displayName}</strong>
              </p>
            ) : null}
            {responsibleError ? (
              <p
                id="process-responsible-error"
                className="form-error"
                role="alert"
              >
                {responsibleError}
              </p>
            ) : null}

            {createError ? (
              <div className="process-create-error">
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
            <div className="process-form-actions">
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

      <div className="process-filters" role="group" aria-label="Filtros de processos">
        <form key={search} className="process-search" onSubmit={submitSearch}>
          <label htmlFor="process-search">Buscar por título, cliente ou número</label>
          <div className="process-search-row">
            <input
              id="process-search"
              name="search"
              type="search"
              defaultValue={search}
              maxLength={maximumSearchLength}
              autoComplete="off"
            />
            <button
              className="secondary-button"
              type="submit"
              aria-label="Buscar processos"
            >
              Buscar
            </button>
          </div>
          {search ? (
            <button
              className="text-button"
              type="button"
              onClick={() => updateFilters({ search: undefined })}
            >
              Limpar busca
            </button>
          ) : null}
        </form>

        <div className="process-filter-control">
          <label htmlFor="process-status-filter">Status</label>
          <select
            id="process-status-filter"
            value={statusFilter ?? 'any'}
            onChange={(event) => {
              const value = event.target.value
              updateFilters({
                status: isLegalProcessStatus(value) ? value : undefined,
              })
            }}
          >
            <option value="any">Todos</option>
            {legalProcessStatuses.map((status) => (
              <option key={status} value={status}>
                {getLegalProcessStatusLabel(status)}
              </option>
            ))}
          </select>
        </div>

        <div className="process-filter-control">
          <label htmlFor="process-responsible-filter">Responsável</label>
          <select
            id="process-responsible-filter"
            value={
              isMemberFilterOpen || hasSpecificResponsible
                ? 'specific'
                : responsibleFilter
            }
            onChange={(event) => {
              const value = event.target.value
              setSelectedFilterMember(undefined)
              if (value === 'specific') {
                setIsMemberFilterOpen(true)
              } else {
                setIsMemberFilterOpen(false)
                updateFilters({
                  responsible: value === 'any' ? undefined : value,
                })
              }
            }}
          >
            <option value="any">Todos</option>
            <option value="self">Meus</option>
            <option value="unassigned">Sem responsável</option>
            <option value="specific">Pessoa específica</option>
          </select>
          {hasSpecificResponsible ? (
            <div className="process-active-filter">
              <span>{currentFilterMember?.displayName ?? 'Pessoa selecionada'}</span>
              <button
                className="text-button"
                type="button"
                onClick={() => {
                  setSelectedFilterMember(undefined)
                  setIsMemberFilterOpen(false)
                  updateFilters({ responsible: undefined })
                }}
              >
                Limpar responsável
              </button>
            </div>
          ) : null}
          {hasSpecificResponsible && !isMemberFilterOpen ? (
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
              selectedId={hasSpecificResponsible ? responsibleFilter : undefined}
              load={lookupOrganizationMembers}
              onUnauthorized={handleUnauthorized}
              onSelect={(item) => {
                setSelectedFilterMember(item)
                setIsMemberFilterOpen(false)
                updateFilters({ responsible: item.id })
              }}
              renderItem={(item) => <span>{item.displayName}</span>}
            />
          ) : null}
        </div>
      </div>

      {currentListState.status === 'loading' ? (
        <p className="processes-state" role="status">
          Carregando processos...
        </p>
      ) : null}

      {currentListState.status === 'forbidden' ? (
        <div className="processes-state" role="alert">
          <h3>Acesso indisponível</h3>
          <p>Não foi possível acessar os processos desta organização.</p>
          <div className="processes-state-actions">
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
        <div className="processes-state" role="alert">
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
          {currentListState.response.items.length === 0 && isFiltered ? (
            <div className="processes-state" role="status">
              <p>Nenhum processo encontrado com estes filtros.</p>
              <button
                className="secondary-button"
                type="button"
                onClick={clearFilters}
              >
                Limpar filtros
              </button>
            </div>
          ) : currentListState.response.items.length === 0 ? (
            <div className="processes-state" role="status">
              <p>
                {page === 1
                  ? 'Nenhum processo cadastrado nesta organização.'
                  : 'Nenhum processo encontrado nesta página.'}
              </p>
              {page === 1 && canCreate && !isCreateOpen ? (
                <button
                  className="secondary-button"
                  type="button"
                  onClick={openCreate}
                >
                  Cadastrar primeiro processo
                </button>
              ) : null}
            </div>
          ) : (
            <section
              className="processes-results"
              aria-labelledby="processes-results-title"
            >
              <h3
                className="processes-results-title"
                id="processes-results-title"
              >
                Processos cadastrados
              </h3>
              <ul className="processes-list">
                {currentListState.response.items.map((legalProcess) => (
                  <li key={legalProcess.id}>
                    <Link
                      className="process-record-link"
                      to={`/organizations/${currentOrganization.id}/processes/${legalProcess.id}`}
                      aria-labelledby={`process-${legalProcess.id}-title`}
                      aria-describedby={`process-${legalProcess.id}-metadata`}
                    >
                      <span className="process-record-summary">
                        <strong
                          className="process-record-title"
                          id={`process-${legalProcess.id}-title`}
                        >
                          {legalProcess.title}
                        </strong>
                        <span
                          className="process-record-metadata"
                          id={`process-${legalProcess.id}-metadata`}
                        >
                          <span>{legalProcess.clientName}</span>
                          {legalProcess.processNumber ? (
                            <span className="process-record-number">
                              {legalProcess.processNumber}
                            </span>
                          ) : null}
                          <span className="process-record-responsible">
                            {legalProcess.responsibleDisplayName ??
                              'Sem responsável'}
                          </span>
                          <time dateTime={legalProcess.createdAt}>
                            {formatLegalProcessCreatedAt(legalProcess.createdAt)}
                          </time>
                          <span
                            className={`process-status ${getLegalProcessStatusTone(legalProcess.status)}`}
                          >
                            {getLegalProcessStatusLabel(legalProcess.status)}
                          </span>
                        </span>
                      </span>
                      <span className="process-record-chevron" aria-hidden="true">
                        <svg viewBox="0 0 24 24" focusable="false">
                          <path d="m9 18 6-6-6-6" />
                        </svg>
                      </span>
                    </Link>
                  </li>
                ))}
              </ul>
            </section>
          )}

          <nav
            className="processes-pagination"
            aria-label="Paginação de processos"
          >
            <button
              className="secondary-button"
              type="button"
              onClick={() => navigateToPage(page - 1)}
              disabled={page === 1}
              aria-label="Página anterior de processos"
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
                !currentListState.response.hasNext
              }
              aria-label="Próxima página de processos"
            >
              Próxima
            </button>
          </nav>
        </>
      ) : null}
    </section>
  )
}
