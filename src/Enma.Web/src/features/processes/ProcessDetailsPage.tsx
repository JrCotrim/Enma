import { useEffect, useRef, useState, type FormEvent } from 'react'
import { Link, useParams } from 'react-router-dom'
import { useAuth } from '../authentication/AuthContext'
import {
  formatDocumentCreatedAt,
  formatDocumentFileType,
  formatDocumentSize,
} from '../documents/documentFormatting'
import {
  DocumentRequestError,
  deleteDocument,
  getDocumentDownloadUrl,
  isDocumentPreviewSupported,
  listDocuments,
} from '../documents/documentService'
import type {
  LegalDocumentListResponse,
  LegalDocumentMetadata,
} from '../documents/documentTypes'
import { DocumentPreviewDialog } from '../documents/DocumentPreviewDialog'
import { DocumentDeleteDialog } from '../documents/DocumentDeleteDialog'
import {
  useCurrentOrganization,
  useOrganizationDiscovery,
} from '../organizations/OrganizationContext'
import { formatLegalProcessCreatedAt } from './legalProcessFormatting'
import {
  getLegalProcess,
  LegalProcessRequestError,
  updateLegalProcess,
} from './legalProcessService'
import type { LegalProcess } from './legalProcessTypes'

const processIdPattern =
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i
const maximumTitleLength = 150
const relatedDocumentsPageSize = 5
const genericDetailError =
  'Não foi possível carregar o processo. Tente novamente.'
const unavailableMessage = 'Processo não encontrado ou indisponível.'
const mutationErrorMessage =
  'Não foi possível salvar o processo. Atualize os dados antes de tentar novamente.'
const mutationValidationMessage =
  'Não foi possível validar a alteração. Verifique o título e tente novamente.'
const mutationPermissionMessage =
  'Você não tem permissão para alterar este processo.'

type DetailState =
  | { readonly status: 'loading'; readonly scope: string }
  | {
      readonly status: 'success'
      readonly scope: string
      readonly legalProcess: LegalProcess
    }
  | { readonly status: 'forbidden'; readonly scope: string }
  | { readonly status: 'not-found'; readonly scope: string }
  | { readonly status: 'error'; readonly scope: string }

type RelatedDocumentsState =
  | { readonly status: 'loading'; readonly scope: string }
  | {
      readonly status: 'success'
      readonly scope: string
      readonly response: LegalDocumentListResponse
    }
  | { readonly status: 'forbidden' | 'error'; readonly scope: string }

function isAbortError(error: unknown): boolean {
  return error instanceof DOMException && error.name === 'AbortError'
}

function belongsToProcess(
  document: LegalDocumentMetadata,
  processId: string,
): boolean {
  return document.processId?.toLowerCase() === processId.toLowerCase()
}

export function ProcessDetailsPage() {
  const { processId } = useParams()
  const { currentOrganization } = useCurrentOrganization()

  return (
    <ProcessDetailsContent
      key={`${currentOrganization.id}:${processId ?? ''}`}
      processId={processId}
    />
  )
}

function ProcessDetailsContent({ processId }: { readonly processId?: string }) {
  const { currentOrganization } = useCurrentOrganization()
  const { refreshOrganizations } = useOrganizationDiscovery()
  const { handleUnauthorized } = useAuth()
  const routeProcessId = processIdPattern.test(processId ?? '')
    ? processId
    : undefined
  const resourceIdentity = `${currentOrganization.id}:${processId ?? ''}`
  const [refreshVersion, setRefreshVersion] = useState(0)
  const requestScope = `${resourceIdentity}:${refreshVersion}`
  const [detailState, setDetailState] = useState<DetailState>({
    status: 'loading',
    scope: requestScope,
  })
  const detailRequestVersionRef = useRef(0)
  const mutationVersionRef = useRef(0)
  const mutationControllerRef = useRef<AbortController | undefined>(undefined)
  const isMutatingRef = useRef(false)
  const [isEditing, setIsEditing] = useState(false)
  const [editTitle, setEditTitle] = useState('')
  const [editTitleError, setEditTitleError] = useState<string>()
  const [mutationError, setMutationError] = useState<string>()
  const [successMessage, setSuccessMessage] = useState<string>()
  const [isMutating, setIsMutating] = useState(false)
  const canUpdateProcess =
    currentOrganization.role === 'Owner' ||
    currentOrganization.role === 'Administrator'

  useEffect(() => {
    if (!routeProcessId) {
      return
    }

    const controller = new AbortController()
    const requestVersion = ++detailRequestVersionRef.current

    void getLegalProcess(
      currentOrganization.id,
      routeProcessId,
      handleUnauthorized,
      controller.signal,
    )
      .then((legalProcess) => {
        if (
          !controller.signal.aborted &&
          requestVersion === detailRequestVersionRef.current
        ) {
          setDetailState({
            status: 'success',
            scope: requestScope,
            legalProcess,
          })
        }
      })
      .catch((error: unknown) => {
        if (
          controller.signal.aborted ||
          requestVersion !== detailRequestVersionRef.current ||
          isAbortError(error) ||
          (error instanceof LegalProcessRequestError &&
            error.failure === 'unauthorized')
        ) {
          return
        }

        setDetailState({
          status:
            error instanceof LegalProcessRequestError
              ? error.failure === 'forbidden'
                ? 'forbidden'
                : error.failure === 'not-found'
                  ? 'not-found'
                  : 'error'
              : 'error',
          scope: requestScope,
        })
      })

    return () => controller.abort()
  }, [
    currentOrganization.id,
    handleUnauthorized,
    refreshVersion,
    requestScope,
    resourceIdentity,
    routeProcessId,
  ])

  useEffect(
    () => () => {
      mutationControllerRef.current?.abort()
    },
    [],
  )

  const currentDetailState: DetailState =
    detailState.scope === requestScope
      ? detailState
      : { status: 'loading', scope: requestScope }

  function startEditing(legalProcess: LegalProcess) {
    setEditTitle(legalProcess.title)
    setEditTitleError(undefined)
    setMutationError(undefined)
    setSuccessMessage(undefined)
    setIsEditing(true)
  }

  function cancelEditing() {
    if (isMutatingRef.current) {
      return
    }

    setIsEditing(false)
    setEditTitleError(undefined)
    setMutationError(undefined)
  }

  async function handleEdit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()

    if (!routeProcessId || isMutatingRef.current) {
      return
    }

    const trimmedTitle = editTitle.trim()

    if (trimmedTitle.length === 0) {
      setEditTitleError('Informe o título do processo.')
      return
    }

    if (trimmedTitle.length > maximumTitleLength) {
      setEditTitleError(
        `O título deve ter no máximo ${maximumTitleLength} caracteres.`,
      )
      return
    }

    setEditTitleError(undefined)
    setMutationError(undefined)
    setSuccessMessage(undefined)

    const mutationVersion = ++mutationVersionRef.current
    const controller = new AbortController()
    mutationControllerRef.current = controller
    isMutatingRef.current = true
    setIsMutating(true)

    try {
      await updateLegalProcess(
        currentOrganization.id,
        routeProcessId,
        trimmedTitle,
        handleUnauthorized,
        controller.signal,
      )

      if (
        controller.signal.aborted ||
        mutationVersion !== mutationVersionRef.current
      ) {
        return
      }

      setIsEditing(false)
      setSuccessMessage('Processo atualizado com sucesso.')
      setRefreshVersion((version) => version + 1)
    } catch (error) {
      if (
        controller.signal.aborted ||
        mutationVersion !== mutationVersionRef.current ||
        isAbortError(error) ||
        (error instanceof LegalProcessRequestError &&
          error.failure === 'unauthorized')
      ) {
        return
      }

      if (
        error instanceof LegalProcessRequestError &&
        error.failure === 'not-found'
      ) {
        setDetailState({ status: 'not-found', scope: requestScope })
        setIsEditing(false)
      } else if (
        error instanceof LegalProcessRequestError &&
        error.failure === 'forbidden'
      ) {
        setMutationError(mutationPermissionMessage)
      } else if (
        error instanceof LegalProcessRequestError &&
        error.failure === 'bad-request'
      ) {
        setMutationError(mutationValidationMessage)
      } else {
        setMutationError(mutationErrorMessage)
      }
    } finally {
      if (
        !controller.signal.aborted &&
        mutationVersion === mutationVersionRef.current
      ) {
        mutationControllerRef.current = undefined
        isMutatingRef.current = false
        setIsMutating(false)
      }
    }
  }

  const backLink = (
    <Link
      className="home-link"
      to={`/organizations/${currentOrganization.id}/processes`}
    >
      Voltar para processos
    </Link>
  )

  if (!routeProcessId || currentDetailState.status === 'not-found') {
    return (
      <section
        className="process-details-page"
        aria-labelledby="process-details-title"
      >
        <div className="processes-state" role="alert">
          <h2 id="process-details-title">Processo indisponível</h2>
          <p>{unavailableMessage}</p>
          <div className="processes-state-actions">{backLink}</div>
        </div>
      </section>
    )
  }

  if (currentDetailState.status === 'loading') {
    return (
      <section
        className="process-details-page"
        aria-labelledby="process-details-title"
      >
        <h2 id="process-details-title" className="visually-hidden">
          Detalhes do processo
        </h2>
        <p className="processes-state" role="status">
          Carregando processo...
        </p>
      </section>
    )
  }

  if (currentDetailState.status === 'forbidden') {
    return (
      <section
        className="process-details-page"
        aria-labelledby="process-details-title"
      >
        <div className="processes-state" role="alert">
          <h2 id="process-details-title">Acesso indisponível</h2>
          <p>Não foi possível acessar este processo.</p>
          <div className="processes-state-actions">
            <button
              className="secondary-button"
              type="button"
              onClick={refreshOrganizations}
            >
              Atualizar acesso
            </button>
            {backLink}
          </div>
        </div>
      </section>
    )
  }

  if (currentDetailState.status === 'error') {
    return (
      <section
        className="process-details-page"
        aria-labelledby="process-details-title"
      >
        <div className="processes-state" role="alert">
          <h2 id="process-details-title">Detalhes do processo</h2>
          <p>{genericDetailError}</p>
          <div className="processes-state-actions">
            <button
              className="secondary-button"
              type="button"
              onClick={() => setRefreshVersion((version) => version + 1)}
            >
              Tentar novamente
            </button>
            {backLink}
          </div>
        </div>
      </section>
    )
  }

  const legalProcess = currentDetailState.legalProcess

  return (
    <section
      className="process-details-page"
      aria-labelledby="process-details-title"
    >
      {backLink}
      <header className="process-details-header">
        <div className="process-details-heading">
          <h2 id="process-details-title">{legalProcess.title}</h2>
          <dl className="process-details-metadata">
            <div>
              <dt>Cliente</dt>
              <dd>{legalProcess.clientName}</dd>
            </div>
            <div>
              <dt>Criado em</dt>
              <dd>
                <time dateTime={legalProcess.createdAt}>
                  {formatLegalProcessCreatedAt(legalProcess.createdAt)}
                </time>
              </dd>
            </div>
          </dl>
        </div>
        {canUpdateProcess && !isEditing ? (
          <div className="process-detail-actions">
            <button
              className="secondary-button"
              type="button"
              onClick={() => startEditing(legalProcess)}
            >
              Editar processo
            </button>
          </div>
        ) : null}
      </header>

      {successMessage ? (
        <p className="success-message" role="status">
          {successMessage}
        </p>
      ) : null}
      {mutationError ? (
        <div className="process-mutation-error">
          <p className="form-error" role="alert">
            {mutationError}
          </p>
          {mutationError === mutationPermissionMessage ? (
            <button
              className="text-button"
              type="button"
              onClick={refreshOrganizations}
              disabled={isMutating}
            >
              Atualizar acesso
            </button>
          ) : (
            <button
              className="text-button"
              type="button"
              onClick={() => setRefreshVersion((version) => version + 1)}
              disabled={isMutating}
            >
              Atualizar dados
            </button>
          )}
        </div>
      ) : null}

      {isEditing ? (
        <form
          className="process-create-panel process-create-form process-edit-form"
          onSubmit={handleEdit}
          aria-busy={isMutating}
        >
          <h3>Editar processo</h3>
          <label htmlFor="process-edit-title">Título</label>
          <input
            id="process-edit-title"
            name="title"
            value={editTitle}
            maxLength={maximumTitleLength}
            onChange={(event) => {
              setEditTitle(event.target.value)
              setEditTitleError(undefined)
            }}
            aria-describedby={
              editTitleError ? 'process-edit-title-error' : undefined
            }
            aria-invalid={editTitleError ? true : undefined}
            autoFocus
            required
          />
          {editTitleError ? (
            <p
              id="process-edit-title-error"
              className="form-error"
              role="alert"
            >
              {editTitleError}
            </p>
          ) : null}
          <div className="process-form-actions">
            <button
              className="secondary-button"
              type="button"
              onClick={cancelEditing}
              disabled={isMutating}
            >
              Cancelar
            </button>
            <button
              className="primary-button"
              type="submit"
              disabled={isMutating}
            >
              {isMutating ? 'Salvando...' : 'Salvar alterações'}
            </button>
          </div>
        </form>
      ) : null}

      <ProcessDocumentsSection processId={routeProcessId} />
    </section>
  )
}

function ProcessDocumentsSection({
  processId,
}: {
  readonly processId: string
}) {
  const { currentOrganization } = useCurrentOrganization()
  const { refreshOrganizations } = useOrganizationDiscovery()
  const { handleUnauthorized } = useAuth()
  const [refreshVersion, setRefreshVersion] = useState(0)
  const requestScope = `${currentOrganization.id}:${processId}:${refreshVersion}`
  const [state, setState] = useState<RelatedDocumentsState>({
    status: 'loading',
    scope: requestScope,
  })
  const requestVersionRef = useRef(0)
  const [previewDocument, setPreviewDocument] =
    useState<LegalDocumentMetadata>()
  const [previewReturnFocus, setPreviewReturnFocus] =
    useState<HTMLButtonElement | null>(null)
  const [deleteTarget, setDeleteTarget] =
    useState<LegalDocumentMetadata>()
  const [deleteReturnFocus, setDeleteReturnFocus] =
    useState<HTMLButtonElement | null>(null)
  const [isDeleting, setIsDeleting] = useState(false)
  const [deleteError, setDeleteError] = useState<string>()
  const [deleteSuccess, setDeleteSuccess] = useState<string>()

  useEffect(() => {
    const controller = new AbortController()
    const requestVersion = ++requestVersionRef.current

    void listDocuments(
      currentOrganization.id,
      {
        processId,
        pageNumber: 1,
        pageSize: relatedDocumentsPageSize,
      },
      handleUnauthorized,
      controller.signal,
    )
      .then((response) => {
        if (
          controller.signal.aborted ||
          requestVersion !== requestVersionRef.current
        ) {
          return
        }

        if (
          !response.items.every((document) =>
            belongsToProcess(document, processId),
          )
        ) {
          setState({ status: 'error', scope: requestScope })
          return
        }

        setState({ status: 'success', scope: requestScope, response })
      })
      .catch((error: unknown) => {
        if (
          controller.signal.aborted ||
          requestVersion !== requestVersionRef.current ||
          isAbortError(error) ||
          (error instanceof DocumentRequestError &&
            error.failure === 'unauthorized')
        ) {
          return
        }

        setState({
          status:
            error instanceof DocumentRequestError &&
            error.failure === 'forbidden'
              ? 'forbidden'
              : 'error',
          scope: requestScope,
        })
      })

    return () => controller.abort()
  }, [
    currentOrganization.id,
    handleUnauthorized,
    processId,
    refreshVersion,
    requestScope,
  ])

  const currentState: RelatedDocumentsState =
    state.scope === requestScope
      ? state
      : { status: 'loading', scope: requestScope }
  const documentsUrl = `/organizations/${currentOrganization.id}/documents?processId=${encodeURIComponent(processId)}`

  async function confirmDeletion() {
    if (!deleteTarget || isDeleting) return

    setIsDeleting(true)
    setDeleteError(undefined)
    try {
      await deleteDocument(
        currentOrganization.id,
        deleteTarget.id,
        handleUnauthorized,
      )
      setPreviewDocument((current) =>
        current?.id === deleteTarget.id ? undefined : current,
      )
      setDeleteSuccess(
        `Documento “${deleteTarget.originalFileName}” excluído com sucesso.`,
      )
      setDeleteTarget(undefined)
      setRefreshVersion((version) => version + 1)
    } catch (error) {
      if (
        error instanceof DocumentRequestError &&
        error.failure === 'unauthorized'
      ) return

      setDeleteError(
        error instanceof DocumentRequestError &&
        error.failure === 'forbidden'
          ? 'Você não tem mais permissão para excluir este documento.'
          : error instanceof DocumentRequestError &&
              error.failure === 'not-found'
            ? 'O documento não está mais disponível. Atualize a lista.'
            : 'Não foi possível excluir o documento. Tente novamente.',
      )
    } finally {
      setIsDeleting(false)
    }
  }

  return (
    <section
      className="process-documents"
      aria-labelledby="process-documents-title"
      aria-busy={currentState.status === 'loading'}
    >
      <div className="process-documents-header">
        <div>
          <h3 id="process-documents-title">Documentos vinculados</h3>
          <p>Arquivos mais recentes deste processo.</p>
        </div>
        <Link className="process-documents-all-link" to={documentsUrl}>
          Ver todos
        </Link>
      </div>

      {deleteSuccess ? (
        <p className="success-message" role="status">{deleteSuccess}</p>
      ) : null}

      {currentState.status === 'loading' ? (
        <p className="process-documents-state" role="status">
          Carregando documentos vinculados...
        </p>
      ) : null}

      {currentState.status === 'forbidden' ? (
        <div className="process-documents-state" role="alert">
          <p>Não foi possível acessar os documentos deste processo.</p>
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
        <div className="process-documents-state" role="alert">
          <p>Não foi possível carregar os documentos vinculados.</p>
          <button
            className="secondary-button"
            type="button"
            onClick={() => setRefreshVersion((version) => version + 1)}
          >
            Tentar novamente
          </button>
        </div>
      ) : null}

      {currentState.status === 'success' &&
      currentState.response.items.length === 0 ? (
        <p className="process-documents-state" role="status">
          Nenhum documento vinculado a este processo.
        </p>
      ) : null}

      {currentState.status === 'success' &&
      currentState.response.items.length > 0 ? (
        <ul className="process-documents-list">
          {currentState.response.items.map((document) => {
            const detailsUrl = `/organizations/${currentOrganization.id}/documents/${document.id}`

            return (
              <li key={document.id}>
                <div className="process-document-content">
                  <span className="process-document-icon" aria-hidden="true">
                    <svg viewBox="0 0 24 24" focusable="false">
                      <path d="M7.5 3.75h6.75l3.75 3.75v12.75H7.5z" />
                      <path d="M14.25 3.75V7.5H18M10 12h5M10 15.5h5" />
                    </svg>
                  </span>
                  <div className="process-document-summary">
                    <Link className="process-document-name" to={detailsUrl}>
                      {document.originalFileName}
                    </Link>
                    <div className="process-document-metadata">
                      <span>{formatDocumentFileType(document.contentType)}</span>
                      <span>{formatDocumentSize(document.sizeBytes)}</span>
                      <time dateTime={document.createdAt}>
                        {formatDocumentCreatedAt(document.createdAt)}
                      </time>
                    </div>
                  </div>
                </div>
                <div className="process-document-actions">
                  <Link to={detailsUrl}>Ver detalhes</Link>
                  {isDocumentPreviewSupported(document.contentType) ? (
                    <button
                      type="button"
                      onClick={(event) => {
                        setPreviewReturnFocus(event.currentTarget)
                        setPreviewDocument(document)
                      }}
                    >
                      Visualizar
                    </button>
                  ) : null}
                  <a
                    href={getDocumentDownloadUrl(
                      currentOrganization.id,
                      document.id,
                    )}
                  >
                    Baixar
                  </a>
                  {currentOrganization.role !== 'Member' ? (
                    <button
                      type="button"
                      onClick={(event) => {
                        setPreviewDocument(undefined)
                        setDeleteError(undefined)
                        setDeleteSuccess(undefined)
                        setDeleteReturnFocus(event.currentTarget)
                        setDeleteTarget(document)
                      }}
                    >
                      Excluir
                    </button>
                  ) : null}
                </div>
              </li>
            )
          })}
        </ul>
      ) : null}


      {previewDocument ? (
        <DocumentPreviewDialog
          document={previewDocument}
          organizationId={currentOrganization.id}
          returnFocus={previewReturnFocus}
          onClose={() => setPreviewDocument(undefined)}
        />
      ) : null}
      {deleteTarget ? (
        <DocumentDeleteDialog
          document={deleteTarget}
          returnFocus={deleteReturnFocus}
          isDeleting={isDeleting}
          error={deleteError}
          onCancel={() => {
            if (!isDeleting) setDeleteTarget(undefined)
          }}
          onConfirm={() => void confirmDeletion()}
        />
      ) : null}
    </section>
  )
}
