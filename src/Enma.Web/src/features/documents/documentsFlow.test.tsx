import { act, fireEvent, render, screen, within } from '@testing-library/react'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createAppRoutes } from '../../app/router'
import { clearCsrfToken } from '../authentication/csrfClient'
import { createEmailVerificationFlow } from '../email-verification/emailVerificationService'
import type { OrganizationNavigationItem } from '../organizations/organizationTypes'
import type { ActiveClientLookupItem } from '../processes/legalProcessTypes'
import type { LegalProcessLookupItem } from '../tasks/legalTaskTypes'
import type { LegalDocumentMetadata } from './documentTypes'

const organization: OrganizationNavigationItem = {
  id: '11111111-1111-4111-8111-111111111111',
  membershipId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa1',
  name: 'Organização Alfa',
  role: 'Member',
}

const client: ActiveClientLookupItem = {
  id: '22222222-2222-4222-8222-222222222222',
  name: 'Cliente Exemplo',
}

const process: LegalProcessLookupItem = {
  id: '33333333-3333-4333-8333-333333333333',
  title: 'Ação de cobrança',
  clientName: client.name,
}

const document: LegalDocumentMetadata & {
  readonly contentHashSha256: string
  readonly uploadedByMembershipId: string
  readonly storedObjectKey: string
} = {
  id: '44444444-4444-4444-8444-444444444444',
  clientId: null,
  processId: process.id,
  originalFileName: 'contestação final.pdf',
  contentType: 'application/pdf',
  sizeBytes: 1_572_864,
  createdAt: '2026-08-20T14:30:00Z',
  contentHashSha256: 'internal-hash',
  uploadedByMembershipId: organization.membershipId,
  storedObjectKey: 'private/storage/key',
}

function response(status: number, body?: unknown): Response {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
  })
}

function organizationResponse(): Response {
  return response(200, { items: [organization] })
}

function documentListResponse(
  items: readonly LegalDocumentMetadata[],
  pageNumber = 1,
  hasNext = false,
): Response {
  return response(200, { items, pageNumber, pageSize: 20, hasNext })
}

function lookupResponse<Item>(items: readonly Item[]): Response {
  return response(200, { items, pageNumber: 1, pageSize: 20, hasNext: false })
}

function authenticatedFetch(
  ...scopedResponses: readonly (Response | Promise<Response>)[]
) {
  const fetchMock = vi
    .fn()
    .mockResolvedValueOnce(response(200))
    .mockResolvedValueOnce(organizationResponse())

  for (const scopedResponse of scopedResponses) {
    fetchMock.mockReturnValueOnce(Promise.resolve(scopedResponse))
  }

  return fetchMock
}

function renderRoute(path: string) {
  const router = createMemoryRouter(
    createAppRoutes(createEmailVerificationFlow(undefined)),
    { initialEntries: [path] },
  )
  render(<RouterProvider router={router} />)
  return router
}

function documentsPath(query = ''): string {
  return `/organizations/${organization.id}/documents${query}`
}

function requestUrl(fetchMock: ReturnType<typeof vi.fn>, callIndex: number): URL {
  return new URL(String(fetchMock.mock.calls[callIndex]?.[0]), 'https://enma.test')
}

beforeEach(() => {
  clearCsrfToken()
  window.localStorage.clear()
  window.sessionStorage.clear()
})

afterEach(() => {
  clearCsrfToken()
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

describe('Documents D1 flow', () => {
  it('DocumentsRoute_LoadsMetadataNavigationAndNativeDownloadWithoutInternalFields', async () => {
    const fetchMock = authenticatedFetch(documentListResponse([document]))
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(documentsPath())

    expect(await screen.findByRole('heading', { name: 'Documentos' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Documentos' })).toHaveAttribute(
      'href',
      documentsPath(),
    )
    expect(await screen.findByRole('link', { name: document.originalFileName })).toHaveAttribute(
      'href',
      `${documentsPath()}/${document.id}`,
    )
    expect(screen.getByText('Vinculado a processo')).toBeInTheDocument()
    expect(screen.getByText('PDF')).toBeInTheDocument()
    expect(screen.getByText('1,5 MB')).toBeInTheDocument()

    const download = screen.getByRole('link', { name: 'Baixar' })
    expect(download).toHaveAttribute(
      'href',
      `/api/organizations/${organization.id}/documents/${document.id}/content`,
    )
    expect(download).not.toHaveAttribute('data-storage-url')
    expect(screen.queryByText(document.contentHashSha256)).not.toBeInTheDocument()
    expect(screen.queryByText(document.uploadedByMembershipId)).not.toBeInTheDocument()
    expect(screen.queryByText(document.storedObjectKey)).not.toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(3)
  })

  it('DocumentsRoute_PendingRequestAnnouncesLoading', async () => {
    const pending = new Promise<Response>(() => undefined)
    vi.stubGlobal('fetch', authenticatedFetch(pending))

    renderRoute(documentsPath())

    expect(await screen.findByText('Carregando documentos...')).toBeInTheDocument()
  })

  it('DocumentsRoute_EmptyAndFilteredEmptyUseDistinctSafeStates', async () => {
    const fetchMock = authenticatedFetch(
      documentListResponse([]),
      documentListResponse([]),
    )
    vi.stubGlobal('fetch', fetchMock)
    renderRoute(documentsPath())

    expect(await screen.findByRole('heading', { name: 'Nenhum documento disponível' })).toBeInTheDocument()
    fireEvent.change(screen.getByLabelText('Buscar por nome do arquivo'), {
      target: { value: 'petição' },
    })
    fireEvent.submit(screen.getByRole('button', { name: 'Buscar' }).closest('form')!)

    expect(await screen.findByRole('heading', { name: 'Nenhum documento encontrado' })).toBeInTheDocument()
  })

  it('DocumentSearch_SubmitUsesBackendSearchAndResetsPage', async () => {
    const fetchMock = authenticatedFetch(
      documentListResponse([], 2),
      documentListResponse([]),
    )
    vi.stubGlobal('fetch', fetchMock)
    renderRoute(documentsPath('?page=2'))

    await screen.findByRole('heading', { name: 'Nenhum documento disponível' })
    fireEvent.change(screen.getByLabelText('Buscar por nome do arquivo'), {
      target: { value: '  contrato final  ' },
    })
    fireEvent.submit(screen.getByRole('button', { name: 'Buscar' }).closest('form')!)

    await screen.findByRole('heading', { name: 'Nenhum documento encontrado' })
    const url = requestUrl(fetchMock, 3)
    expect(url.searchParams.get('search')).toBe('contrato final')
    expect(url.searchParams.get('page')).toBe('1')
    expect(url.searchParams.get('clientId')).toBeNull()
    expect(url.searchParams.get('processId')).toBeNull()
  })

  it('ClientFilter_UsesTenantLookupAndSendsOnlyClientId', async () => {
    const fetchMock = authenticatedFetch(
      documentListResponse([]),
      lookupResponse([client]),
      documentListResponse([]),
    )
    vi.stubGlobal('fetch', fetchMock)
    renderRoute(documentsPath())

    await screen.findByRole('heading', { name: 'Nenhum documento disponível' })
    fireEvent.click(screen.getByRole('button', { name: 'Escolher cliente' }))
    fireEvent.click(await screen.findByRole('button', { name: client.name }))
    await screen.findByText(client.name)

    expect(requestUrl(fetchMock, 3).pathname).toBe(
      `/api/organizations/${organization.id}/clients/lookup`,
    )
    const listUrl = requestUrl(fetchMock, 4)
    expect(listUrl.searchParams.get('clientId')).toBe(client.id)
    expect(listUrl.searchParams.get('processId')).toBeNull()
  })

  it('ProcessFilter_UsesTenantLookupAndReplacesClientFilter', async () => {
    const fetchMock = authenticatedFetch(
      documentListResponse([]),
      lookupResponse([process]),
      documentListResponse([]),
    )
    vi.stubGlobal('fetch', fetchMock)
    renderRoute(documentsPath(`?clientId=${client.id}`))

    await screen.findByRole('heading', { name: 'Nenhum documento encontrado' })
    fireEvent.click(screen.getByRole('button', { name: 'Escolher processo' }))
    const processButton = await screen.findByRole('button', { name: new RegExp(process.title) })
    fireEvent.click(processButton)
    await screen.findByText(`${process.title} — ${process.clientName}`)

    const listUrl = requestUrl(fetchMock, 4)
    expect(listUrl.searchParams.get('processId')).toBe(process.id)
    expect(listUrl.searchParams.get('clientId')).toBeNull()
  })

  it('DocumentsRoute_DirectConflictingFiltersNeverSendsBoth', async () => {
    const fetchMock = authenticatedFetch(documentListResponse([]))
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(documentsPath(`?clientId=${client.id}&processId=${process.id}`))

    await screen.findByRole('heading', { name: 'Nenhum documento encontrado' })
    const url = requestUrl(fetchMock, 2)
    expect(url.searchParams.get('processId')).toBe(process.id)
    expect(url.searchParams.get('clientId')).toBeNull()
  })

  it('DocumentsPagination_RequestsRequestedPageAndUsesHasNext', async () => {
    const fetchMock = authenticatedFetch(documentListResponse([document], 2, true))
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(documentsPath('?page=2'))

    expect(await screen.findByText('Página 2')).toBeInTheDocument()
    expect(requestUrl(fetchMock, 2).searchParams.get('page')).toBe('2')
    expect(screen.getByRole('button', { name: 'Página anterior' })).toBeEnabled()
    expect(screen.getByRole('button', { name: 'Próxima página' })).toBeEnabled()
  })

  it('DocumentsError_HidesServerDetailsAndOffersRetry', async () => {
    const fetchMock = authenticatedFetch(
      response(500, { detail: 'private storage failure' }),
      documentListResponse([]),
    )
    vi.stubGlobal('fetch', fetchMock)
    renderRoute(documentsPath())

    expect(await screen.findByRole('heading', { name: 'Não foi possível carregar os documentos' })).toBeInTheDocument()
    expect(screen.queryByText('private storage failure')).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Tentar novamente' }))
    expect(await screen.findByRole('heading', { name: 'Nenhum documento disponível' })).toBeInTheDocument()
  })

  it('DocumentDetails_LoadsAuthoritativeMetadataAndKeepsDownloadNative', async () => {
    const fetchMock = authenticatedFetch(response(200, document))
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(`${documentsPath()}/${document.id}`)

    const heading = await screen.findByRole('heading', { name: document.originalFileName })
    const details = heading.closest('section')!
    expect(within(details).getByText('Vinculado a processo')).toBeInTheDocument()
    expect(within(details).getByText('1,5 MB')).toBeInTheDocument()
    expect(within(details).queryByText(document.contentHashSha256)).not.toBeInTheDocument()
    expect(within(details).queryByText(document.storedObjectKey)).not.toBeInTheDocument()
    expect(within(details).getByRole('link', { name: 'Baixar documento' })).toHaveAttribute(
      'href',
      `/api/organizations/${organization.id}/documents/${document.id}/content`,
    )
    expect(fetchMock).toHaveBeenCalledTimes(3)
    expect(requestUrl(fetchMock, 2).pathname).toBe(
      `/api/organizations/${organization.id}/documents/${document.id}`,
    )
  })

  it('DocumentDetails_NotFoundAndMalformedIdFailSafely', async () => {
    const fetchMock = authenticatedFetch(response(404, { detail: 'cross tenant' }))
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(`${documentsPath()}/${document.id}`)

    expect(await screen.findByRole('heading', { name: 'Documento não encontrado ou indisponível' })).toBeInTheDocument()
    expect(screen.queryByText('cross tenant')).not.toBeInTheDocument()

    await act(async () => router.navigate(`${documentsPath()}/not-a-guid`))
    expect(await screen.findByRole('heading', { name: 'Documento não encontrado ou indisponível' })).toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(3)
  })
})
