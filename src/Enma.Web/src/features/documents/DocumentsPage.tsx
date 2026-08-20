import { useEffect, useRef, useState, type FormEvent } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { useAuth } from '../authentication/AuthContext'
import { isValidGuid } from '../deadlines/legalDeadlineFormatting'
import { lookupLegalProcesses } from '../deadlines/legalProcessLookupService'
import {
  useCurrentOrganization,
  useOrganizationDiscovery,
} from '../organizations/OrganizationContext'
import { lookupActiveClients } from '../processes/activeClientLookupService'
import type { ActiveClientLookupItem } from '../processes/legalProcessTypes'
import { TaskLookupPicker } from '../tasks/TaskLookupPicker'
import type { LegalProcessLookupItem } from '../tasks/legalTaskTypes'
import {
  formatDocumentCreatedAt,
  formatDocumentFileType,
  formatDocumentSize,
  getDocumentContextLabel,
} from './documentFormatting'
import {
  DocumentRequestError,
  getDocumentDownloadUrl,
  listDocuments,
} from './documentService'
import type { LegalDocumentListResponse } from './documentTypes'

const pageSize = 20
const maximumPageNumber = 2_147_483_647
const maximumSearchLength = 150

type ListState =
  | { readonly status: 'loading'; readonly scope: string }
  | {
      readonly status: 'success'
      readonly scope: string
      readonly response: LegalDocumentListResponse
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

export function DocumentsPage() {
  const { currentOrganization } = useCurrentOrganization()
  const { refreshOrganizations } = useOrganizationDiscovery()
  const { handleUnauthorized } = useAuth()
  const [searchParams, setSearchParams] = useSearchParams()
  const search = (searchParams.get('search') ?? '').slice(0, maximumSearchLength)
  const processParameter = searchParams.get('processId')
  const processId =
    processParameter !== null && isValidGuid(processParameter)
      ? processParameter
      : undefined
  const clientParameter = searchParams.get('clientId')
  const clientId =
    !processId && clientParameter !== null && isValidGuid(clientParameter)
      ? clientParameter
      : undefined
  const page = resolvePage(searchParams.get('page'))
  const [selectedClient, setSelectedClient] =
    useState<ActiveClientLookupItem>()
  const [selectedProcess, setSelectedProcess] =
    useState<LegalProcessLookupItem>()
  const [isClientFilterOpen, setIsClientFilterOpen] = useState(false)
  const [isProcessFilterOpen, setIsProcessFilterOpen] = useState(false)
  const [refreshVersion, setRefreshVersion] = useState(0)
  const listScope = `${currentOrganization.id}:${search}:${clientId ?? ''}:${processId ?? ''}:${page}:${refreshVersion}`
  const [listState, setListState] = useState<ListState>({
    status: 'loading',
    scope: listScope,
  })
  const requestIdRef = useRef(0)

  useEffect(() => {
    const normalized = new URLSearchParams(searchParams)
    let changed = false

    if ((searchParams.get('search') ?? '') !== search) {
      if (search.length === 0) normalized.delete('search')
      else normalized.set('search', search)
      changed = true
    }
    if (processParameter !== null && !processId) {
      normalized.delete('processId')
      changed = true
    }
    if (clientParameter !== null && (!clientId || Boolean(processId))) {
      normalized.delete('clientId')
      changed = true
    }
    const pageParameter = searchParams.get('page')
    if (pageParameter !== null && (page === 1 || pageParameter !== page.toString())) {
      normalized.delete('page')
      changed = true
    }

    if (changed) setSearchParams(normalized, { replace: true })
  }, [clientId, clientParameter, page, processId, processParameter, search, searchParams, setSearchParams])

  useEffect(() => {
    const controller = new AbortController()
    const requestId = ++requestIdRef.current

    void listDocuments(
      currentOrganization.id,
      { search: search || undefined, clientId, processId, pageNumber: page, pageSize },
      handleUnauthorized,
      controller.signal,
    )
      .then((response) => {
        if (!controller.signal.aborted && requestId === requestIdRef.current) {
          setListState({ status: 'success', scope: listScope, response })
        }
      })
      .catch((error: unknown) => {
        if (
          controller.signal.aborted ||
          requestId !== requestIdRef.current ||
          isAbortError(error) ||
          (error instanceof DocumentRequestError && error.failure === 'unauthorized')
        ) {
          return
        }

        setListState({
          status:
            error instanceof DocumentRequestError
              ? error.failure === 'forbidden'
                ? 'forbidden'
                : error.failure === 'bad-request'
                  ? 'invalid'
                  : 'error'
              : 'error',
          scope: listScope,
        })
      })

    return () => controller.abort()
  }, [clientId, currentOrganization.id, handleUnauthorized, listScope, page, processId, refreshVersion, search])

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

  function submitSearch(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const value = new FormData(event.currentTarget).get('search')
    const normalizedSearch = typeof value === 'string' ? value.trim() : ''
    updateFilters({ search: normalizedSearch || undefined })
  }

  function navigateToPage(nextPage: number) {
    const next = new URLSearchParams(searchParams)
    if (nextPage === 1) next.delete('page')
    else next.set('page', nextPage.toString())
    setSearchParams(next)
  }

  const currentClient = selectedClient?.id === clientId ? selectedClient : undefined
  const currentProcess =
    selectedProcess?.id === processId ? selectedProcess : undefined
  const isFiltered = search.length > 0 || Boolean(clientId) || Boolean(processId)

  return (
    <section className="documents-page" aria-labelledby="documents-title">
      <div className="documents-header">
        <div>
          <p className="eyebrow">Acervo jurídico</p>
          <h2 id="documents-title">Documentos</h2>
          <p className="documents-description">
            Consulte e baixe os documentos privados desta organização.
          </p>
        </div>
      </div>

      <div className="document-filters" aria-label="Filtros de documentos">
        <form key={search} className="document-search" onSubmit={submitSearch}>
          <label htmlFor="document-search">Buscar por nome do arquivo</label>
          <div className="document-search-row">
            <input
              id="document-search"
              name="search"
              defaultValue={search}
              maxLength={maximumSearchLength}
            />
            <button className="secondary-button" type="submit">Buscar</button>
          </div>
        </form>

        <div className="document-filter-control">
          <span>Cliente</span>
          <span>{clientId ? currentClient?.name ?? 'Cliente selecionado' : 'Todos os clientes'}</span>
          <div className="document-filter-actions">
            <button
              className="secondary-button"
              type="button"
              onClick={() => {
                setIsClientFilterOpen((open) => !open)
                setIsProcessFilterOpen(false)
              }}
            >
              {isClientFilterOpen ? 'Fechar busca' : 'Escolher cliente'}
            </button>
            {clientId ? (
              <button
                className="text-button"
                type="button"
                onClick={() => {
                  setSelectedClient(undefined)
                  setIsClientFilterOpen(false)
                  updateFilters({ clientId: undefined })
                }}
              >
                Limpar cliente
              </button>
            ) : null}
          </div>
          {isClientFilterOpen ? (
            <TaskLookupPicker
              organizationId={currentOrganization.id}
              searchLabel="Buscar cliente para filtro"
              resultsLabel="Clientes encontrados para o filtro"
              loadingMessage="Carregando clientes..."
              emptyMessage="Não há clientes ativos disponíveis."
              noResultsMessage="Nenhum cliente encontrado para esta busca."
              errorMessage="Não foi possível carregar os clientes. Tente novamente."
              selectedId={clientId}
              load={lookupActiveClients}
              onUnauthorized={handleUnauthorized}
              onSelect={(item) => {
                setSelectedClient(item)
                setSelectedProcess(undefined)
                setIsClientFilterOpen(false)
                updateFilters({ clientId: item.id, processId: undefined })
              }}
              renderItem={(item) => <span>{item.name}</span>}
            />
          ) : null}
        </div>

        <div className="document-filter-control">
          <span>Processo</span>
          <span>
            {processId
              ? currentProcess
                ? `${currentProcess.title} — ${currentProcess.clientName}`
                : 'Processo selecionado'
              : 'Todos os processos'}
          </span>
          <div className="document-filter-actions">
            <button
              className="secondary-button"
              type="button"
              onClick={() => {
                setIsProcessFilterOpen((open) => !open)
                setIsClientFilterOpen(false)
              }}
            >
              {isProcessFilterOpen ? 'Fechar busca' : 'Escolher processo'}
            </button>
            {processId ? (
              <button
                className="text-button"
                type="button"
                onClick={() => {
                  setSelectedProcess(undefined)
                  setIsProcessFilterOpen(false)
                  updateFilters({ processId: undefined })
                }}
              >
                Limpar processo
              </button>
            ) : null}
          </div>
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
                setSelectedProcess(item)
                setSelectedClient(undefined)
                setIsProcessFilterOpen(false)
                updateFilters({ processId: item.id, clientId: undefined })
              }}
              renderItem={(item) => (
                <><span>{item.title}</span><small>Cliente: {item.clientName}</small></>
              )}
            />
          ) : null}
        </div>
      </div>

      {currentListState.status === 'loading' ? (
        <div className="documents-state" role="status"><p>Carregando documentos...</p></div>
      ) : null}
      {currentListState.status === 'invalid' ? (
        <div className="documents-state" role="alert">
          <h3>Não foi possível aplicar os filtros</h3>
          <p>Revise a busca e os filtros selecionados.</p>
          <button className="secondary-button" type="button" onClick={() => setSearchParams({})}>Limpar filtros</button>
        </div>
      ) : null}
      {currentListState.status === 'forbidden' ? (
        <div className="documents-state" role="alert">
          <h3>Acesso aos documentos indisponível</h3>
          <p>Seu acesso à organização pode ter mudado.</p>
          <button className="secondary-button" type="button" onClick={refreshOrganizations}>Atualizar acesso</button>
        </div>
      ) : null}
      {currentListState.status === 'error' ? (
        <div className="documents-state" role="alert">
          <h3>Não foi possível carregar os documentos</h3>
          <p>Tente novamente. Nenhum detalhe interno foi exibido.</p>
          <button className="secondary-button" type="button" onClick={() => setRefreshVersion((version) => version + 1)}>Tentar novamente</button>
        </div>
      ) : null}
      {currentListState.status === 'success' && currentListState.response.items.length === 0 ? (
        <div className="documents-state" role="status">
          <h3>{isFiltered ? 'Nenhum documento encontrado' : 'Nenhum documento disponível'}</h3>
          <p>{isFiltered ? 'Ajuste a busca ou os filtros para tentar novamente.' : 'Os documentos enviados para esta organização aparecerão aqui.'}</p>
        </div>
      ) : null}

      {currentListState.status === 'success' && currentListState.response.items.length > 0 ? (
        <>
          <div className="documents-table-wrapper">
            <table className="documents-table">
              <thead><tr><th>Arquivo</th><th>Contexto</th><th>Tipo</th><th>Tamanho</th><th>Adicionado em</th><th>Ações</th></tr></thead>
              <tbody>
                {currentListState.response.items.map((document) => (
                  <tr key={document.id}>
                    <td data-label="Arquivo"><Link to={document.id}>{document.originalFileName}</Link></td>
                    <td data-label="Contexto">{getDocumentContextLabel(document)}</td>
                    <td data-label="Tipo">{formatDocumentFileType(document.contentType)}</td>
                    <td data-label="Tamanho">{formatDocumentSize(document.sizeBytes)}</td>
                    <td data-label="Adicionado em">{formatDocumentCreatedAt(document.createdAt)}</td>
                    <td data-label="Ações">
                      <div className="document-row-actions">
                        <Link to={document.id}>Ver detalhes</Link>
                        <a href={getDocumentDownloadUrl(currentOrganization.id, document.id)}>Baixar</a>
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <div className="documents-pagination" aria-label="Paginação de documentos">
            <button className="secondary-button" type="button" disabled={page === 1} onClick={() => navigateToPage(page - 1)}>Página anterior</button>
            <span>Página {page}</span>
            <button className="secondary-button" type="button" disabled={!currentListState.response.hasNext} onClick={() => navigateToPage(page + 1)}>Próxima página</button>
          </div>
        </>
      ) : null}
    </section>
  )
}
