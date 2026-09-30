import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from '@testing-library/react'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const { deleteDocumentMock, listDocumentsMock } = vi.hoisted(() => ({
  deleteDocumentMock: vi.fn(),
  listDocumentsMock: vi.fn(),
}))

vi.mock('../notifications/NotificationCenter', () => ({
  NotificationCenter: () => null,
}))
vi.mock('../documents/documentService', async (importOriginal) => {
  const actual =
    await importOriginal<typeof import('../documents/documentService')>()
  return {
    ...actual,
    deleteDocument: deleteDocumentMock,
    listDocuments: listDocumentsMock,
  }
})
import { createAppRoutes } from '../../app/router'
import { clearCsrfToken } from '../authentication/csrfClient'
import { createEmailVerificationFlow } from '../email-verification/emailVerificationService'
import type { OrganizationNavigationItem } from '../organizations/organizationTypes'
import type { LegalDocumentMetadata } from '../documents/documentTypes'
import type { LegalProcess } from './legalProcessTypes'

const organizationA: OrganizationNavigationItem = {
  id: '11111111-1111-4111-8111-111111111111',
  membershipId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa1',
  name: 'Organização Alfa',
  role: 'Owner',
}

const organizationB: OrganizationNavigationItem = {
  id: '22222222-2222-4222-8222-222222222222',
  membershipId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbb2',
  name: 'Organização Beta',
  role: 'Administrator',
}

const processA: LegalProcess = {
  id: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
  title: 'Ação de cobrança',
  clientId: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc',
  clientName: 'Cliente inativo permanece relacionado',
  createdAt: '2026-08-12T14:30:00Z',
  processNumber: null,
  status: 'inProgress',
  courtOrAuthority: null,
  responsibleMembershipId: null,
  responsibleDisplayName: null,
}

const processB: LegalProcess = {
  id: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',
  title: 'Revisional de contrato',
  clientId: 'dddddddd-dddd-4ddd-8ddd-dddddddddddd',
  clientName: 'Cliente Beta',
  createdAt: '2026-08-11T12:00:00Z',
  processNumber: null,
  status: 'inProgress',
  courtOrAuthority: null,
  responsibleMembershipId: null,
  responsibleDisplayName: null,
}

const documentA: LegalDocumentMetadata = {
  id: 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee',
  clientId: null,
  processId: processA.id,
  originalFileName: 'peticao-inicial.pdf',
  contentType: 'application/pdf',
  sizeBytes: 2_048,
  createdAt: '2026-08-13T10:00:00Z',
}

const documentB: LegalDocumentMetadata = {
  id: 'ffffffff-ffff-4fff-8fff-ffffffffffff',
  clientId: null,
  processId: processB.id,
  originalFileName: 'contrato-revisional.docx',
  contentType:
    'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
  sizeBytes: 4_096,
  createdAt: '2026-08-14T11:00:00Z',
}

function response(status: number, body?: unknown): Response {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers:
      body === undefined ? undefined : { 'Content-Type': 'application/json' },
  })
}

function organizationResponse(
  items: readonly OrganizationNavigationItem[],
): Response {
  return response(200, { items })
}

function processListResponse(items: readonly LegalProcess[]): Response {
  return response(200, { items, pageNumber: 1, pageSize: 20, hasNext: false })
}

function documentListResponse(items: readonly LegalDocumentMetadata[]) {
  return { items, pageNumber: 1, pageSize: 5, hasNext: false }
}

function authenticatedFetch(
  organizations: readonly OrganizationNavigationItem[],
  ...scopedResponses: readonly (Response | Promise<Response>)[]
) {
  const fetchMock = vi
    .fn()
    .mockResolvedValueOnce(organizationResponse([]))
    .mockResolvedValueOnce(organizationResponse(organizations))

  for (const scopedResponse of scopedResponses) {
    fetchMock.mockReturnValueOnce(Promise.resolve(scopedResponse))
  }

  return fetchMock
}

function detailPath(
  organization: OrganizationNavigationItem,
  legalProcess: LegalProcess,
): string {
  return `/organizations/${organization.id}/processes/${legalProcess.id}`
}

function renderRoute(path: string) {
  const router = createMemoryRouter(
    createAppRoutes(createEmailVerificationFlow(undefined)),
    { initialEntries: [path] },
  )

  render(<RouterProvider router={router} />)
  return router
}

function openEditAndSubmit(title: string) {
  fireEvent.click(screen.getByRole('button', { name: 'Editar processo' }))
  const input = screen.getByLabelText('Título')
  fireEvent.change(input, { target: { value: title } })
  const form = screen
    .getByRole('button', { name: 'Salvar alterações' })
    .closest('form')!
  fireEvent.submit(form)
  return form
}

beforeEach(() => {
  deleteDocumentMock.mockReset()
  deleteDocumentMock.mockResolvedValue(undefined)
  listDocumentsMock.mockReset()
  listDocumentsMock.mockResolvedValue(documentListResponse([]))
  clearCsrfToken()
  window.localStorage.clear()
  window.sessionStorage.clear()
})

afterEach(() => {
  clearCsrfToken()
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

describe('Processes D2 flow', () => {
  it('ProcessDetail_Documents_ListsOnlyExactProcessWithBoundedRequest', async () => {
    listDocumentsMock.mockResolvedValueOnce(documentListResponse([documentA]))
    vi.stubGlobal(
      'fetch',
      authenticatedFetch([organizationA], response(200, processA)),
    )

    renderRoute(detailPath(organizationA, processA))

    expect(
      await screen.findByRole('link', { name: documentA.originalFileName }),
    ).toBeInTheDocument()
    expect(listDocumentsMock).toHaveBeenCalledWith(
      organizationA.id,
      { processId: processA.id, pageNumber: 1, pageSize: 5 },
      expect.any(Function),
      expect.any(AbortSignal),
    )
  })

  it('ProcessDetail_Documents_RejectsUnexpectedOtherProcessResult', async () => {
    listDocumentsMock.mockResolvedValueOnce(documentListResponse([documentB]))
    vi.stubGlobal(
      'fetch',
      authenticatedFetch([organizationA], response(200, processA)),
    )

    renderRoute(detailPath(organizationA, processA))

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Não foi possível carregar os documentos vinculados.',
    )
    expect(screen.queryByText(documentB.originalFileName)).not.toBeInTheDocument()
  })

  it('ProcessDetail_Documents_ShowsEmptyState', async () => {
    vi.stubGlobal(
      'fetch',
      authenticatedFetch([organizationA], response(200, processA)),
    )

    renderRoute(detailPath(organizationA, processA))

    expect(
      await screen.findByText('Nenhum documento vinculado a este processo.'),
    ).toBeInTheDocument()
  })

  it('ProcessDetail_Documents_LoadingErrorAndRetryStaySectionLocal', async () => {
    let rejectDocuments: ((reason?: unknown) => void) | undefined
    const pendingDocuments = new Promise((_, reject) => {
      rejectDocuments = reject
    })
    listDocumentsMock
      .mockReturnValueOnce(pendingDocuments)
      .mockResolvedValueOnce(documentListResponse([documentA]))
    vi.stubGlobal(
      'fetch',
      authenticatedFetch([organizationA], response(200, processA)),
    )

    renderRoute(detailPath(organizationA, processA))

    await screen.findByRole('heading', { name: processA.title })
    expect(screen.getByText('Carregando documentos vinculados...')).toBeInTheDocument()

    await act(async () => rejectDocuments?.(new Error('private network detail')))
    const alert = await screen.findByRole('alert')
    expect(alert).not.toHaveTextContent('private network detail')
    expect(screen.getByRole('heading', { name: processA.title })).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'Tentar novamente' }))
    expect(
      await screen.findByRole('link', { name: documentA.originalFileName }),
    ).toBeInTheDocument()
  })

  it('ProcessDetail_Documents_NavigatesToDetailAndFilteredList', async () => {
    listDocumentsMock.mockResolvedValue(documentListResponse([documentA]))
    vi.stubGlobal(
      'fetch',
      authenticatedFetch([organizationA], response(200, processA)),
    )
    const router = renderRoute(detailPath(organizationA, processA))

    const documentLink = await screen.findByRole('link', {
      name: documentA.originalFileName,
    })
    expect(documentLink).toHaveAttribute(
      'href',
      `/organizations/${organizationA.id}/documents/${documentA.id}`,
    )
    expect(screen.getByRole('link', { name: 'Baixar' })).toHaveAttribute(
      'href',
      `/api/organizations/${organizationA.id}/documents/${documentA.id}/content`,
    )
    expect(screen.getByRole('button', { name: 'Visualizar' })).toBeVisible()

    fireEvent.click(screen.getByRole('link', { name: 'Ver todos' }))
    expect(router.state.location.pathname).toBe(
      `/organizations/${organizationA.id}/documents`,
    )
    expect(router.state.location.search).toBe(`?processId=${processA.id}`)
  })

  it('ProcessDetail_Documents_OwnerConfirmsDeletionAndRefreshesSection', async () => {
    listDocumentsMock
      .mockResolvedValueOnce(documentListResponse([documentA]))
      .mockResolvedValueOnce(documentListResponse([]))
    vi.stubGlobal(
      'fetch',
      authenticatedFetch([organizationA], response(200, processA)),
    )
    renderRoute(detailPath(organizationA, processA))

    fireEvent.click(await screen.findByRole('button', { name: 'Excluir' }))
    expect(await screen.findByRole('alertdialog', {
      name: 'Excluir documento?',
    })).toHaveTextContent(documentA.originalFileName)
    fireEvent.click(screen.getByRole('button', { name: 'Excluir documento' }))

    await waitFor(() => expect(deleteDocumentMock).toHaveBeenCalledWith(
      organizationA.id,
      documentA.id,
      expect.any(Function),
    ))
    expect(await screen.findByText(
      `Documento “${documentA.originalFileName}” excluído com sucesso.`,
    )).toBeInTheDocument()
    expect(await screen.findByText(
      'Nenhum documento vinculado a este processo.',
    )).toBeInTheDocument()
  })

  it('ProcessDetail_Documents_MemberDoesNotReceiveDeleteAction', async () => {
    const member = { ...organizationA, role: 'Member' as const }
    listDocumentsMock.mockResolvedValueOnce(documentListResponse([documentA]))
    vi.stubGlobal(
      'fetch',
      authenticatedFetch([member], response(200, processA)),
    )
    renderRoute(detailPath(member, processA))

    expect(await screen.findByText(documentA.originalFileName)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Excluir' })).not.toBeInTheDocument()
  })

  it.each([
    ['process', organizationA, processB],
    ['organization', organizationB, { ...processB, id: processA.id }],
  ] as const)(
    'ProcessDetail_Documents_LateResponseAfter%sChangeNeverRendersStaleItems',
    async (_scenario, targetOrganization, targetProcess) => {
      let resolveDocumentsA:
        | ((value: ReturnType<typeof documentListResponse>) => void)
        | undefined
      const pendingDocumentsA = new Promise<
        ReturnType<typeof documentListResponse>
      >((resolve) => {
        resolveDocumentsA = resolve
      })
      const targetDocument = { ...documentB, processId: targetProcess.id }
      listDocumentsMock
        .mockReturnValueOnce(pendingDocumentsA)
        .mockResolvedValueOnce(documentListResponse([targetDocument]))
      vi.stubGlobal(
        'fetch',
        authenticatedFetch(
          targetOrganization.id === organizationA.id
            ? [organizationA]
            : [organizationA, organizationB],
          response(200, processA),
          response(200, targetProcess),
        ),
      )
      const router = renderRoute(detailPath(organizationA, processA))

      await screen.findByRole('heading', { name: processA.title })
      await act(async () =>
        router.navigate(detailPath(targetOrganization, targetProcess)),
      )
      expect(
        await screen.findByRole('link', {
          name: targetDocument.originalFileName,
        }),
      ).toBeInTheDocument()

      await act(async () => {
        resolveDocumentsA?.(documentListResponse([documentA]))
        await pendingDocumentsA
      })

      expect(screen.queryByText(documentA.originalFileName)).not.toBeInTheDocument()
      expect(screen.getByText(targetDocument.originalFileName)).toBeInTheDocument()
    },
  )

  it('ProcessDetail_MemberRoute_TargetsContextAndRendersReadOnlyDisplayFields', async () => {
    const member = { ...organizationA, role: 'Member' as const }
    const localStorageSpy = vi.spyOn(window.localStorage, 'setItem')
    const sessionStorageSpy = vi.spyOn(window.sessionStorage, 'setItem')
    const fetchMock = authenticatedFetch([member], response(200, processA))
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(member, processA))

    expect(
      await screen.findByRole('heading', { name: processA.title }),
    ).toBeInTheDocument()
    expect(screen.getByText(processA.clientName)).toBeInTheDocument()
    expect(screen.getByText(/12\/08\/2026/)).toBeInTheDocument()
    expect(screen.queryByText(processA.id)).not.toBeInTheDocument()
    expect(screen.queryByText(processA.clientId)).not.toBeInTheDocument()
    expect(
      screen.queryByRole('button', { name: 'Editar processo' }),
    ).not.toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Voltar para processos' })).toHaveAttribute(
      'href',
      `/organizations/${member.id}/processes`,
    )
    expect(fetchMock).toHaveBeenNthCalledWith(
      3,
      `/api/organizations/${member.id}/processes/${processA.id}`,
      {
        method: 'GET',
        cache: 'no-store',
        signal: expect.any(AbortSignal),
        credentials: 'same-origin',
      },
    )
    expect(localStorageSpy).not.toHaveBeenCalled()
    expect(sessionStorageSpy).not.toHaveBeenCalled()
  })

  it.each(['Owner', 'Administrator'] as const)(
    'ProcessDetail_%sRole_ShowsTitleEditControl',
    async (role) => {
      const organization = { ...organizationA, role }
      vi.stubGlobal(
        'fetch',
        authenticatedFetch([organization], response(200, processA)),
      )

      renderRoute(detailPath(organization, processA))

      expect(
        await screen.findByRole('button', { name: 'Editar processo' }),
      ).toBeInTheDocument()
    },
  )

  it('ProcessList_TitleLink_NavigatesToCurrentOrganizationDetail', async () => {
    const fetchMock = authenticatedFetch(
      [organizationA],
      processListResponse([processA]),
      response(200, processA),
    )
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(`/organizations/${organizationA.id}/processes`)

    fireEvent.click(await screen.findByRole('link', { name: processA.title }))

    expect(
      await screen.findByRole('heading', { name: processA.title }),
    ).toBeInTheDocument()
    expect(router.state.location.pathname).toBe(
      detailPath(organizationA, processA),
    )
  })

  it('ProcessDetail_MalformedProcessId_DoesNotIssueBusinessRequest', async () => {
    const fetchMock = authenticatedFetch([organizationA])
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(`/organizations/${organizationA.id}/processes/not-a-guid`)

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Processo não encontrado ou indisponível.',
    )
    expect(fetchMock).toHaveBeenCalledTimes(2)
  })

  it.each([
    [404, 'Processo não encontrado ou indisponível.'],
    [403, 'Não foi possível acessar este processo.'],
  ] as const)(
    'ProcessDetail_Status%s_ShowsSafeStateWithoutServerDetail',
    async (status, expectedMessage) => {
      vi.stubGlobal(
        'fetch',
        authenticatedFetch(
          [organizationA],
          response(status, { detail: 'private tenant information' }),
        ),
      )

      renderRoute(detailPath(organizationA, processA))

      const alert = await screen.findByRole('alert')
      expect(alert).toHaveTextContent(expectedMessage)
      expect(alert).not.toHaveTextContent('private tenant information')
    },
  )

  it('ProcessDetail_MalformedResponse_ShowsGenericSafeError', async () => {
    vi.stubGlobal(
      'fetch',
      authenticatedFetch(
        [organizationA],
        response(200, { ...processA, clientName: 42 }),
      ),
    )

    renderRoute(detailPath(organizationA, processA))

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Não foi possível carregar o processo. Tente novamente.',
    )
    expect(screen.queryByText(processA.title)).not.toBeInTheDocument()
  })

  it('ProcessDetail_Unauthorized_InvalidatesSessionAndRemovesProtectedDetail', async () => {
    vi.stubGlobal(
      'fetch',
      authenticatedFetch([organizationA], response(401)),
    )

    renderRoute(detailPath(organizationA, processA))

    expect(
      await screen.findByRole('heading', { name: 'Entrar no ENMA' }),
    ).toBeInTheDocument()
    expect(screen.queryByText(processA.title)).not.toBeInTheDocument()
  })

  it('ProcessDetail_NetworkFailure_RetriesOnlyCurrentContext', async () => {
    const fetchMock = authenticatedFetch([organizationA])
      .mockRejectedValueOnce(new Error('private network detail'))
      .mockResolvedValueOnce(response(200, processA))
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(organizationA, processA))

    const alert = await screen.findByRole('alert')
    expect(alert).not.toHaveTextContent('private network detail')
    fireEvent.click(screen.getByRole('button', { name: 'Tentar novamente' }))

    expect(
      await screen.findByRole('heading', { name: processA.title }),
    ).toBeInTheDocument()
    expect(fetchMock.mock.calls[3]?.[0]).toBe(
      `/api/organizations/${organizationA.id}/processes/${processA.id}`,
    )
  })

  it('ProcessEdit_TrimmedTitle_SendsExactCsrfBodyWithoutOptimismAndUsesRefetch', async () => {
    let resolveUpdate: ((value: Response) => void) | undefined
    const pendingUpdate = new Promise<Response>((resolve) => {
      resolveUpdate = resolve
    })
    const normalizedProcess = { ...processA, title: 'Novo título normalizado' }
    const fetchMock = authenticatedFetch(
      [organizationA],
      response(200, processA),
      response(200, { requestToken: 'test-token' }),
      pendingUpdate,
      response(200, normalizedProcess),
    )
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(organizationA, processA))
    await screen.findByRole('heading', { name: processA.title })
    const form = openEditAndSubmit('  Novo título normalizado  ')

    expect(await screen.findByRole('button', { name: 'Salvando...' })).toBeDisabled()
    fireEvent.submit(form)
    expect(fetchMock).toHaveBeenCalledTimes(5)
    expect(screen.getByRole('heading', { name: processA.title })).toBeInTheDocument()
    expect(
      screen.queryByRole('heading', { name: normalizedProcess.title }),
    ).not.toBeInTheDocument()

    const [url, init] = fetchMock.mock.calls[4] as [string, RequestInit]
    expect(url).toBe(
      `/api/organizations/${organizationA.id}/processes/${processA.id}`,
    )
    expect(init.method).toBe('PUT')
    expect(init.headers).toEqual({
      'Content-Type': 'application/json',
      'X-CSRF-TOKEN': 'test-token',
    })
    expect(JSON.parse(init.body as string)).toEqual({
      title: normalizedProcess.title,
    })
    expect(Object.keys(JSON.parse(init.body as string))).toEqual(['title'])

    await act(async () => {
      resolveUpdate?.(response(204))
      await pendingUpdate
    })

    expect(
      await screen.findByRole('heading', { name: normalizedProcess.title }),
    ).toBeInTheDocument()
    expect(fetchMock.mock.calls[5]?.[0]).toBe(url)
    expect(fetchMock.mock.calls[5]?.[1]?.method).toBe('GET')
  })

  it('ProcessEdit_InvalidTitlesRejectWhitespaceAndOverlongButAccept150Characters', async () => {
    const acceptedTitle = 'x'.repeat(150)
    const fetchMock = authenticatedFetch(
      [organizationA],
      response(200, processA),
      response(200, { requestToken: 'test-token' }),
      response(204),
      response(200, { ...processA, title: acceptedTitle }),
    )
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(organizationA, processA))
    await screen.findByRole('heading', { name: processA.title })
    fireEvent.click(screen.getByRole('button', { name: 'Editar processo' }))
    const input = screen.getByLabelText('Título')
    const form = screen
      .getByRole('button', { name: 'Salvar alterações' })
      .closest('form')!

    fireEvent.change(input, { target: { value: '   ' } })
    fireEvent.submit(form)
    expect(await screen.findByText('Informe o título do processo.')).toBeInTheDocument()

    fireEvent.change(input, { target: { value: 'x'.repeat(151) } })
    fireEvent.submit(form)
    expect(
      await screen.findByText('O título deve ter no máximo 150 caracteres.'),
    ).toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(3)

    fireEvent.change(input, { target: { value: acceptedTitle } })
    fireEvent.submit(form)

    expect(
      await screen.findByRole('heading', { name: acceptedTitle }),
    ).toBeInTheDocument()
    expect(JSON.parse(fetchMock.mock.calls[4]?.[1]?.body as string)).toEqual({
      title: acceptedTitle,
    })
  })

  it.each([
    [403, 'Você não tem permissão para alterar este processo.', false],
    [400, 'Não foi possível validar a alteração.', true],
  ] as const)(
    'ProcessEdit_Status%s_KeepsAuthoritativeDetailWithoutRetry',
    async (status, expectedMessage, reloads) => {
      const fetchMock = authenticatedFetch(
        [organizationB],
        response(200, processA),
        response(200, { requestToken: 'test-token' }),
        response(status, { detail: 'private mutation detail' }),
        ...(reloads ? [response(200, processA)] : []),
      )
      vi.stubGlobal('fetch', fetchMock)

      renderRoute(detailPath(organizationB, processA))
      await screen.findByRole('heading', { name: processA.title })
      openEditAndSubmit('Título sem autorização atual')

      const alert = await screen.findByRole('alert')
      expect(alert).toHaveTextContent(expectedMessage)
      expect(alert).not.toHaveTextContent('private mutation detail')
      expect(screen.getByRole('heading', { name: processA.title })).toBeInTheDocument()
      await waitFor(() =>
        expect(fetchMock).toHaveBeenCalledTimes(reloads ? 6 : 5),
      )
      const methods = fetchMock.mock.calls
        .slice(2)
        .map(([, init]) => (init as RequestInit | undefined)?.method)
      expect(methods.filter((method) => method === 'PUT')).toHaveLength(1)
      if (reloads) expect(methods.at(-1)).toBe('GET')
    },
  )

  it('ProcessEdit_NotFound_RemovesPreviouslyRenderedResourceWithoutTenantInference', async () => {
    const fetchMock = authenticatedFetch(
      [organizationA],
      response(200, processA),
      response(200, { requestToken: 'test-token' }),
      response(404, { detail: 'cross tenant' }),
    )
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(organizationA, processA))
    await screen.findByRole('heading', { name: processA.title })
    openEditAndSubmit('Título indisponível')

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Processo não encontrado ou indisponível.')
    expect(alert).not.toHaveTextContent('cross tenant')
    expect(screen.queryByText(processA.title)).not.toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(5)
  })

  it('ProcessEdit_NetworkFailure_ShowsUncertainStateWithoutRetryOrFakeTitle', async () => {
    const fetchMock = authenticatedFetch(
      [organizationA],
      response(200, processA),
      response(200, { requestToken: 'test-token' }),
    )
      .mockRejectedValueOnce(new Error('private connection detail'))
      .mockResolvedValueOnce(response(200, processA))
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(organizationA, processA))
    await screen.findByRole('heading', { name: processA.title })
    openEditAndSubmit('Resultado incerto')

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Atualize os dados antes de tentar novamente.')
    expect(alert).not.toHaveTextContent('private connection detail')
    expect(screen.getByRole('heading', { name: processA.title })).toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(6)
    expect(fetchMock.mock.calls[5]?.[1]?.method).toBe('GET')
  })

  it('ProcessDetail_OldProcessResponseCompletesLast_RemainsOnCurrentProcess', async () => {
    let resolveProcessA: ((value: Response) => void) | undefined
    const pendingProcessA = new Promise<Response>((resolve) => {
      resolveProcessA = resolve
    })
    const fetchMock = authenticatedFetch(
      [organizationA],
      pendingProcessA,
      response(200, processB),
    )
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(detailPath(organizationA, processA))

    await screen.findByText('Carregando processo...')
    await act(async () => router.navigate(detailPath(organizationA, processB)))
    expect(
      await screen.findByRole('heading', { name: processB.title }),
    ).toBeInTheDocument()

    await act(async () => {
      resolveProcessA?.(response(200, processA))
      await pendingProcessA
    })

    expect(screen.getByRole('heading', { name: processB.title })).toBeInTheDocument()
    expect(
      screen.queryByRole('heading', { name: processA.title }),
    ).not.toBeInTheDocument()
  })

  it('ProcessDetail_OldOrganizationResponseCompletesLast_NeverRendersAcrossTenant', async () => {
    let resolveProcessA: ((value: Response) => void) | undefined
    const pendingProcessA = new Promise<Response>((resolve) => {
      resolveProcessA = resolve
    })
    const fetchMock = authenticatedFetch(
      [organizationA, organizationB],
      pendingProcessA,
      response(200, processB),
    )
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(detailPath(organizationA, processA))

    await screen.findByText('Carregando processo...')
    await act(async () => router.navigate(detailPath(organizationB, processB)))
    expect(
      await screen.findByRole('heading', { name: processB.title }),
    ).toBeInTheDocument()

    await act(async () => {
      resolveProcessA?.(response(200, processA))
      await pendingProcessA
    })

    expect(screen.getByRole('heading', { name: processB.title })).toBeInTheDocument()
    expect(
      screen.queryByRole('heading', { name: processA.title }),
    ).not.toBeInTheDocument()
  })

  it('ProcessDetail_SameIdAcrossOrganizations_UsesFullContextIdentity', async () => {
    let resolveOrganizationA: ((value: Response) => void) | undefined
    const pendingOrganizationA = new Promise<Response>((resolve) => {
      resolveOrganizationA = resolve
    })
    const sameIdProcessB = { ...processB, id: processA.id }
    const fetchMock = authenticatedFetch(
      [organizationA, organizationB],
      pendingOrganizationA,
      response(200, sameIdProcessB),
    )
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(detailPath(organizationA, processA))

    await screen.findByText('Carregando processo...')
    await act(async () =>
      router.navigate(detailPath(organizationB, sameIdProcessB)),
    )
    expect(
      await screen.findByRole('heading', { name: sameIdProcessB.title }),
    ).toBeInTheDocument()

    await act(async () => {
      resolveOrganizationA?.(response(200, processA))
      await pendingOrganizationA
    })

    expect(
      screen.getByRole('heading', { name: sameIdProcessB.title }),
    ).toBeInTheDocument()
    expect(
      screen.queryByRole('heading', { name: processA.title }),
    ).not.toBeInTheDocument()
  })

  it.each([
    ['same organization', organizationA, processB],
    ['new organization', organizationB, processB],
  ] as const)(
    'ProcessEdit_LatePutAfterNavigationTo%s_DoesNotAffectCurrentProcess',
    async (_scenario, targetOrganization, targetProcess) => {
      let resolveUpdate: ((value: Response) => void) | undefined
      const pendingUpdate = new Promise<Response>((resolve) => {
        resolveUpdate = resolve
      })
      const organizations =
        targetOrganization.id === organizationA.id
          ? [organizationA]
          : [organizationA, organizationB]
      const fetchMock = authenticatedFetch(
        organizations,
        response(200, processA),
        response(200, { requestToken: 'test-token' }),
        pendingUpdate,
        response(200, targetProcess),
      )
      vi.stubGlobal('fetch', fetchMock)
      const router = renderRoute(detailPath(organizationA, processA))

      await screen.findByRole('heading', { name: processA.title })
      openEditAndSubmit('Título atrasado')
      await screen.findByRole('button', { name: 'Salvando...' })
      await act(async () =>
        router.navigate(detailPath(targetOrganization, targetProcess)),
      )
      expect(
        await screen.findByRole('heading', { name: targetProcess.title }),
      ).toBeInTheDocument()

      await act(async () => {
        resolveUpdate?.(response(204))
        await pendingUpdate
      })

      await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(6))
      expect(
        screen.getByRole('heading', { name: targetProcess.title }),
      ).toBeInTheDocument()
      expect(screen.queryByText('Título atrasado')).not.toBeInTheDocument()
      expect(
        screen.queryByText('Processo atualizado com sucesso.'),
      ).not.toBeInTheDocument()
    },
  )

  it('ProcessEdit_AuthoritativeRefetchCompletesAfterNavigation_DoesNotOverwriteCurrentProcess', async () => {
    let resolveRefetchA: ((value: Response) => void) | undefined
    const pendingRefetchA = new Promise<Response>((resolve) => {
      resolveRefetchA = resolve
    })
    const updatedProcessA = { ...processA, title: 'Título confirmado em A' }
    const fetchMock = authenticatedFetch(
      [organizationA],
      response(200, processA),
      response(200, { requestToken: 'test-token' }),
      response(204),
      pendingRefetchA,
      response(200, processB),
    )
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(detailPath(organizationA, processA))

    await screen.findByRole('heading', { name: processA.title })
    openEditAndSubmit(updatedProcessA.title)
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(6))
    expect(screen.getByRole('button', { name: 'Salvando...' })).toBeDisabled()
    await act(async () => router.navigate(detailPath(organizationA, processB)))
    expect(
      await screen.findByRole('heading', { name: processB.title }),
    ).toBeInTheDocument()

    await act(async () => {
      resolveRefetchA?.(response(200, updatedProcessA))
      await pendingRefetchA
    })

    expect(screen.getByRole('heading', { name: processB.title })).toBeInTheDocument()
    expect(screen.queryByText(updatedProcessA.title)).not.toBeInTheDocument()
  })

  it('ProcessEdit_NavigationResetsDraftAndDoesNotRequireClientLookup', async () => {
    const fetchMock = authenticatedFetch(
      [organizationA],
      response(200, processA),
      response(200, processB),
    )
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(detailPath(organizationA, processA))

    await screen.findByRole('heading', { name: processA.title })
    fireEvent.click(screen.getByRole('button', { name: 'Editar processo' }))
    fireEvent.change(screen.getByLabelText('Título'), {
      target: { value: 'Rascunho exclusivo de A' },
    })

    await act(async () => router.navigate(detailPath(organizationA, processB)))
    expect(
      await screen.findByRole('heading', { name: processB.title }),
    ).toBeInTheDocument()
    expect(screen.queryByDisplayValue('Rascunho exclusivo de A')).not.toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'Editar processo' }))
    expect(screen.getByLabelText('Título')).toHaveValue(processB.title)
    expect(screen.getByText(processB.clientName)).toBeInTheDocument()
    expect(
      fetchMock.mock.calls.some(([url]) =>
        String(url).includes('/clients/lookup'),
      ),
    ).toBe(false)
  })
})

const cnjProcessNumber = '0001234-56.2026.8.26.0100'
const responsibleMember = {
  id: '99999999-9999-4999-8999-999999999999',
  displayName: 'Ana Responsável',
}
const operationalProcess: LegalProcess = {
  ...processA,
  processNumber: cnjProcessNumber,
  courtOrAuthority: '2ª Vara Cível de São Paulo',
  responsibleMembershipId: responsibleMember.id,
  responsibleDisplayName: responsibleMember.displayName,
}

function csrfResponse(): Response {
  return response(200, { requestToken: 'test-token' })
}

function memberLookupResponse(
  items: readonly { readonly id: string; readonly displayName: string }[],
): Response {
  return response(200, { items, pageNumber: 1, pageSize: 20, hasNext: false })
}

function processUrl(organization = organizationA, legalProcess = processA) {
  return `/api/organizations/${organization.id}/processes/${legalProcess.id}`
}

function mutationCalls(fetchMock: ReturnType<typeof vi.fn>) {
  return fetchMock.mock.calls
    .filter(([, init]) => (init as RequestInit | undefined)?.method === 'PUT')
    .map(([url, init]) => ({
      url: String(url),
      body: JSON.parse(String((init as RequestInit).body)) as unknown,
    }))
}

function metadataList(): HTMLElement {
  return screen.getByText('Criado em').closest('dl') as HTMLElement
}

function metadataValue(term: string): HTMLElement {
  return within(metadataList())
    .getByText(term)
    .parentElement!.querySelector('dd') as HTMLElement
}

async function renderOwnerDetail(
  legalProcess: LegalProcess,
  ...scopedResponses: readonly (Response | Promise<Response>)[]
) {
  const fetchMock = authenticatedFetch(
    [organizationA],
    response(200, legalProcess),
    ...scopedResponses,
  )
  vi.stubGlobal('fetch', fetchMock)
  renderRoute(detailPath(organizationA, legalProcess))
  await screen.findByRole('heading', { name: legalProcess.title })
  return fetchMock
}

function submitEditForm(values: {
  readonly title?: string
  readonly processNumber?: string
  readonly courtOrAuthority?: string
}) {
  fireEvent.click(screen.getByRole('button', { name: 'Editar processo' }))
  if (values.title !== undefined) {
    fireEvent.change(screen.getByLabelText('Título'), {
      target: { value: values.title },
    })
  }
  if (values.processNumber !== undefined) {
    fireEvent.change(screen.getByLabelText('Número do processo'), {
      target: { value: values.processNumber },
    })
  }
  if (values.courtOrAuthority !== undefined) {
    fireEvent.change(screen.getByLabelText('Órgão/tribunal'), {
      target: { value: values.courtOrAuthority },
    })
  }
  fireEvent.submit(
    screen.getByRole('button', { name: 'Salvar alterações' }).closest('form')!,
  )
}

describe('Process detail operations (Phase 9B.2D-c)', () => {
  it('ProcessDetail_Metadata_ShowsOperationalValues', async () => {
    await renderOwnerDetail({ ...operationalProcess, status: 'suspended' })

    const status = within(metadataValue('Status')).getByText('Suspenso')
    expect(status).toHaveClass('process-status', 'is-pending')
    expect(metadataValue('Cliente')).toHaveTextContent(processA.clientName)
    expect(metadataValue('Número')).toHaveTextContent(cnjProcessNumber)
    expect(metadataValue('Órgão/tribunal')).toHaveTextContent(
      '2ª Vara Cível de São Paulo',
    )
    expect(metadataValue('Responsável')).toHaveTextContent(
      responsibleMember.displayName,
    )
    expect(metadataValue('Criado em')).toHaveTextContent(/12\/08\/2026/)
    expect(within(metadataList()).queryByText('Criado por')).not.toBeInTheDocument()
  })

  it('ProcessDetail_Metadata_ShowsFallbacksForEmptyOperationalFields', async () => {
    await renderOwnerDetail(processA)

    expect(
      within(metadataValue('Status')).getByText('Em andamento'),
    ).toHaveClass('process-status', 'is-active')
    expect(metadataValue('Número')).toHaveTextContent('Não informado')
    expect(metadataValue('Órgão/tribunal')).toHaveTextContent('Não informado')
    expect(metadataValue('Responsável')).toHaveTextContent('Sem responsável')
  })

  it('ProcessDetail_Member_SeesMetadataWithoutOperationalControls', async () => {
    const member = { ...organizationA, role: 'Member' as const }
    vi.stubGlobal(
      'fetch',
      authenticatedFetch([member], response(200, operationalProcess)),
    )
    renderRoute(detailPath(member, operationalProcess))
    await screen.findByRole('heading', { name: operationalProcess.title })

    expect(metadataValue('Número')).toHaveTextContent(cnjProcessNumber)
    expect(metadataValue('Responsável')).toHaveTextContent(
      responsibleMember.displayName,
    )
    for (const name of [
      'Suspender',
      'Encerrar',
      'Retomar',
      'Reabrir',
      'Editar processo',
      'Alterar responsável',
    ]) {
      expect(screen.queryByRole('button', { name })).not.toBeInTheDocument()
    }
    expect(
      screen.queryByRole('heading', { name: 'Responsável' }),
    ).not.toBeInTheDocument()
  })

  it.each([
    ['inProgress', ['Suspender', 'Encerrar']],
    ['suspended', ['Retomar', 'Encerrar']],
    ['closed', ['Reabrir']],
  ] as const)(
    'ProcessStatus_%s_OffersOnlyValidTransitions',
    async (status, expectedActions) => {
      await renderOwnerDetail({ ...processA, status })

      const actions = screen
        .getByRole('button', { name: 'Editar processo' })
        .closest('.process-detail-actions') as HTMLElement
      expect(
        within(actions)
          .getAllByRole('button')
          .map((button) => button.textContent),
      ).toEqual([...expectedActions, 'Editar processo'])
    },
  )

  it.each([
    ['inProgress', 'Suspender', 'suspended', 'Processo suspenso.'],
    ['inProgress', 'Encerrar', 'closed', 'Processo encerrado.'],
    ['suspended', 'Retomar', 'inProgress', 'Processo retomado.'],
    ['suspended', 'Encerrar', 'closed', 'Processo encerrado.'],
    ['closed', 'Reabrir', 'inProgress', 'Processo reaberto.'],
  ] as const)(
    'ProcessStatus_%s_%s_SendsTargetStatusAndReloads',
    async (status, actionLabel, target, successMessage) => {
      const fetchMock = await renderOwnerDetail(
        { ...processA, status },
        csrfResponse(),
        response(204),
        response(200, { ...processA, status: target }),
      )

      fireEvent.click(screen.getByRole('button', { name: actionLabel }))

      expect(await screen.findByText(successMessage)).toBeInTheDocument()
      expect(mutationCalls(fetchMock)).toEqual([
        { url: `${processUrl()}/status`, body: { status: target } },
      ])
      expect(fetchMock).toHaveBeenCalledTimes(6)
      expect(fetchMock.mock.calls[5]?.[0]).toBe(processUrl())
      expect(fetchMock.mock.calls[5]?.[1]?.method).toBe('GET')
      expect(
        within(metadataValue('Status')).getByText(
          target === 'inProgress'
            ? 'Em andamento'
            : target === 'suspended'
              ? 'Suspenso'
              : 'Encerrado',
        ),
      ).toBeInTheDocument()
    },
  )

  it('ProcessStatus_PendingMutation_DisablesAllActions', async () => {
    let resolveStatus: ((value: Response) => void) | undefined
    const pendingStatus = new Promise<Response>((resolve) => {
      resolveStatus = resolve
    })
    const fetchMock = await renderOwnerDetail(
      processA,
      csrfResponse(),
      pendingStatus,
      response(200, { ...processA, status: 'suspended' }),
    )

    fireEvent.click(screen.getByRole('button', { name: 'Suspender' }))

    await waitFor(() =>
      expect(screen.getByRole('button', { name: 'Suspender' })).toBeDisabled(),
    )
    expect(screen.getByRole('button', { name: 'Encerrar' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Editar processo' })).toBeDisabled()
    expect(
      screen.getByRole('button', { name: 'Alterar responsável' }),
    ).toBeDisabled()
    fireEvent.click(screen.getByRole('button', { name: 'Encerrar' }))
    await waitFor(() => expect(mutationCalls(fetchMock)).toHaveLength(1))

    await act(async () => {
      resolveStatus?.(response(204))
      await pendingStatus
    })
    expect(await screen.findByRole('button', { name: 'Retomar' })).toBeEnabled()
    expect(mutationCalls(fetchMock)).toHaveLength(1)
  })

  it('ProcessStatus_Conflict_ShowsGenericMessageAndReloadsWithoutServerText', async () => {
    const fetchMock = await renderOwnerDetail(
      { ...operationalProcess, status: 'closed' },
      csrfResponse(),
      response(409, {
        title: 'Related responsible member unavailable',
        detail: 'private conflict detail',
      }),
      response(200, { ...operationalProcess, status: 'closed' }),
    )

    fireEvent.click(screen.getByRole('button', { name: 'Reabrir' }))

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent(
      'Não foi possível alterar o status. Os dados foram atualizados; se o processo tiver um responsável indisponível, altere o responsável antes de reabrir.',
    )
    expect(alert).not.toHaveTextContent('private conflict detail')
    expect(fetchMock).toHaveBeenCalledTimes(6)
    expect(fetchMock.mock.calls[5]?.[1]?.method).toBe('GET')
    expect(screen.getByRole('button', { name: 'Reabrir' })).toBeEnabled()
  })

  it('ProcessEdit_OnlyTitleChanged_SendsOnlyTitleRequest', async () => {
    const fetchMock = await renderOwnerDetail(
      operationalProcess,
      csrfResponse(),
      response(204),
      response(200, { ...operationalProcess, title: 'Título revisado' }),
    )

    submitEditForm({ title: '  Título revisado  ' })

    expect(
      await screen.findByRole('heading', { name: 'Título revisado' }),
    ).toBeInTheDocument()
    expect(mutationCalls(fetchMock)).toEqual([
      { url: processUrl(), body: { title: 'Título revisado' } },
    ])
    expect(screen.getByText('Processo atualizado com sucesso.')).toBeInTheDocument()
  })

  it('ProcessEdit_OnlyDetailsChanged_SendsBothTrimmedDetailValues', async () => {
    const updatedProcess = {
      ...operationalProcess,
      processNumber: '5000000-00.2026.8.26.0001',
      courtOrAuthority: null,
    }
    const fetchMock = await renderOwnerDetail(
      operationalProcess,
      csrfResponse(),
      response(204),
      response(200, updatedProcess),
    )

    submitEditForm({
      processNumber: '  5000000-00.2026.8.26.0001  ',
      courtOrAuthority: '   ',
    })

    expect(
      await screen.findByText('Processo atualizado com sucesso.'),
    ).toBeInTheDocument()
    expect(mutationCalls(fetchMock)).toEqual([
      {
        url: `${processUrl()}/details`,
        body: {
          processNumber: '5000000-00.2026.8.26.0001',
          courtOrAuthority: null,
        },
      },
    ])
    expect(metadataValue('Órgão/tribunal')).toHaveTextContent('Não informado')
  })

  it('ProcessEdit_TitleAndDetailsChanged_SendsDetailsBeforeTitle', async () => {
    const updatedProcess = {
      ...processA,
      title: 'Título e número',
      processNumber: cnjProcessNumber,
    }
    const fetchMock = await renderOwnerDetail(
      processA,
      csrfResponse(),
      response(204),
      response(204),
      response(200, updatedProcess),
    )

    submitEditForm({ title: 'Título e número', processNumber: cnjProcessNumber })

    expect(
      await screen.findByRole('heading', { name: updatedProcess.title }),
    ).toBeInTheDocument()
    expect(mutationCalls(fetchMock)).toEqual([
      {
        url: `${processUrl()}/details`,
        body: { processNumber: cnjProcessNumber, courtOrAuthority: null },
      },
      { url: processUrl(), body: { title: 'Título e número' } },
    ])
    expect(fetchMock.mock.calls.at(-1)?.[1]?.method).toBe('GET')
  })

  it('ProcessEdit_DetailsConflict_DoesNotSendTitleAndMarksNumberField', async () => {
    const fetchMock = await renderOwnerDetail(
      processA,
      csrfResponse(),
      response(409, { detail: 'private duplicate detail' }),
      response(200, processA),
    )

    submitEditForm({ title: 'Título não enviado', processNumber: cnjProcessNumber })

    expect(
      await screen.findByText(
        'Já existe um processo com este número nesta organização.',
      ),
    ).toBeInTheDocument()
    expect(screen.getByLabelText('Número do processo')).toHaveAttribute(
      'aria-invalid',
      'true',
    )
    expect(mutationCalls(fetchMock)).toEqual([
      {
        url: `${processUrl()}/details`,
        body: { processNumber: cnjProcessNumber, courtOrAuthority: null },
      },
    ])
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(6))
    expect(fetchMock.mock.calls[5]?.[1]?.method).toBe('GET')
    expect(screen.queryByText('private duplicate detail')).not.toBeInTheDocument()
    expect(screen.getByLabelText('Título')).toHaveValue('Título não enviado')
  })

  it('ProcessEdit_TitleFailsAfterDetails_ReportsPartialSuccessAndReloads', async () => {
    const savedDetails = { ...processA, processNumber: cnjProcessNumber }
    const fetchMock = await renderOwnerDetail(
      processA,
      csrfResponse(),
      response(204),
      response(500, { detail: 'private server detail' }),
      response(200, savedDetails),
    )

    submitEditForm({ title: 'Título que falha', processNumber: cnjProcessNumber })

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent(
      'Número e órgão foram salvos, mas o título não pôde ser salvo.',
    )
    expect(alert).not.toHaveTextContent('private server detail')
    expect(mutationCalls(fetchMock).map(({ url }) => url)).toEqual([
      `${processUrl()}/details`,
      processUrl(),
    ])
    expect(fetchMock).toHaveBeenCalledTimes(7)
    expect(metadataValue('Número')).toHaveTextContent(cnjProcessNumber)
    expect(screen.getByRole('heading', { name: processA.title })).toBeInTheDocument()
  })

  it('ProcessEdit_NothingChanged_ClosesWithoutRequest', async () => {
    const fetchMock = await renderOwnerDetail(operationalProcess)

    submitEditForm({ title: `  ${operationalProcess.title} ` })

    await waitFor(() =>
      expect(screen.queryByLabelText('Título')).not.toBeInTheDocument(),
    )
    expect(fetchMock).toHaveBeenCalledTimes(3)
  })

  it('ProcessEdit_OverlongOptionalFields_AreRejectedLocally', async () => {
    const fetchMock = await renderOwnerDetail(processA)

    fireEvent.click(screen.getByRole('button', { name: 'Editar processo' }))
    const numberInput = screen.getByLabelText('Número do processo')
    const courtInput = screen.getByLabelText('Órgão/tribunal')
    expect(numberInput).toHaveAttribute('maxLength', '100')
    expect(courtInput).toHaveAttribute('maxLength', '200')
    fireEvent.change(numberInput, { target: { value: '1'.repeat(101) } })
    fireEvent.change(courtInput, { target: { value: 'x'.repeat(201) } })
    fireEvent.submit(numberInput.closest('form')!)

    expect(
      await screen.findByText('O número deve ter no máximo 100 caracteres.'),
    ).toBeInTheDocument()
    expect(
      screen.getByText('O órgão/tribunal deve ter no máximo 200 caracteres.'),
    ).toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(3)
  })

  it('ProcessResponsible_PickerLoadsOnlyForOtherPerson', async () => {
    const fetchMock = await renderOwnerDetail(
      processA,
      memberLookupResponse([responsibleMember]),
    )

    expect(screen.queryByText(/Responsável atual/)).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Alterar responsável' }))
    const mode = screen.getByLabelText('Novo responsável')
    expect(mode).toHaveValue('unassigned')
    expect(
      within(mode).getAllByRole('option').map((option) => option.textContent),
    ).toEqual(['Sem responsável', 'Eu', 'Outra pessoa'])

    fireEvent.change(mode, { target: { value: 'self' } })
    expect(screen.queryByLabelText('Buscar novo responsável')).not.toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(3)

    fireEvent.change(mode, { target: { value: 'other' } })
    expect(
      await screen.findByRole('button', { name: responsibleMember.displayName }),
    ).toBeInTheDocument()
    expect(fetchMock.mock.calls[3]?.[0]).toBe(
      `/api/organizations/${organizationA.id}/members/lookup?search=&pageNumber=1&pageSize=20`,
    )
  })

  it.each([
    ['unassigned', operationalProcess, null],
    ['self', processA, organizationA.membershipId],
  ] as const)(
    'ProcessResponsible_%sMode_SendsExpectedMembership',
    async (mode, initialProcess, expectedMembershipId) => {
      const opensWithCurrentMember = initialProcess.responsibleMembershipId !== null
      const fetchMock = await renderOwnerDetail(
        initialProcess,
        ...(opensWithCurrentMember
          ? [memberLookupResponse([responsibleMember])]
          : []),
        csrfResponse(),
        response(204),
        response(200, initialProcess),
      )

      fireEvent.click(
        screen.getByRole('button', { name: 'Alterar responsável' }),
      )
      if (opensWithCurrentMember) {
        expect(screen.getByLabelText('Novo responsável')).toHaveValue('other')
        expect(
          await screen.findByRole('button', {
            name: responsibleMember.displayName,
            pressed: true,
          }),
        ).toBeInTheDocument()
      }
      fireEvent.change(screen.getByLabelText('Novo responsável'), {
        target: { value: mode },
      })
      fireEvent.click(screen.getByRole('button', { name: 'Salvar responsável' }))

      expect(
        await screen.findByText('Responsável atualizado com sucesso.'),
      ).toBeInTheDocument()
      expect(mutationCalls(fetchMock)).toEqual([
        {
          url: `${processUrl()}/responsible`,
          body: { responsibleMembershipId: expectedMembershipId },
        },
      ])
      expect(fetchMock.mock.calls.at(-1)?.[1]?.method).toBe('GET')
    },
  )

  it('ProcessResponsible_OtherPerson_RequiresSelectionThenSendsSelectedMember', async () => {
    const fetchMock = await renderOwnerDetail(
      processA,
      memberLookupResponse([responsibleMember]),
      csrfResponse(),
      response(204),
      response(200, operationalProcess),
    )

    fireEvent.click(screen.getByRole('button', { name: 'Alterar responsável' }))
    fireEvent.change(screen.getByLabelText('Novo responsável'), {
      target: { value: 'other' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Salvar responsável' }))
    expect(
      await screen.findByText('Selecione uma pessoa responsável.'),
    ).toBeInTheDocument()
    expect(mutationCalls(fetchMock)).toHaveLength(0)

    fireEvent.click(
      await screen.findByRole('button', { name: responsibleMember.displayName }),
    )
    fireEvent.click(screen.getByRole('button', { name: 'Salvar responsável' }))

    expect(
      await screen.findByText('Responsável atualizado com sucesso.'),
    ).toBeInTheDocument()
    expect(mutationCalls(fetchMock)).toEqual([
      {
        url: `${processUrl()}/responsible`,
        body: { responsibleMembershipId: responsibleMember.id },
      },
    ])
    expect(metadataValue('Responsável')).toHaveTextContent(
      responsibleMember.displayName,
    )
  })

  it('ProcessResponsible_Unavailable_ClearsSelectionAndAsksForAnotherPerson', async () => {
    const fetchMock = await renderOwnerDetail(
      processA,
      memberLookupResponse([responsibleMember]),
      csrfResponse(),
      response(400, {
        title: 'Related responsible member unavailable',
        detail: 'private membership detail',
      }),
      response(200, processA),
      memberLookupResponse([]),
    )

    fireEvent.click(screen.getByRole('button', { name: 'Alterar responsável' }))
    fireEvent.change(screen.getByLabelText('Novo responsável'), {
      target: { value: 'other' },
    })
    fireEvent.click(
      await screen.findByRole('button', { name: responsibleMember.displayName }),
    )
    expect(screen.getByText(/Responsável selecionado:/)).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Salvar responsável' }))

    expect(
      await screen.findByText(
        'O responsável selecionado não está mais disponível. Escolha outra pessoa.',
      ),
    ).toBeInTheDocument()
    expect(screen.queryByText(/Responsável selecionado:/)).not.toBeInTheDocument()
    expect(screen.queryByText('private membership detail')).not.toBeInTheDocument()
    expect(screen.getByLabelText('Novo responsável')).toHaveValue('other')
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(8))
    expect(fetchMock.mock.calls[6]?.[0]).toBe(processUrl())
  })
})
