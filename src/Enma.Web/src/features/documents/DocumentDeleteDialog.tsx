import { useEffect, useRef, type KeyboardEvent as ReactKeyboardEvent } from 'react'
import { createPortal } from 'react-dom'
import type { LegalDocumentMetadata } from './documentTypes'

interface DocumentDeleteDialogProps {
  readonly document: LegalDocumentMetadata
  readonly returnFocus?: HTMLElement | null
  readonly isDeleting: boolean
  readonly error?: string
  readonly onCancel: () => void
  readonly onConfirm: () => void
}

export function DocumentDeleteDialog({
  document,
  returnFocus,
  isDeleting,
  error,
  onCancel,
  onConfirm,
}: DocumentDeleteDialogProps) {
  const dialogRef = useRef<HTMLDivElement>(null)

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
      if (event.key !== 'Escape' || isDeleting) return

      event.preventDefault()
      event.stopPropagation()
      onCancel()
    }

    window.document.addEventListener('keydown', handleEscape, true)
    return () => {
      window.document.removeEventListener('keydown', handleEscape, true)
    }
  }, [isDeleting, onCancel])

  function handleKeyDown(event: ReactKeyboardEvent<HTMLDivElement>) {
    if (event.key !== 'Tab') return

    const focusable = Array.from(
      event.currentTarget.querySelectorAll<HTMLButtonElement>(
        'button:not([disabled])',
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

  const titleId = `document-delete-title-${document.id}`
  const descriptionId = `document-delete-description-${document.id}`

  return createPortal(
    <div className="document-delete-backdrop">
      <div
        ref={dialogRef}
        className="document-delete-dialog"
        role="alertdialog"
        aria-modal="true"
        aria-labelledby={titleId}
        aria-describedby={descriptionId}
        aria-busy={isDeleting}
        onKeyDown={handleKeyDown}
      >
        <p className="eyebrow">EXCLUSÃO PERMANENTE</p>
        <h2 id={titleId}>Excluir documento?</h2>
        <p id={descriptionId}>
          O arquivo <strong>{document.originalFileName}</strong> será removido
          permanentemente e não poderá ser recuperado.
        </p>
        {error ? <p className="form-error" role="alert">{error}</p> : null}
        <div className="document-delete-actions">
          <button
            className="secondary-button"
            type="button"
            disabled={isDeleting}
            onClick={onCancel}
            autoFocus
          >
            Cancelar
          </button>
          <button
            className="danger-button"
            type="button"
            disabled={isDeleting}
            onClick={onConfirm}
          >
            {isDeleting ? 'Excluindo...' : 'Excluir documento'}
          </button>
        </div>
      </div>
    </div>,
    window.document.body,
  )
}
