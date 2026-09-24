import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { useState } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { LegalDocumentMetadata } from './documentTypes'

const { handleUnauthorizedMock, loadDocumentPreviewMock } = vi.hoisted(() => ({
  handleUnauthorizedMock: vi.fn(),
  loadDocumentPreviewMock: vi.fn(),
}))

vi.mock('../authentication/AuthContext', () => ({
  useAuth: () => ({ handleUnauthorized: handleUnauthorizedMock }),
}))

vi.mock('./documentService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('./documentService')>()
  return {
    ...actual,
    loadDocumentPreview: loadDocumentPreviewMock,
  }
})

import { DocumentRequestError } from './documentService'
import { DocumentPreviewDialog } from './DocumentPreviewDialog'

const organizationId = '11111111-1111-4111-8111-111111111111'
const pdfDocument: LegalDocumentMetadata = {
  id: '44444444-4444-4444-8444-444444444444',
  clientId: null,
  processId: null,
  originalFileName: 'petição final.pdf',
  contentType: 'application/pdf',
  sizeBytes: 128,
  createdAt: '2026-08-20T14:30:00Z',
}

function Harness({
  document,
  organization = organizationId,
}: {
  readonly document: LegalDocumentMetadata
  readonly organization?: string
}) {
  const [isOpen, setIsOpen] = useState(false)
  const [returnFocus, setReturnFocus] = useState<HTMLButtonElement | null>(null)

  return (
    <>
      <button
        type="button"
        onClick={(event) => {
          setReturnFocus(event.currentTarget)
          setIsOpen(true)
        }}
      >
        Visualizar
      </button>
      {isOpen ? (
        <DocumentPreviewDialog
          document={document}
          organizationId={organization}
          returnFocus={returnFocus}
          onClose={() => setIsOpen(false)}
        />
      ) : null}
    </>
  )
}

beforeEach(() => {
  loadDocumentPreviewMock.mockReset()
  vi.spyOn(URL, 'createObjectURL').mockImplementation(
    (blob) => `blob:preview-${blob instanceof Blob ? blob.type : 'media'}`,
  )
  vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => undefined)
})

afterEach(() => {
  vi.restoreAllMocks()
})

describe('DocumentPreviewDialog', () => {
  it('closesWithButtonOutsideTheWorkspaceStackingContext', async () => {
    let resolvePreview: ((blob: Blob) => void) | undefined
    const pendingPreview = new Promise<Blob>((resolve) => {
      resolvePreview = resolve
    })
    loadDocumentPreviewMock.mockReturnValue(pendingPreview)
    render(<Harness document={pdfDocument} />)
    const trigger = screen.getByRole('button', { name: 'Visualizar' })

    trigger.focus()
    fireEvent.click(trigger)

    const dialog = await screen.findByRole('dialog', {
      name: pdfDocument.originalFileName,
    })
    expect(dialog.parentElement?.parentElement).toBe(window.document.body)

    fireEvent.click(screen.getByRole('button', { name: 'Fechar' }))

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    await waitFor(() => expect(trigger).toHaveFocus())

    await act(async () => {
      resolvePreview?.(new Blob(['stale'], { type: 'application/pdf' }))
      await pendingPreview
    })
    expect(URL.createObjectURL).not.toHaveBeenCalled()
  })

  it('opensPdfClosesWithEscapeRestoresFocusAndKeepsDownloadSeparate', async () => {
    loadDocumentPreviewMock.mockResolvedValue(
      new Blob(['%PDF-1.7'], { type: 'application/pdf' }),
    )
    render(<Harness document={pdfDocument} />)
    const trigger = screen.getByRole('button', { name: 'Visualizar' })

    trigger.focus()
    fireEvent.click(trigger)

    const dialog = await screen.findByRole('dialog', {
      name: pdfDocument.originalFileName,
    })
    expect(dialog).toHaveAttribute('aria-modal', 'true')
    expect(
      await screen.findByTitle(`Conteúdo de ${pdfDocument.originalFileName}`),
    ).toHaveAttribute('src', 'blob:preview-application/pdf')
    expect(screen.getByRole('link', { name: 'Baixar documento' })).toHaveAttribute(
      'href',
      `/api/organizations/${organizationId}/documents/${pdfDocument.id}/content`,
    )
    const close = screen.getByRole('button', { name: 'Fechar' })
    const download = screen.getByRole('link', { name: 'Baixar documento' })
    expect(close).toHaveFocus()
    fireEvent.keyDown(close, { key: 'Tab', shiftKey: true })
    expect(download).toHaveFocus()
    fireEvent.keyDown(download, { key: 'Tab' })
    expect(close).toHaveFocus()

    fireEvent.keyDown(window.document, { key: 'Escape' })

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    await waitFor(() => expect(trigger).toHaveFocus())
    expect(URL.revokeObjectURL).toHaveBeenCalledWith(
      'blob:preview-application/pdf',
    )
  })

  it('closesWithEscapeWhenFocusMovesToThePdfFrame', async () => {
    loadDocumentPreviewMock.mockResolvedValue(
      new Blob(['%PDF-1.7'], { type: 'application/pdf' }),
    )
    render(<Harness document={pdfDocument} />)
    const trigger = screen.getByRole('button', { name: 'Visualizar' })

    fireEvent.click(trigger)
    const pdfFrame = await screen.findByTitle(
      `Conteúdo de ${pdfDocument.originalFileName}`,
    ) as HTMLIFrameElement
    pdfFrame.focus()
    expect(pdfFrame).toHaveFocus()

    fireEvent.keyDown(pdfFrame.contentWindow!, { key: 'Escape' })

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    await waitFor(() => expect(trigger).toHaveFocus())
  })

  it.each([
    ['image/png', 'imagem.png'],
    ['image/jpeg', 'fotografia.jpeg'],
  ])('rendersSupportedImageWithoutChangingItsProportions_%s', async (contentType, fileName) => {
    const imageDocument = {
      ...pdfDocument,
      id: contentType === 'image/png'
        ? '55555555-5555-4555-8555-555555555555'
        : '66666666-6666-4666-8666-666666666666',
      originalFileName: fileName,
      contentType,
    }
    loadDocumentPreviewMock.mockResolvedValue(
      new Blob(['synthetic image'], { type: contentType }),
    )
    render(<Harness document={imageDocument} />)

    fireEvent.click(screen.getByRole('button', { name: 'Visualizar' }))

    expect(
      await screen.findByRole('img', {
        name: `Pré-visualização de ${fileName}`,
      }),
    ).toHaveAttribute('src', `blob:preview-${contentType}`)
  })

  it('announcesLoadingAndSafeErrorThenRetries', async () => {
    let rejectPreview: ((reason: unknown) => void) | undefined
    const pending = new Promise<Blob>((_, reject) => {
      rejectPreview = reject
    })
    loadDocumentPreviewMock
      .mockReturnValueOnce(pending)
      .mockResolvedValueOnce(new Blob(['%PDF'], { type: 'application/pdf' }))
    render(<Harness document={pdfDocument} />)

    fireEvent.click(screen.getByRole('button', { name: 'Visualizar' }))
    expect(
      await screen.findByText('Carregando pré-visualização...'),
    ).toBeInTheDocument()

    await act(async () => {
      rejectPreview?.(new DocumentRequestError('unavailable'))
    })
    expect(await screen.findByRole('alert')).toHaveTextContent(
      'O conteúdo está temporariamente indisponível.',
    )

    fireEvent.click(screen.getByRole('button', { name: 'Tentar novamente' }))
    expect(
      await screen.findByTitle(`Conteúdo de ${pdfDocument.originalFileName}`),
    ).toBeInTheDocument()
  })

  it('showsUnsupportedFallbackWithDownload', async () => {
    loadDocumentPreviewMock.mockRejectedValue(
      new DocumentRequestError('unsupported'),
    )
    render(<Harness document={pdfDocument} />)

    fireEvent.click(screen.getByRole('button', { name: 'Visualizar' }))

    expect(
      await screen.findByText(/pré-visualização não está disponível/i),
    ).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Baixar documento' })).toBeVisible()
  })

  it('neverShowsAStalePreviewAfterDocumentOrOrganizationChanges', async () => {
    let resolveFirst: ((blob: Blob) => void) | undefined
    let resolveSecond: ((blob: Blob) => void) | undefined
    loadDocumentPreviewMock
      .mockReturnValueOnce(new Promise<Blob>((resolve) => {
        resolveFirst = resolve
      }))
      .mockReturnValueOnce(new Promise<Blob>((resolve) => {
        resolveSecond = resolve
      }))
    const secondDocument = {
      ...pdfDocument,
      id: '77777777-7777-4777-8777-777777777777',
      originalFileName: 'imagem atual.png',
      contentType: 'image/png',
    }
    const { rerender } = render(<Harness document={pdfDocument} />)
    fireEvent.click(screen.getByRole('button', { name: 'Visualizar' }))

    rerender(
      <Harness
        document={secondDocument}
        organization="99999999-9999-4999-8999-999999999999"
      />,
    )
    await act(async () => {
      resolveSecond?.(new Blob(['current'], { type: 'image/png' }))
    })
    expect(
      await screen.findByRole('img', {
        name: `Pré-visualização de ${secondDocument.originalFileName}`,
      }),
    ).toBeInTheDocument()

    await act(async () => {
      resolveFirst?.(new Blob(['stale'], { type: 'application/pdf' }))
    })
    expect(
      screen.queryByTitle(`Conteúdo de ${pdfDocument.originalFileName}`),
    ).not.toBeInTheDocument()
    expect(URL.createObjectURL).toHaveBeenCalledTimes(1)
  })
})
