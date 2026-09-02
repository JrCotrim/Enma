import { useEffect, useRef, useState, type FormEvent } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { useAuth } from '../authentication/AuthContext'
import {
  useCurrentOrganization,
  useOrganizationDiscovery,
} from '../organizations/OrganizationContext'
import { lookupActiveClients } from './activeClientLookupService'
import { formatLegalProcessCreatedAt } from './legalProcessFormatting'
import {
  createLegalProcess,
  LegalProcessRequestError,
  listLegalProcesses,
} from './legalProcessService'
import type {
  ActiveClientLookupItem,
  LegalProcessListResponse,
} from './legalProcessTypes'

const pageSize = 20
const maximumPageNumber = 2_147_483_647
const maximumTitleLength = 150
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
    if (!isCanonicalPage(pageParameter, page)) {
      const normalized = new URLSearchParams(searchParams)
      normalized.delete('page')
      setSearchParams(normalized, { replace: true })
    }
  }, [page, pageParameter, searchParams, setSearchParams])

  useEffect(() => {
    const controller = new AbortController()
    const requestVersion = ++listRequestVersionRef.current

    void listLegalProcesses(
      currentOrganization.id,
      page,
      pageSize,
      handleUnauthorized,
      controller.signal,
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
    const controller = new AbortController()
    createControllerRef.current = controller
    isSubmittingRef.current = true
    setIsSubmitting(true)
    setTitleError(undefined)
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
          {currentListState.response.items.length === 0 ? (
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
            <div className="processes-table-wrapper">
              <table className="processes-table">
                <caption className="visually-hidden">
                  Processos da organização {currentOrganization.name}
                </caption>
                <thead>
                  <tr>
                    <th scope="col">Título</th>
                    <th scope="col">Cliente</th>
                    <th scope="col">Criado em</th>
                  </tr>
                </thead>
                <tbody>
                  {currentListState.response.items.map((legalProcess) => (
                    <tr key={legalProcess.id}>
                      <td data-label="Título">
                        <Link
                          className="process-detail-link"
                          to={`/organizations/${currentOrganization.id}/processes/${legalProcess.id}`}
                        >
                          {legalProcess.title}
                        </Link>
                      </td>
                      <td data-label="Cliente">{legalProcess.clientName}</td>
                      <td data-label="Criado em">
                        {formatLegalProcessCreatedAt(legalProcess.createdAt)}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
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
                currentListState.response.items.length < pageSize
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
