import { useEffect, useRef, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { useAuth } from '../authentication/AuthContext'
import { isValidGuid } from '../deadlines/legalDeadlineFormatting'
import {
  useCurrentOrganization,
  useOrganizationDiscovery,
} from '../organizations/OrganizationContext'
import {
  formatDocumentCreatedAt,
  formatDocumentFileType,
  formatDocumentSize,
  getDocumentContextLabel,
} from './documentFormatting'
import {
  DocumentRequestError,
  getDocument,
  getDocumentDownloadUrl,
} from './documentService'
import type { LegalDocumentMetadata } from './documentTypes'

type DetailState =
  | { readonly status: 'loading'; readonly scope: string }
  | { readonly status: 'success'; readonly scope: string; readonly document: LegalDocumentMetadata }
  | { readonly status: 'not-found'; readonly scope: string }
  | { readonly status: 'forbidden'; readonly scope: string }
  | { readonly status: 'error'; readonly scope: string }

function isAbortError(error: unknown): boolean {
  return error instanceof DOMException && error.name === 'AbortError'
}

export function DocumentDetailsPage() {
  const { currentOrganization } = useCurrentOrganization()
  const { refreshOrganizations } = useOrganizationDiscovery()
  const { handleUnauthorized } = useAuth()
  const { documentId = '' } = useParams()
  const [refreshVersion, setRefreshVersion] = useState(0)
  const scope = `${currentOrganization.id}:${documentId}:${refreshVersion}`
  const [state, setState] = useState<DetailState>({ status: 'loading', scope })
  const requestIdRef = useRef(0)
  const hasValidDocumentId = isValidGuid(documentId)

  useEffect(() => {
    if (!hasValidDocumentId) return

    const controller = new AbortController()
    const requestId = ++requestIdRef.current

    void getDocument(
      currentOrganization.id,
      documentId,
      handleUnauthorized,
      controller.signal,
    )
      .then((document) => {
        if (!controller.signal.aborted && requestId === requestIdRef.current) {
          setState({ status: 'success', scope, document })
        }
      })
      .catch((error: unknown) => {
        if (
          controller.signal.aborted ||
          requestId !== requestIdRef.current ||
          isAbortError(error) ||
          (error instanceof DocumentRequestError && error.failure === 'unauthorized')
        ) return

        setState({
          status:
            error instanceof DocumentRequestError
              ? error.failure === 'not-found' || error.failure === 'bad-request'
                ? 'not-found'
                : error.failure === 'forbidden'
                  ? 'forbidden'
                  : 'error'
              : 'error',
          scope,
        })
      })

    return () => controller.abort()
  }, [currentOrganization.id, documentId, handleUnauthorized, hasValidDocumentId, refreshVersion, scope])

  const currentState: DetailState =
    !hasValidDocumentId
      ? { status: 'not-found', scope }
      : state.scope === scope
        ? state
        : { status: 'loading', scope }

  if (currentState.status === 'loading') {
    return <section className="document-details-page" role="status"><p>Carregando documento...</p></section>
  }

  if (currentState.status === 'not-found') {
    return (
      <section className="document-details-page" role="alert">
        <h2>Documento não encontrado ou indisponível</h2>
        <p>O documento pode não existir ou não estar disponível neste contexto.</p>
        <Link to="../documents">Voltar para documentos</Link>
      </section>
    )
  }

  if (currentState.status === 'forbidden') {
    return (
      <section className="document-details-page" role="alert">
        <h2>Acesso ao documento indisponível</h2>
        <p>Seu acesso à organização pode ter mudado.</p>
        <button className="secondary-button" type="button" onClick={refreshOrganizations}>Atualizar acesso</button>
      </section>
    )
  }

  if (currentState.status === 'error') {
    return (
      <section className="document-details-page" role="alert">
        <h2>Não foi possível carregar o documento</h2>
        <p>Tente novamente. Nenhum detalhe interno foi exibido.</p>
        <button className="secondary-button" type="button" onClick={() => setRefreshVersion((version) => version + 1)}>Tentar novamente</button>
      </section>
    )
  }

  const document = currentState.document
  return (
    <section className="document-details-page" aria-labelledby="document-title">
      <div className="document-details-header">
        <div>
          <p className="eyebrow">Detalhes do documento</p>
          <h2 id="document-title">{document.originalFileName}</h2>
        </div>
        <div className="document-detail-actions">
          <Link className="secondary-button" to="../documents">Voltar</Link>
          <a className="primary-button" href={getDocumentDownloadUrl(currentOrganization.id, document.id)}>Baixar documento</a>
        </div>
      </div>

      <dl className="document-properties">
        <div><dt>Nome do arquivo</dt><dd>{document.originalFileName}</dd></div>
        <div><dt>Contexto</dt><dd>{getDocumentContextLabel(document)}</dd></div>
        <div><dt>Tipo</dt><dd>{formatDocumentFileType(document.contentType)}</dd></div>
        <div><dt>Tamanho</dt><dd>{formatDocumentSize(document.sizeBytes)}</dd></div>
        <div><dt>Adicionado em</dt><dd>{formatDocumentCreatedAt(document.createdAt)}</dd></div>
      </dl>
    </section>
  )
}
