import {
  useEffect,
  useRef,
  useState,
  type KeyboardEvent as ReactKeyboardEvent,
} from 'react'
import { createPortal } from 'react-dom'
import { useAuth } from '../authentication/AuthContext'
import {
  DocumentRequestError,
  getDocumentDownloadUrl,
  loadDocumentPreview,
} from './documentService'
import type { LegalDocumentMetadata } from './documentTypes'

interface DocumentPreviewDialogProps {
  readonly document: LegalDocumentMetadata
  readonly organizationId: string
  readonly returnFocus?: HTMLElement | null
  readonly onClose: () => void
}

type PreviewState =
  | { readonly status: 'loading'; readonly scope: string }
  | {
      readonly status: 'ready'
      readonly scope: string
      readonly objectUrl: string
    }
  | {
      readonly status: 'unsupported' | 'unavailable' | 'error'
      readonly scope: string
    }

function isAbortError(error: unknown): boolean {
  return error instanceof DOMException && error.name === 'AbortError'
}

export function DocumentPreviewDialog({
  document,
  organizationId,
  returnFocus,
  onClose,
}: DocumentPreviewDialogProps) {
  const { handleUnauthorized } = useAuth()
  const pdfFrameRef = useRef<HTMLIFrameElement>(null)
  const [retryVersion, setRetryVersion] = useState(0)
  const requestScope = `${organizationId}:${document.id}:${retryVersion}`
  const [state, setState] = useState<PreviewState>({
    status: 'loading',
    scope: requestScope,
  })

  useEffect(() => {
    const controller = new AbortController()
    let objectUrl: string | undefined
    let active = true

    void loadDocumentPreview(
      organizationId,
      document,
      handleUnauthorized,
      controller.signal,
    )
      .then((blob) => {
        if (!active || controller.signal.aborted) return
        objectUrl = URL.createObjectURL(blob)
        setState({ status: 'ready', scope: requestScope, objectUrl })
      })
      .catch((error: unknown) => {
        if (
          !active ||
          controller.signal.aborted ||
          isAbortError(error) ||
          (error instanceof DocumentRequestError &&
            error.failure === 'unauthorized')
        ) {
          return
        }

        setState({
          status:
            error instanceof DocumentRequestError
              ? error.failure === 'unsupported'
                ? 'unsupported'
                : error.failure === 'unavailable'
                  ? 'unavailable'
                  : 'error'
              : 'error',
          scope: requestScope,
        })
      })

    return () => {
      active = false
      controller.abort()
      if (objectUrl) URL.revokeObjectURL(objectUrl)
    }
  }, [document, handleUnauthorized, organizationId, requestScope])

  useEffect(() => {
    const previousOverflow = window.document.body.style.overflow

    window.document.body.style.overflow = 'hidden'

    return () => {
      window.document.body.style.overflow = previousOverflow
      if (returnFocus?.isConnected) returnFocus.focus()
    }
  }, [returnFocus])

  useEffect(() => {
    const handleEscape = (event: globalThis.KeyboardEvent) => {
      if (event.key !== 'Escape') return

      event.preventDefault()
      event.stopPropagation()
      onClose()
    }

    window.document.addEventListener('keydown', handleEscape, true)
    return () => {
      window.document.removeEventListener('keydown', handleEscape, true)
    }
  }, [onClose])

  useEffect(() => {
    if (
      state.status !== 'ready' ||
      state.scope !== requestScope ||
      document.contentType !== 'application/pdf'
    ) return

    const frameWindow = pdfFrameRef.current?.contentWindow
    if (!frameWindow) return

    const handleEmbeddedEscape = (event: globalThis.KeyboardEvent) => {
      if (event.key !== 'Escape') return

      event.preventDefault()
      event.stopPropagation()
      onClose()
    }

    try {
      frameWindow.addEventListener('keydown', handleEmbeddedEscape, true)
    } catch {
      return
    }

    return () => {
      try {
        frameWindow.removeEventListener('keydown', handleEmbeddedEscape, true)
      } catch {
        // A native PDF viewer may replace the browsing context during cleanup.
      }
    }
  }, [document.contentType, onClose, requestScope, state])

  function handleKeyDown(event: ReactKeyboardEvent<HTMLDivElement>) {
    if (event.key !== 'Tab') return

    const focusable = Array.from(
      event.currentTarget.querySelectorAll<HTMLElement>(
        'a[href], button:not([disabled]), iframe, [tabindex]:not([tabindex="-1"])',
      ),
    )
    const first = focusable.at(0)
    const last = focusable.at(-1)

    if (event.shiftKey && window.document.activeElement === first) {
      event.preventDefault()
      last?.focus()
    } else if (!event.shiftKey && window.document.activeElement === last) {
      event.preventDefault()
      first?.focus()
    }
  }

  const titleId = `document-preview-title-${document.id}`
  const descriptionId = `document-preview-description-${document.id}`
  const currentState: PreviewState =
    state.scope === requestScope
      ? state
      : { status: 'loading', scope: requestScope }

  return createPortal(
    <div className="document-preview-backdrop">
      <div
        className="document-preview-dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        aria-describedby={descriptionId}
        aria-busy={currentState.status === 'loading'}
        onKeyDown={handleKeyDown}
      >
        <header className="document-preview-header">
          <div>
            <h2 id={titleId}>{document.originalFileName}</h2>
            <p id={descriptionId}>Pré-visualização segura do documento.</p>
          </div>
          <button
            className="document-preview-close"
            type="button"
            onClick={onClose}
            autoFocus
          >
            Fechar
          </button>
        </header>

        <div className="document-preview-content">
          {currentState.status === 'loading' ? (
            <p className="document-preview-state" role="status">
              Carregando pré-visualização...
            </p>
          ) : null}

          {currentState.status === 'ready' && document.contentType === 'application/pdf' ? (
            <iframe
              ref={pdfFrameRef}
              className="document-preview-pdf"
              src={currentState.objectUrl}
              title={`Conteúdo de ${document.originalFileName}`}
              referrerPolicy="no-referrer"
            />
          ) : null}

          {currentState.status === 'ready' && document.contentType !== 'application/pdf' ? (
            <img
              className="document-preview-image"
              src={currentState.objectUrl}
              alt={`Pré-visualização de ${document.originalFileName}`}
            />
          ) : null}

          {currentState.status === 'unsupported' ? (
            <p className="document-preview-state" role="status">
              A pré-visualização não está disponível para este formato. Baixe o
              arquivo para consultá-lo.
            </p>
          ) : null}

          {currentState.status === 'unavailable' || currentState.status === 'error' ? (
            <div className="document-preview-state" role="alert">
              <p>
                {currentState.status === 'unavailable'
                  ? 'O conteúdo está temporariamente indisponível.'
                  : 'Não foi possível carregar a pré-visualização.'}
              </p>
              <button
                className="secondary-button"
                type="button"
                onClick={() => setRetryVersion((version) => version + 1)}
              >
                Tentar novamente
              </button>
            </div>
          ) : null}
        </div>

        <footer className="document-preview-actions">
          <a
            className="primary-button"
            href={getDocumentDownloadUrl(organizationId, document.id)}
          >
            Baixar documento
          </a>
        </footer>
      </div>
    </div>,
    window.document.body,
  )
}
