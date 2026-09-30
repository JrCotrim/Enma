import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { clearCsrfToken } from '../authentication/csrfClient'
import {
  getLegalProcessStatusLabel,
  getLegalProcessStatusTone,
} from './legalProcessFormatting'
import {
  changeLegalProcessDetails,
  changeLegalProcessResponsible,
  changeLegalProcessStatus,
  createLegalProcess,
  getLegalProcess,
  LegalProcessRequestError,
  listLegalProcesses,
  type LegalProcessRequestFailure,
} from './legalProcessService'

const organizationId = '11111111-1111-4111-8111-111111111111'
const processId = '22222222-2222-4222-8222-222222222222'
const clientId = '33333333-3333-4333-8333-333333333333'
const membershipId = '44444444-4444-4444-8444-444444444444'
const processesPath = `/api/organizations/${organizationId}/processes`

const processWithResponsible = {
  id: processId,
  title: 'Ação de cobrança',
  clientId,
  clientName: 'Cliente Exemplo',
  createdAt: '2026-08-12T14:30:00Z',
  processNumber: '0001234-56.2026.8.26.0100',
  status: 'suspended',
  courtOrAuthority: '1ª Vara Cível',
  responsibleMembershipId: membershipId,
  responsibleDisplayName: 'Maria Souza',
}

const processWithoutResponsible = {
  ...processWithResponsible,
  processNumber: null,
  status: 'inProgress',
  courtOrAuthority: null,
  responsibleMembershipId: null,
  responsibleDisplayName: null,
}

function response(status: number, body?: unknown): Response {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
  })
}

function listBody(items: readonly unknown[], overrides: Record<string, unknown> = {}) {
  return { items, pageNumber: 1, pageSize: 20, hasNext: false, ...overrides }
}

function csrfResponse(token = 'csrf-token'): Response {
  return response(200, { requestToken: token })
}

async function expectFailure(
  promise: Promise<unknown>,
  failure: LegalProcessRequestFailure,
) {
  const error = await promise.catch((caught: unknown) => caught)
  expect(error).toBeInstanceOf(LegalProcessRequestError)
  expect((error as LegalProcessRequestError).failure).toBe(failure)
}

function requestedUrl(fetchMock: ReturnType<typeof vi.fn>, index = 0): string {
  return String(fetchMock.mock.calls[index]?.[0])
}

beforeEach(clearCsrfToken)
afterEach(() => {
  clearCsrfToken()
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

describe('legal process parser', () => {
  it('GetLegalProcess_ParsesOperationalFieldsWithResponsible', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(response(200, processWithResponsible)))

    await expect(getLegalProcess(organizationId, processId, vi.fn())).resolves.toEqual(
      processWithResponsible,
    )
  })

  it('ListLegalProcesses_ParsesNullOperationalFieldsAndHasNext', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        response(200, listBody([processWithoutResponsible], { hasNext: true })),
      ),
    )

    await expect(listLegalProcesses(organizationId, 1, 20, vi.fn())).resolves.toEqual({
      items: [processWithoutResponsible],
      pageNumber: 1,
      pageSize: 20,
      hasNext: true,
    })
  })

  it.each([
    ['status desconhecido', { status: 'archived' }],
    ['status em PascalCase', { status: 'InProgress' }],
    ['status ausente', { status: undefined }],
    ['processNumber ausente', { processNumber: undefined }],
    ['processNumber vazio', { processNumber: '' }],
    ['courtOrAuthority ausente', { courtOrAuthority: undefined }],
    ['responsável sem nome', { responsibleDisplayName: null }],
    ['nome sem responsável', { responsibleMembershipId: null }],
    ['responsável com id inválido', { responsibleMembershipId: 'not-a-guid' }],
    ['responsibleMembershipId ausente', { responsibleMembershipId: undefined }],
  ])('GetLegalProcess_%s_FailsAsUnexpected', async (_, overrides) => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(response(200, { ...processWithResponsible, ...overrides })),
    )

    await expectFailure(getLegalProcess(organizationId, processId, vi.fn()), 'unexpected')
  })

  it('ListLegalProcesses_WithInvalidItemStatus_FailsAsUnexpected', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        response(200, listBody([{ ...processWithoutResponsible, status: 'archived' }])),
      ),
    )

    await expectFailure(listLegalProcesses(organizationId, 1, 20, vi.fn()), 'unexpected')
  })

  it('ListLegalProcesses_WithoutHasNext_FailsAsUnexpected', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        response(200, { items: [], pageNumber: 1, pageSize: 20 }),
      ),
    )

    await expectFailure(listLegalProcesses(organizationId, 1, 20, vi.fn()), 'unexpected')
  })
})

describe('listLegalProcesses query', () => {
  it.each([
    ['sem filtros', undefined],
    ['filtros vazios', {}],
    [
      'valores vazios ou padrão',
      { search: '   ', responsible: 'any', sort: 'title' as const },
    ],
  ])('KeepsDefaultUrlIdentical_%s', async (_, filters) => {
    const fetchMock = vi.fn().mockResolvedValue(response(200, listBody([])))
    vi.stubGlobal('fetch', fetchMock)

    await listLegalProcesses(organizationId, 1, 20, vi.fn(), undefined, filters)

    expect(requestedUrl(fetchMock)).toBe(`${processesPath}?pageNumber=1&pageSize=20`)
  })

  it.each([
    [{ search: '  0001234  ' }, 'search=0001234'],
    [{ status: 'closed' as const }, 'status=closed'],
    [{ responsible: 'self' }, 'responsible=self'],
    [{ responsible: 'unassigned' }, 'responsible=unassigned'],
    [{ responsible: membershipId }, `responsible=${membershipId}`],
    [{ sort: 'newest' as const }, 'sort=newest'],
  ])('AppendsOptionalParameterAfterPagination_%o', async (filters, expected) => {
    const fetchMock = vi.fn().mockResolvedValue(response(200, listBody([])))
    vi.stubGlobal('fetch', fetchMock)

    await listLegalProcesses(organizationId, 1, 20, vi.fn(), undefined, filters)

    expect(requestedUrl(fetchMock)).toBe(
      `${processesPath}?pageNumber=1&pageSize=20&${expected}`,
    )
  })

  it('AppendsAllOptionalParametersInStableOrder', async () => {
    const fetchMock = vi.fn().mockResolvedValue(response(200, listBody([])))
    vi.stubGlobal('fetch', fetchMock)

    await listLegalProcesses(organizationId, 1, 20, vi.fn(), undefined, {
      sort: 'newest',
      responsible: 'self',
      status: 'suspended',
      search: 'cobrança',
    })

    expect(requestedUrl(fetchMock)).toBe(
      `${processesPath}?pageNumber=1&pageSize=20&search=cobran%C3%A7a&status=suspended&responsible=self&sort=newest`,
    )
  })

  it('RejectsUnknownStatusFilterWithoutRequest', async () => {
    const fetchMock = vi.fn()
    vi.stubGlobal('fetch', fetchMock)

    await expectFailure(
      listLegalProcesses(organizationId, 1, 20, vi.fn(), undefined, {
        status: 'archived' as never,
      }),
      'bad-request',
    )
    expect(fetchMock).not.toHaveBeenCalled()
  })
})

describe('createLegalProcess', () => {
  it('WithoutOperationalFields_SendsIdenticalBody', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(csrfResponse())
      .mockResolvedValueOnce(response(201, { id: processId }))
    vi.stubGlobal('fetch', fetchMock)

    await expect(
      createLegalProcess(organizationId, clientId, 'Ação de cobrança', vi.fn()),
    ).resolves.toEqual({ id: processId })

    const [url, init] = fetchMock.mock.calls[1] as [string, RequestInit]
    expect(url).toBe(processesPath)
    expect(init.method).toBe('POST')
    expect(init.headers).toEqual({
      'Content-Type': 'application/json',
      'X-CSRF-TOKEN': 'csrf-token',
    })
    expect(init.body).toBe(JSON.stringify({ clientId, title: 'Ação de cobrança' }))
  })

  it('WithOperationalFields_SendsOnlyInformedFields', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(csrfResponse())
      .mockResolvedValueOnce(response(201, { id: processId }))
      .mockResolvedValueOnce(response(201, { id: processId }))
    vi.stubGlobal('fetch', fetchMock)

    await createLegalProcess(organizationId, clientId, 'Ação', vi.fn(), undefined, {
      processNumber: '0001234-56.2026.8.26.0100',
      status: 'suspended',
      courtOrAuthority: '1ª Vara Cível',
      responsibleMembershipId: membershipId,
    })
    await createLegalProcess(organizationId, clientId, 'Ação', vi.fn(), undefined, {
      status: 'closed',
    })

    const fullBody = JSON.parse(
      (fetchMock.mock.calls[1] as [string, RequestInit])[1].body as string,
    ) as Record<string, unknown>
    expect(fullBody).toEqual({
      clientId,
      title: 'Ação',
      processNumber: '0001234-56.2026.8.26.0100',
      status: 'suspended',
      courtOrAuthority: '1ª Vara Cível',
      responsibleMembershipId: membershipId,
    })
    const partialBody = JSON.parse(
      (fetchMock.mock.calls[2] as [string, RequestInit])[1].body as string,
    ) as Record<string, unknown>
    expect(Object.keys(partialBody)).toEqual(['clientId', 'title', 'status'])
    expect(partialBody.status).toBe('closed')
  })

  it.each([
    [
      409,
      { title: 'Resource conflict', detail: 'duplicate' },
      'conflict' as const,
    ],
    [
      400,
      { title: 'Related responsible member unavailable' },
      'related-responsible-unavailable' as const,
    ],
    [400, { title: 'One or more validation errors occurred.' }, 'bad-request' as const],
    [400, undefined, 'bad-request' as const],
  ])('Status%s_MapsToFailure', async (status, body, failure) => {
    vi.stubGlobal(
      'fetch',
      vi
        .fn()
        .mockResolvedValueOnce(csrfResponse())
        .mockResolvedValueOnce(response(status, body)),
    )

    await expectFailure(
      createLegalProcess(organizationId, clientId, 'Ação', vi.fn()),
      failure,
    )
  })
})

const operationalChanges = [
  {
    name: 'changeLegalProcessDetails',
    path: 'details',
    body: { processNumber: '0001234-56.2026.8.26.0100', courtOrAuthority: null },
    send: (signal?: AbortSignal) =>
      changeLegalProcessDetails(
        organizationId,
        processId,
        { processNumber: '0001234-56.2026.8.26.0100', courtOrAuthority: null },
        vi.fn(),
        signal,
      ),
  },
  {
    name: 'changeLegalProcessStatus',
    path: 'status',
    body: { status: 'closed' },
    send: (signal?: AbortSignal) =>
      changeLegalProcessStatus(organizationId, processId, 'closed', vi.fn(), signal),
  },
  {
    name: 'changeLegalProcessResponsible',
    path: 'responsible',
    body: { responsibleMembershipId: null },
    send: (signal?: AbortSignal) =>
      changeLegalProcessResponsible(organizationId, processId, null, vi.fn(), signal),
  },
] as const

describe.each(operationalChanges)('$name', ({ path, body, send }) => {
  it('Status204_SendsPutWithCsrfAndExactBody', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(csrfResponse())
      .mockResolvedValueOnce(response(204))
    vi.stubGlobal('fetch', fetchMock)
    const controller = new AbortController()

    await expect(send(controller.signal)).resolves.toBeUndefined()

    expect(fetchMock).toHaveBeenNthCalledWith(1, '/api/auth/csrf', {
      method: 'GET',
      credentials: 'same-origin',
      cache: 'no-store',
    })
    expect(fetchMock).toHaveBeenNthCalledWith(
      2,
      `${processesPath}/${processId}/${path}`,
      {
        method: 'PUT',
        headers: {
          'Content-Type': 'application/json',
          'X-CSRF-TOKEN': 'csrf-token',
        },
        body: JSON.stringify(body),
        cache: 'no-store',
        signal: controller.signal,
        credentials: 'same-origin',
      },
    )
  })

  it.each([
    [400, { title: 'One or more validation errors occurred.' }, 'bad-request' as const],
    [403, undefined, 'forbidden' as const],
    [404, undefined, 'not-found' as const],
    [409, { title: 'Resource conflict' }, 'conflict' as const],
    [500, { detail: 'internal' }, 'unexpected' as const],
  ])('Status%s_MapsToFailure', async (status, problem, failure) => {
    vi.stubGlobal(
      'fetch',
      vi
        .fn()
        .mockResolvedValueOnce(csrfResponse())
        .mockResolvedValueOnce(response(status, problem)),
    )

    await expectFailure(send(), failure)
  })

  it('Status400_WithRelatedResponsibleTitle_MapsToSpecificFailure', async () => {
    vi.stubGlobal(
      'fetch',
      vi
        .fn()
        .mockResolvedValueOnce(csrfResponse())
        .mockResolvedValueOnce(
          response(400, { title: 'Related responsible member unavailable' }),
        ),
    )

    await expectFailure(send(), 'related-responsible-unavailable')
  })

  it('Status400_ClearsCsrfTokenBeforeNextRequest', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(csrfResponse('first-token'))
      .mockResolvedValueOnce(response(400))
      .mockResolvedValueOnce(csrfResponse('second-token'))
      .mockResolvedValueOnce(response(204))
    vi.stubGlobal('fetch', fetchMock)

    await expectFailure(send(), 'bad-request')
    await send()

    expect(requestedUrl(fetchMock, 2)).toBe('/api/auth/csrf')
    expect((fetchMock.mock.calls[3] as [string, RequestInit])[1].headers).toMatchObject({
      'X-CSRF-TOKEN': 'second-token',
    })
  })

  it('Status409_KeepsCsrfToken', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(csrfResponse())
      .mockResolvedValueOnce(response(409, { title: 'Resource conflict' }))
      .mockResolvedValueOnce(response(204))
    vi.stubGlobal('fetch', fetchMock)

    await expectFailure(send(), 'conflict')
    await send()

    expect(fetchMock).toHaveBeenCalledTimes(3)
    expect(requestedUrl(fetchMock, 2)).toBe(`${processesPath}/${processId}/${path}`)
  })
})

describe('legal process status formatting', () => {
  it.each([
    ['inProgress' as const, 'Em andamento', 'is-active'],
    ['suspended' as const, 'Suspenso', 'is-pending'],
    ['closed' as const, 'Encerrado', 'is-inactive'],
  ])('%s_UsesApprovedLabelAndTone', (status, label, tone) => {
    expect(getLegalProcessStatusLabel(status)).toBe(label)
    expect(getLegalProcessStatusTone(status)).toBe(tone)
  })
})
