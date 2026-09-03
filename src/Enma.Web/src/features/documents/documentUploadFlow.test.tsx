import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
} from '@testing-library/react'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createAppRoutes } from '../../app/router'
import { clearCsrfToken } from '../authentication/csrfClient'
import { createEmailVerificationFlow } from '../email-verification/emailVerificationService'
import type { OrganizationNavigationItem } from '../organizations/organizationTypes'

const organizationA: OrganizationNavigationItem = {
  id: '11111111-1111-4111-8111-111111111111',
  membershipId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa1',
  name: 'Organização Alfa',
  role: 'Member',
}

const organizationB: OrganizationNavigationItem = {
  id: '99999999-9999-4999-8999-999999999999',
  membershipId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbb2',
  name: 'Organização Beta',
  role: 'Member',
}

const client = {
  id: '22222222-2222-4222-8222-222222222222',
  name: 'Cliente Exemplo',
}

const process = {
  id: '33333333-3333-4333-8333-333333333333',
  title: 'Ação de cobrança',
  clientName: client.name,
}

function response(status: number, body?: unknown): Response {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
  })
}

function documentsResponse(): Response {
  return response(200, {
    items: [],
    pageNumber: 1,
    pageSize: 20,
    hasNext: false,
  })
}

type ScopedHandler = (
  url: URL,
  init: RequestInit | undefined,
) => Response | Promise<Response> | undefined

function createFetch(
  handler?: ScopedHandler,
  organizations: readonly OrganizationNavigationItem[] = [organizationA],
) {
  let organizationRequestCount = 0

  return vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), 'https://enma.test')

    if (url.pathname === '/api/me/organizations') {
      organizationRequestCount += 1
      return Promise.resolve(
        organizationRequestCount === 1
          ? response(200)
          : response(200, { items: organizations }),
      )
    }

    const handled = handler?.(url, init)
    if (handled) return Promise.resolve(handled)

    if (url.pathname === '/api/auth/csrf') {
      return Promise.resolve(response(200, { requestToken: 'csrf-token' }))
    }
    if (url.pathname.endsWith('/clients/lookup')) {
      return Promise.resolve(response(200, {
        items: [client], pageNumber: 1, pageSize: 20, hasNext: false,
      }))
    }
    if (url.pathname.endsWith('/processes/lookup')) {
      return Promise.resolve(response(200, {
        items: [process], pageNumber: 1, pageSize: 20, hasNext: false,
      }))
    }
    if (url.pathname.endsWith('/documents') && (init?.method ?? 'GET') === 'GET') {
      return Promise.resolve(documentsResponse())
    }
    if (url.pathname.endsWith('/documents') && init?.method === 'POST') {
      return Promise.resolve(response(201, {
        id: '44444444-4444-4444-8444-444444444444',
      }))
    }

    throw new Error(`Unexpected request: ${init?.method ?? 'GET'} ${url.pathname}`)
  })
}

function documentsPath(organizationId = organizationA.id): string {
  return `/organizations/${organizationId}/documents`
}

function renderDocuments() {
  const router = createMemoryRouter(
    createAppRoutes(createEmailVerificationFlow(undefined)),
    { initialEntries: [documentsPath()] },
  )
  render(<RouterProvider router={router} />)
  return router
}

async function openUploadAndSelect(file = new File(
  ['synthetic document'],
  'petição.pdf',
  { type: 'application/pdf' },
)): Promise<File> {
  await screen.findByRole('heading', { name: 'Nenhum documento disponível' })
  fireEvent.click(screen.getByRole('button', { name: 'Enviar documento' }))
  const input = screen.getByLabelText('Arquivo')
  fireEvent.change(input, { target: { files: [file] } })
  expect(screen.getByText(file.name)).toBeInTheDocument()
  return file
}

function getUploadCall(fetchMock: ReturnType<typeof vi.fn>) {
  return fetchMock.mock.calls.find(([, init]) => init?.method === 'POST')
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

describe('Documents D2 upload flow', () => {
  it('GeneralUpload_UsesDirectFileFormDataCsrfAndRefreshesThenResets', async () => {
    let documentListRequests = 0
    const fetchMock = createFetch((url, init) => {
      if (url.pathname.endsWith('/documents') && (init?.method ?? 'GET') === 'GET') {
        documentListRequests += 1
        return documentsResponse()
      }
      return undefined
    })
    vi.stubGlobal('fetch', fetchMock)
    const arrayBufferSpy = vi.spyOn(File.prototype, 'arrayBuffer')

    renderDocuments()
    const file = await openUploadAndSelect()
    expect(screen.getByRole('heading', { name: 'Enviar documento' })).toBeInTheDocument()
    expect(screen.getByRole('radio', { name: 'Geral' })).toBeChecked()

    fireEvent.submit(screen.getByRole('button', { name: 'Enviar documento' }).closest('form')!)

    expect(await screen.findByText('Documento enviado com sucesso.')).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Enviar documento' })).not.toBeInTheDocument()
    await waitFor(() => expect(documentListRequests).toBe(2))

    const uploadCall = getUploadCall(fetchMock)
    expect(uploadCall).toBeDefined()
    const [url, init] = uploadCall!
    expect(String(url)).toBe(`/api/organizations/${organizationA.id}/documents`)
    expect(init?.credentials).toBe('same-origin')
    expect(init?.headers).toEqual({ 'X-CSRF-TOKEN': 'csrf-token' })
    expect(init?.headers).not.toHaveProperty('Content-Type')
    expect(init?.body).toBeInstanceOf(FormData)

    const formData = init?.body as FormData
    expect([...formData.keys()]).toEqual(['file'])
    expect(formData.get('file')).toBe(file)
    expect(formData.has('organizationId')).toBe(false)
    expect(formData.has('userId')).toBe(false)
    expect(formData.has('membershipId')).toBe(false)
    expect(formData.has('role')).toBe(false)
    expect(formData.has('storedObjectKey')).toBe(false)
    expect(formData.has('contentHash')).toBe(false)
    expect(formData.has('sizeBytes')).toBe(false)
    expect(formData.has('createdAt')).toBe(false)
    expect(arrayBufferSpy).not.toHaveBeenCalled()
  })

  it('ClientUpload_SendsClientIdOnly', async () => {
    const fetchMock = createFetch()
    vi.stubGlobal('fetch', fetchMock)
    renderDocuments()
    await openUploadAndSelect()

    fireEvent.click(screen.getByRole('radio', { name: 'Cliente' }))
    fireEvent.click(screen.getByRole('button', { name: 'Selecionar cliente' }))
    fireEvent.click(await screen.findByRole('button', { name: client.name }))
    fireEvent.submit(screen.getByRole('button', { name: 'Enviar documento' }).closest('form')!)

    await screen.findByText('Documento enviado com sucesso.')
    const formData = getUploadCall(fetchMock)?.[1]?.body as FormData
    expect(formData.get('clientId')).toBe(client.id)
    expect(formData.has('processId')).toBe(false)
  })

  it('ProcessUpload_SendsProcessIdOnly', async () => {
    const fetchMock = createFetch()
    vi.stubGlobal('fetch', fetchMock)
    renderDocuments()
    await openUploadAndSelect()

    fireEvent.click(screen.getByRole('radio', { name: 'Processo' }))
    fireEvent.click(screen.getByRole('button', { name: 'Selecionar processo' }))
    fireEvent.click(await screen.findByRole('button', { name: new RegExp(process.title) }))
    fireEvent.submit(screen.getByRole('button', { name: 'Enviar documento' }).closest('form')!)

    await screen.findByText('Documento enviado com sucesso.')
    const formData = getUploadCall(fetchMock)?.[1]?.body as FormData
    expect(formData.get('processId')).toBe(process.id)
    expect(formData.has('clientId')).toBe(false)
  })

  it('ClassificationSwitch_ClearsIncompatibleSelectionAndRequiresANewOne', async () => {
    const fetchMock = createFetch()
    vi.stubGlobal('fetch', fetchMock)
    renderDocuments()
    await openUploadAndSelect()

    fireEvent.click(screen.getByRole('radio', { name: 'Cliente' }))
    fireEvent.click(screen.getByRole('button', { name: 'Selecionar cliente' }))
    fireEvent.click(await screen.findByRole('button', { name: client.name }))
    expect(screen.getByText(client.name)).toBeInTheDocument()

    fireEvent.click(screen.getByRole('radio', { name: 'Processo' }))
    fireEvent.click(screen.getByRole('radio', { name: 'Cliente' }))
    expect(screen.getByText(/Cliente:/).closest('p')).toHaveTextContent('não selecionado')

    fireEvent.submit(screen.getByRole('button', { name: 'Enviar documento' }).closest('form')!)
    expect(await screen.findByText('Selecione um cliente ativo.')).toBeInTheDocument()
    expect(getUploadCall(fetchMock)).toBeUndefined()
  })

  it('FileValidation_BlocksUnsupportedAndOversizedFiles', async () => {
    const fetchMock = createFetch()
    vi.stubGlobal('fetch', fetchMock)
    renderDocuments()

    const unsupported = new File(['text'], 'anotação.txt', { type: 'text/plain' })
    await openUploadAndSelect(unsupported)
    expect(screen.getByText('Selecione um arquivo PDF, DOCX, XLSX, PNG, JPG ou JPEG.')).toBeInTheDocument()
    fireEvent.submit(screen.getByRole('button', { name: 'Enviar documento' }).closest('form')!)
    expect(getUploadCall(fetchMock)).toBeUndefined()

    const oversized = new File(['x'], 'grande.pdf', { type: 'application/pdf' })
    Object.defineProperty(oversized, 'size', { value: 26_214_401 })
    fireEvent.change(screen.getByLabelText('Arquivo'), {
      target: { files: [oversized] },
    })
    expect(await screen.findByText('O arquivo excede o limite de 25 MiB.')).toBeInTheDocument()
    fireEvent.submit(screen.getByRole('button', { name: 'Enviar documento' }).closest('form')!)
    expect(getUploadCall(fetchMock)).toBeUndefined()
  })

  it('PendingUpload_DisablesSubmitAndPreventsDuplicateRequest', async () => {
    const pending = new Promise<Response>(() => undefined)
    const fetchMock = createFetch((url, init) =>
      url.pathname.endsWith('/documents') && init?.method === 'POST'
        ? pending
        : undefined,
    )
    vi.stubGlobal('fetch', fetchMock)
    renderDocuments()
    await openUploadAndSelect()

    const submit = screen.getByRole('button', { name: 'Enviar documento' })
    fireEvent.submit(submit.closest('form')!)
    expect(await screen.findByRole('button', { name: 'Enviando...' })).toBeDisabled()
    fireEvent.submit(submit.closest('form')!)

    await waitFor(() => {
      expect(fetchMock.mock.calls.filter(([, init]) => init?.method === 'POST')).toHaveLength(1)
    })
  })

  it('OrganizationSwitch_DiscardsLateUploadSuccess', async () => {
    let resolveUpload!: (value: Response) => void
    const pendingUpload = new Promise<Response>((resolve) => {
      resolveUpload = resolve
    })
    const fetchMock = createFetch(
      (url, init) =>
        url.pathname === `/api/organizations/${organizationA.id}/documents` &&
        init?.method === 'POST'
          ? pendingUpload
          : undefined,
      [organizationA, organizationB],
    )
    vi.stubGlobal('fetch', fetchMock)
    const router = renderDocuments()
    await openUploadAndSelect()
    fireEvent.submit(screen.getByRole('button', { name: 'Enviar documento' }).closest('form')!)
    await screen.findByRole('button', { name: 'Enviando...' })

    await act(async () => router.navigate(documentsPath(organizationB.id)))
    expect(
      await screen.findByRole('heading', { name: 'Nenhum documento disponível' }),
    ).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Enviar documento' })).not.toBeInTheDocument()

    await act(async () => {
      resolveUpload(response(201, { id: '44444444-4444-4444-8444-444444444444' }))
      await pendingUpload
    })
    expect(screen.queryByText('Documento enviado com sucesso.')).not.toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Nenhum documento disponível' })).toBeInTheDocument()
  })

  it.each([
    [400, undefined, 'O arquivo ou a classificação não pôde ser aceito.'],
    [403, undefined, 'Você não tem permissão para enviar documentos nesta organização.'],
    [413, undefined, 'O arquivo ou a requisição excede o limite de 25 MiB.'],
    [503, { title: 'Document upload unavailable' }, 'O serviço de envio está temporariamente indisponível.'],
  ])('UploadError_%i_UsesSafeMessage', async (status, body, expected) => {
    const fetchMock = createFetch((url, init) =>
      url.pathname.endsWith('/documents') && init?.method === 'POST'
        ? response(status, body)
        : undefined,
    )
    vi.stubGlobal('fetch', fetchMock)
    renderDocuments()
    await openUploadAndSelect()
    fireEvent.submit(screen.getByRole('button', { name: 'Enviar documento' }).closest('form')!)

    expect(await screen.findByText(new RegExp(expected))).toBeInTheDocument()
  })

  it('UploadError_404_ClearsStaleRelatedSelection', async () => {
    const fetchMock = createFetch((url, init) =>
      url.pathname.endsWith('/documents') && init?.method === 'POST'
        ? response(404)
        : undefined,
    )
    vi.stubGlobal('fetch', fetchMock)
    renderDocuments()
    await openUploadAndSelect()
    fireEvent.click(screen.getByRole('radio', { name: 'Cliente' }))
    fireEvent.click(screen.getByRole('button', { name: 'Selecionar cliente' }))
    fireEvent.click(await screen.findByRole('button', { name: client.name }))
    fireEvent.submit(screen.getByRole('button', { name: 'Enviar documento' }).closest('form')!)

    expect(await screen.findByText('O cliente ou processo selecionado não está mais disponível.')).toBeInTheDocument()
    expect(screen.getByText(/Cliente:/).closest('p')).toHaveTextContent('não selecionado')
  })

  it('UploadError_AmbiguousOutcomeBlocksRetryAndInstructsListCheck', async () => {
    const fetchMock = createFetch((url, init) =>
      url.pathname.endsWith('/documents') && init?.method === 'POST'
        ? response(500, {
            title: 'Document upload outcome unknown',
            detail: 'The upload may have succeeded. Do not retry automatically.',
            traceId: 'safe-synthetic-trace',
          })
        : undefined,
    )
    vi.stubGlobal('fetch', fetchMock)
    renderDocuments()
    await openUploadAndSelect()
    fireEvent.submit(screen.getByRole('button', { name: 'Enviar documento' }).closest('form')!)

    const message = await screen.findByText(/resultado é incerto/)
    expect(message).toHaveTextContent('Não envie novamente agora')
    expect(message).toHaveTextContent('confira Documentos')
    expect(screen.getByRole('button', { name: 'Enviar documento' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Atualizar lista de documentos' })).toBeEnabled()
    expect(screen.queryByText('safe-synthetic-trace')).not.toBeInTheDocument()

    fireEvent.change(screen.getByLabelText('Arquivo'), {
      target: {
        files: [new File(['replacement'], 'substituto.pdf', {
          type: 'application/pdf',
        })],
      },
    })
    expect(screen.getByText(/resultado é incerto/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Enviar documento' })).toBeDisabled()
  })
})
