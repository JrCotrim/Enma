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
  uploadDocument,
} from './documentService'
import type {
  LegalDocumentListResponse,
  LegalDocumentUploadClassification,
} from './documentTypes'

const pageSize = 20
const maximumPageNumber = 2_147_483_647
const maximumSearchLength = 150
const maximumUploadSizeBytes = 26_214_400
const supportedFileExtensionPattern = /\.(pdf|docx|xlsx|png|jpe?g)$/i

type UploadClassification = 'general' | 'client' | 'process'
type UploadFailure =
  | 'validation'
  | 'forbidden'
  | 'not-found'
  | 'too-large'
  | 'unavailable'
  | 'outcome-unknown'
  | 'unexpected'

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

function validateUploadFile(file: File): string | undefined {
  if (file.size > maximumUploadSizeBytes) {
    return 'O arquivo excede o limite de 25 MiB.'
  }

  if (!supportedFileExtensionPattern.test(file.name)) {
    return 'Selecione um arquivo PDF, DOCX, XLSX, PNG, JPG ou JPEG.'
  }

  return undefined
}

export function DocumentsPage() {
  const { currentOrganization } = useCurrentOrganization()
  return <OrganizationDocumentsPage key={currentOrganization.id} />
}

function OrganizationDocumentsPage() {
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
  const currentOrganizationIdRef = useRef(currentOrganization.id)
  const mountedRef = useRef(true)
  const uploadOrganizationIdRef = useRef<string | undefined>(undefined)
  const uploadControllerRef = useRef<AbortController | undefined>(undefined)
  const uploadOperationRef = useRef(0)
  const isUploadingRef = useRef(false)
  const [isUploadOpen, setIsUploadOpen] = useState(false)
  const [uploadOrganizationId, setUploadOrganizationId] = useState<string>()
  const [uploadFormKey, setUploadFormKey] = useState(0)
  const [uploadFile, setUploadFile] = useState<File>()
  const [fileError, setFileError] = useState<string>()
  const [uploadClassification, setUploadClassification] =
    useState<UploadClassification>('general')
  const [selectedUploadClient, setSelectedUploadClient] =
    useState<ActiveClientLookupItem>()
  const [selectedUploadProcess, setSelectedUploadProcess] =
    useState<LegalProcessLookupItem>()
  const [isUploadClientLookupOpen, setIsUploadClientLookupOpen] =
    useState(false)
  const [isUploadProcessLookupOpen, setIsUploadProcessLookupOpen] =
    useState(false)
  const [classificationError, setClassificationError] = useState<string>()
  const [uploadFailure, setUploadFailure] = useState<UploadFailure>()
  const [uploadError, setUploadError] = useState<string>()
  const [uploadSuccess, setUploadSuccess] = useState<{
    readonly organizationId: string
    readonly message: string
  }>()
  const [isUploading, setIsUploading] = useState(false)

  function clearUploadFormState() {
    setIsUploadOpen(false)
    setUploadOrganizationId(undefined)
    setUploadFile(undefined)
    setFileError(undefined)
    setUploadClassification('general')
    setSelectedUploadClient(undefined)
    setSelectedUploadProcess(undefined)
    setIsUploadClientLookupOpen(false)
    setIsUploadProcessLookupOpen(false)
    setClassificationError(undefined)
    setUploadFailure(undefined)
    setUploadError(undefined)
  }

  useEffect(
    () => {
      mountedRef.current = true

      return () => {
        mountedRef.current = false
        uploadOperationRef.current += 1
        uploadControllerRef.current?.abort()
      }
    },
    [],
  )

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

  function openUpload() {
    uploadOrganizationIdRef.current = currentOrganization.id
    setUploadOrganizationId(currentOrganization.id)
    isUploadingRef.current = false
    setIsUploading(false)
    setUploadFormKey((value) => value + 1)
    setUploadFile(undefined)
    setFileError(undefined)
    setUploadClassification('general')
    setSelectedUploadClient(undefined)
    setSelectedUploadProcess(undefined)
    setIsUploadClientLookupOpen(false)
    setIsUploadProcessLookupOpen(false)
    setClassificationError(undefined)
    setUploadFailure(undefined)
    setUploadError(undefined)
    setUploadSuccess(undefined)
    setIsUploadOpen(true)
  }

  function closeUpload() {
    if (isUploadingRef.current) return
    uploadOrganizationIdRef.current = undefined
    clearUploadFormState()
  }

  function changeUploadClassification(classification: UploadClassification) {
    setUploadClassification(classification)
    setSelectedUploadClient(undefined)
    setSelectedUploadProcess(undefined)
    setIsUploadClientLookupOpen(false)
    setIsUploadProcessLookupOpen(false)
    setClassificationError(undefined)
    if (uploadFailure !== 'outcome-unknown') {
      setUploadFailure(undefined)
      setUploadError(undefined)
    }
  }

  async function submitUpload(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (isUploadingRef.current || uploadFailure === 'outcome-unknown') return

    const organizationId = uploadOrganizationIdRef.current
    let isValid = true

    if (!uploadFile) {
      setFileError('Selecione um arquivo para enviar.')
      isValid = false
    } else {
      const validationError = validateUploadFile(uploadFile)
      setFileError(validationError)
      if (validationError) isValid = false
    }

    if (uploadClassification === 'client' && !selectedUploadClient) {
      setClassificationError('Selecione um cliente ativo.')
      isValid = false
    } else if (uploadClassification === 'process' && !selectedUploadProcess) {
      setClassificationError('Selecione um processo.')
      isValid = false
    } else {
      setClassificationError(undefined)
    }

    if (
      !isValid ||
      !uploadFile ||
      !organizationId ||
      organizationId !== currentOrganization.id
    ) {
      return
    }

    let classification: LegalDocumentUploadClassification
    if (uploadClassification === 'client' && selectedUploadClient) {
      classification = { kind: 'client', clientId: selectedUploadClient.id }
    } else if (uploadClassification === 'process' && selectedUploadProcess) {
      classification = { kind: 'process', processId: selectedUploadProcess.id }
    } else {
      classification = { kind: 'general' }
    }

    const operationId = ++uploadOperationRef.current
    const controller = new AbortController()
    uploadControllerRef.current = controller
    isUploadingRef.current = true
    setIsUploading(true)
    setUploadFailure(undefined)
    setUploadError(undefined)
    setUploadSuccess(undefined)

    const isCurrentOperation = () =>
      mountedRef.current &&
      !controller.signal.aborted &&
      operationId === uploadOperationRef.current &&
      uploadOrganizationIdRef.current === organizationId &&
      currentOrganizationIdRef.current === organizationId

    try {
      await uploadDocument(
        organizationId,
        uploadFile,
        classification,
        handleUnauthorized,
        controller.signal,
      )

      if (!isCurrentOperation()) return

      uploadControllerRef.current = undefined
      isUploadingRef.current = false
      setIsUploading(false)
      uploadOrganizationIdRef.current = undefined
      clearUploadFormState()
      setUploadSuccess({
        organizationId,
        message: 'Documento enviado com sucesso.',
      })
      setRefreshVersion((version) => version + 1)
    } catch (error) {
      if (
        !isCurrentOperation() ||
        isAbortError(error) ||
        (error instanceof DocumentRequestError &&
          error.failure === 'unauthorized')
      ) {
        return
      }

      if (error instanceof DocumentRequestError) {
        if (error.failure === 'forbidden') {
          setUploadFailure('forbidden')
          setUploadError(
            'Você não tem permissão para enviar documentos nesta organização.',
          )
        } else if (error.failure === 'not-found') {
          if (uploadClassification === 'client') {
            setSelectedUploadClient(undefined)
          } else if (uploadClassification === 'process') {
            setSelectedUploadProcess(undefined)
          }
          setUploadFailure('not-found')
          setUploadError(
            'O cliente ou processo selecionado não está mais disponível.',
          )
        } else if (error.failure === 'bad-request') {
          setUploadFailure('validation')
          setUploadError(
            'O arquivo ou a classificação não pôde ser aceito. Revise os dados e tente novamente.',
          )
        } else if (error.failure === 'too-large') {
          setUploadFailure('too-large')
          setUploadError('O arquivo ou a requisição excede o limite de 25 MiB.')
        } else if (error.failure === 'unavailable') {
          setUploadFailure('unavailable')
          setUploadError(
            'O serviço de envio está temporariamente indisponível. Tente novamente mais tarde.',
          )
        } else if (error.failure === 'outcome-unknown') {
          setUploadFailure('outcome-unknown')
          setUploadError(
            'O envio pode ter sido concluído, mas o resultado é incerto. Não envie novamente agora: atualize e confira Documentos antes de tentar outra vez.',
          )
        } else {
          setUploadFailure('unexpected')
          setUploadError(
            'Não foi possível enviar o documento. Tente novamente mais tarde.',
          )
        }
      } else {
        setUploadFailure('unexpected')
        setUploadError(
          'Não foi possível enviar o documento. Tente novamente mais tarde.',
        )
      }
    } finally {
      if (isCurrentOperation()) {
        uploadControllerRef.current = undefined
        isUploadingRef.current = false
        setIsUploading(false)
      }
    }
  }

  const currentClient = selectedClient?.id === clientId ? selectedClient : undefined
  const currentProcess =
    selectedProcess?.id === processId ? selectedProcess : undefined
  const isFiltered = search.length > 0 || Boolean(clientId) || Boolean(processId)

  return (
    <section className="documents-page" aria-labelledby="documents-title">
      <div className="documents-header workspace-page-header">
        <div className="workspace-page-heading">
          <p className="eyebrow workspace-page-eyebrow">ACERVO JURÍDICO</p>
          <h2 className="workspace-page-title" id="documents-title">Documentos</h2>
          <p className="documents-description workspace-page-subtitle">
            Consulte e baixe os documentos privados desta organização.
          </p>
        </div>
        {!isUploadOpen ? (
          <button className="primary-button" type="button" onClick={openUpload}>
            Enviar documento
          </button>
        ) : null}
      </div>

      {uploadSuccess?.organizationId === currentOrganization.id ? (
        <p className="success-message" role="status">{uploadSuccess.message}</p>
      ) : null}

      {isUploadOpen &&
      uploadOrganizationId === currentOrganization.id ? (
        <div className="document-upload-panel">
          <h3>Enviar documento</h3>
          <form
            className="document-upload-form"
            onSubmit={submitUpload}
            aria-busy={isUploading}
          >
            <label htmlFor="document-upload-file">Arquivo</label>
            <input
              key={uploadFormKey}
              id="document-upload-file"
              type="file"
              accept=".pdf,.docx,.xlsx,.png,.jpg,.jpeg"
              disabled={isUploading}
              aria-invalid={fileError ? true : undefined}
              aria-describedby="document-upload-file-help document-upload-file-selection"
              onChange={(event) => {
                const file = event.target.files?.[0]
                setUploadFile(file)
                setFileError(file ? validateUploadFile(file) : undefined)
                if (uploadFailure !== 'outcome-unknown') {
                  setUploadFailure(undefined)
                  setUploadError(undefined)
                }
              }}
            />
            <p id="document-upload-file-help" className="form-help">
              PDF, DOCX, XLSX, PNG, JPG ou JPEG, com até 25 MiB. A validação final é feita pelo servidor.
            </p>
            <p id="document-upload-file-selection" className="document-upload-selection" role="status">
              {uploadFile ? (
                <>Arquivo selecionado: <strong>{uploadFile.name}</strong></>
              ) : 'Nenhum arquivo selecionado.'}
            </p>
            {fileError ? <p className="form-error" role="alert">{fileError}</p> : null}

            <fieldset className="document-upload-classification">
              <legend>Classificação</legend>
              <label>
                <input
                  type="radio"
                  name="document-classification"
                  value="general"
                  checked={uploadClassification === 'general'}
                  onChange={() => changeUploadClassification('general')}
                  disabled={isUploading}
                />
                Geral
              </label>
              <label>
                <input
                  type="radio"
                  name="document-classification"
                  value="client"
                  checked={uploadClassification === 'client'}
                  onChange={() => changeUploadClassification('client')}
                  disabled={isUploading}
                />
                Cliente
              </label>
              <label>
                <input
                  type="radio"
                  name="document-classification"
                  value="process"
                  checked={uploadClassification === 'process'}
                  onChange={() => changeUploadClassification('process')}
                  disabled={isUploading}
                />
                Processo
              </label>
            </fieldset>

            {uploadClassification === 'client' ? (
              <div className="document-upload-relation">
                <p>
                  Cliente: <strong>{selectedUploadClient?.name ?? 'não selecionado'}</strong>
                </p>
                <button
                  className="secondary-button"
                  type="button"
                  onClick={() => setIsUploadClientLookupOpen((open) => !open)}
                  disabled={isUploading}
                >
                  {isUploadClientLookupOpen ? 'Fechar busca de cliente' : 'Selecionar cliente'}
                </button>
                {isUploadClientLookupOpen ? (
                  <TaskLookupPicker
                    organizationId={currentOrganization.id}
                    searchLabel="Buscar cliente para o documento"
                    resultsLabel="Clientes encontrados para o documento"
                    loadingMessage="Carregando clientes..."
                    emptyMessage="Não há clientes ativos disponíveis."
                    noResultsMessage="Nenhum cliente encontrado para esta busca."
                    errorMessage="Não foi possível carregar os clientes. Tente novamente."
                    selectedId={selectedUploadClient?.id}
                    disabled={isUploading}
                    load={lookupActiveClients}
                    onUnauthorized={handleUnauthorized}
                    onSelect={(item) => {
                      setSelectedUploadClient(item)
                      setIsUploadClientLookupOpen(false)
                      setClassificationError(undefined)
                      if (uploadFailure !== 'outcome-unknown') {
                        setUploadFailure(undefined)
                        setUploadError(undefined)
                      }
                    }}
                    renderItem={(item) => <span>{item.name}</span>}
                  />
                ) : null}
              </div>
            ) : null}

            {uploadClassification === 'process' ? (
              <div className="document-upload-relation">
                <p>
                  Processo:{' '}
                  <strong>
                    {selectedUploadProcess
                      ? `${selectedUploadProcess.title} — ${selectedUploadProcess.clientName}`
                      : 'não selecionado'}
                  </strong>
                </p>
                <button
                  className="secondary-button"
                  type="button"
                  onClick={() => setIsUploadProcessLookupOpen((open) => !open)}
                  disabled={isUploading}
                >
                  {isUploadProcessLookupOpen ? 'Fechar busca de processo' : 'Selecionar processo'}
                </button>
                {isUploadProcessLookupOpen ? (
                  <TaskLookupPicker
                    organizationId={currentOrganization.id}
                    searchLabel="Buscar processo para o documento"
                    resultsLabel="Processos encontrados para o documento"
                    loadingMessage="Carregando processos..."
                    emptyMessage="Não há processos disponíveis."
                    noResultsMessage="Nenhum processo encontrado para esta busca."
                    errorMessage="Não foi possível carregar os processos. Tente novamente."
                    selectedId={selectedUploadProcess?.id}
                    disabled={isUploading}
                    load={lookupLegalProcesses}
                    onUnauthorized={handleUnauthorized}
                    onSelect={(item) => {
                      setSelectedUploadProcess(item)
                      setIsUploadProcessLookupOpen(false)
                      setClassificationError(undefined)
                      if (uploadFailure !== 'outcome-unknown') {
                        setUploadFailure(undefined)
                        setUploadError(undefined)
                      }
                    }}
                    renderItem={(item) => (
                      <><span>{item.title}</span><small>Cliente: {item.clientName}</small></>
                    )}
                  />
                ) : null}
              </div>
            ) : null}

            {classificationError ? (
              <p className="form-error" role="alert">{classificationError}</p>
            ) : null}

            {uploadError ? (
              <div className="document-upload-error">
                <p className="form-error" role="alert">{uploadError}</p>
                {uploadFailure === 'forbidden' ? (
                  <button
                    className="text-button"
                    type="button"
                    onClick={refreshOrganizations}
                    disabled={isUploading}
                  >
                    Atualizar acesso
                  </button>
                ) : null}
                {uploadFailure === 'outcome-unknown' ? (
                  <button
                    className="secondary-button"
                    type="button"
                    onClick={() => {
                      setSearchParams({})
                      setRefreshVersion((version) => version + 1)
                    }}
                  >
                    Atualizar lista de documentos
                  </button>
                ) : null}
              </div>
            ) : null}

            <div className="document-upload-actions">
              <button
                className="secondary-button"
                type="button"
                onClick={closeUpload}
                disabled={isUploading}
              >
                Cancelar
              </button>
              <button
                className="primary-button"
                type="submit"
                disabled={isUploading || uploadFailure === 'outcome-unknown'}
              >
                {isUploading ? 'Enviando...' : 'Enviar documento'}
              </button>
            </div>
          </form>
        </div>
      ) : null}

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
