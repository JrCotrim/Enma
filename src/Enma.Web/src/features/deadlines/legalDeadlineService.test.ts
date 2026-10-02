import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { clearCsrfToken } from '../authentication/csrfClient'
import {
  changeLegalDeadlineResponsible,
  createLegalDeadline,
  getLegalDeadline,
  LegalDeadlineRequestError,
  listLegalDeadlines,
  reopenLegalDeadline,
  type LegalDeadlineRequestFailure,
} from './legalDeadlineService'

const organizationId = '11111111-1111-4111-8111-111111111111'
const deadlineId = '22222222-2222-4222-8222-222222222222'
const processId = '33333333-3333-4333-8333-333333333333'
const membershipId = '44444444-4444-4444-8444-444444444444'
const deadlinesPath = `/api/organizations/${organizationId}/deadlines`
const deadlinePath = `${deadlinesPath}/${deadlineId}`

const deadlineWithResponsible = {
  id: deadlineId,
  title: 'Apresentar contestação',
  dueDate: '2026-11-01',
  processId,
  processTitle: 'Ação de cobrança',
  clientName: 'Cliente Exemplo',
  state: 'Pending',
  createdAt: '2026-08-12T14:30:00Z',
  completedAt: null,
  responsibleMembershipId: membershipId,
  responsibleDisplayName: 'Maria Souza',
}

const deadlineWithoutResponsible = {
  ...deadlineWithResponsible,
  responsibleMembershipId: null,
  responsibleDisplayName: null,
}

function response(status: number, body?: unknown): Response {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
  })
}

function listBody(items: readonly unknown[]) {
  return { items, pageNumber: 1, pageSize: 20 }
}

function csrfResponse(token = 'csrf-token'): Response {
  return response(200, { requestToken: token })
}

async function expectFailure(
  promise: Promise<unknown>,
  failure: LegalDeadlineRequestFailure,
) {
  const error = await promise.catch((caught: unknown) => caught)
  expect(error).toBeInstanceOf(LegalDeadlineRequestError)
  expect((error as LegalDeadlineRequestError).failure).toBe(failure)
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

describe('legal deadline responsible parser', () => {
  it('GetLegalDeadline_ParsesResponsiblePair', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(response(200, deadlineWithResponsible)))

    await expect(
      getLegalDeadline(organizationId, deadlineId, vi.fn()),
    ).resolves.toMatchObject({
      responsibleMembershipId: membershipId,
      responsibleDisplayName: 'Maria Souza',
    })
  })

  it('ListLegalDeadlines_ParsesNullResponsiblePair', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(response(200, listBody([deadlineWithoutResponsible]))),
    )

    const result = await listLegalDeadlines(organizationId, 1, 20, vi.fn())

    expect(result.items[0]).toMatchObject({
      responsibleMembershipId: null,
      responsibleDisplayName: null,
    })
  })

  it.each([
    ['id sem nome', { responsibleMembershipId: membershipId, responsibleDisplayName: null }],
    ['nome sem id', { responsibleMembershipId: null, responsibleDisplayName: 'Maria Souza' }],
    ['nome vazio', { responsibleDisplayName: '' }],
    ['id inválido', { responsibleMembershipId: 'not-a-guid' }],
    ['id numérico', { responsibleMembershipId: 123 }],
    ['campos ausentes', { responsibleMembershipId: undefined, responsibleDisplayName: undefined }],
  ])('%s_FailsAsUnexpected', async (_, overrides) => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        response(200, listBody([{ ...deadlineWithResponsible, ...overrides }])),
      ),
    )

    await expectFailure(listLegalDeadlines(organizationId, 1, 20, vi.fn()), 'unexpected')
  })
})

describe('listLegalDeadlines responsible filter', () => {
  it.each([
    [undefined, 'pageNumber=1&pageSize=20'],
    ['any', 'pageNumber=1&pageSize=20'],
    ['self', 'pageNumber=1&pageSize=20&responsible=self'],
    ['unassigned', 'pageNumber=1&pageSize=20&responsible=unassigned'],
    [membershipId, `pageNumber=1&pageSize=20&responsible=${membershipId}`],
  ])('Responsible %s_BuildsExpectedQuery', async (responsible, query) => {
    const fetchMock = vi.fn().mockResolvedValue(response(200, listBody([])))
    vi.stubGlobal('fetch', fetchMock)

    await listLegalDeadlines(organizationId, 1, 20, vi.fn(), undefined, {
      responsible,
    })

    expect(requestedUrl(fetchMock)).toBe(`${deadlinesPath}?${query}`)
  })
})

describe('createLegalDeadline responsible', () => {
  it('WithoutResponsible_SendsIdenticalBody', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(csrfResponse())
      .mockResolvedValueOnce(response(201, { id: deadlineId }))
    vi.stubGlobal('fetch', fetchMock)

    await createLegalDeadline(organizationId, processId, 'Prazo', '2026-11-01', vi.fn())

    const init = fetchMock.mock.calls[1]?.[1] as RequestInit
    const body = JSON.parse(init.body as string) as Record<string, unknown>
    expect(Object.keys(body)).toEqual(['processId', 'title', 'dueDate'])
  })

  it('WithResponsible_SendsResponsibleMembershipId', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(csrfResponse())
      .mockResolvedValueOnce(response(201, { id: deadlineId }))
    vi.stubGlobal('fetch', fetchMock)

    await createLegalDeadline(
      organizationId,
      processId,
      'Prazo',
      '2026-11-01',
      vi.fn(),
      undefined,
      { responsibleMembershipId: membershipId },
    )

    const init = fetchMock.mock.calls[1]?.[1] as RequestInit
    expect(JSON.parse(init.body as string)).toEqual({
      processId,
      title: 'Prazo',
      dueDate: '2026-11-01',
      responsibleMembershipId: membershipId,
    })
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

    await expectFailure(
      createLegalDeadline(
        organizationId,
        processId,
        'Prazo',
        '2026-11-01',
        vi.fn(),
        undefined,
        { responsibleMembershipId: membershipId },
      ),
      'related-responsible-unavailable',
    )
  })
})

describe('changeLegalDeadlineResponsible', () => {
  it.each([[membershipId], [null]])(
    'Responsible %s_Status204_SendsPutWithCsrfAndExactBody',
    async (responsibleMembershipId) => {
      const fetchMock = vi
        .fn()
        .mockResolvedValueOnce(csrfResponse('responsible-token'))
        .mockResolvedValueOnce(response(204))
      vi.stubGlobal('fetch', fetchMock)

      await changeLegalDeadlineResponsible(
        organizationId,
        deadlineId,
        responsibleMembershipId,
        vi.fn(),
      )

      const [url, init] = fetchMock.mock.calls[1] as [string, RequestInit]
      expect(url).toBe(`${deadlinePath}/responsible`)
      expect(init.method).toBe('PUT')
      expect(init.headers).toEqual({
        'Content-Type': 'application/json',
        'X-CSRF-TOKEN': 'responsible-token',
      })
      expect(JSON.parse(init.body as string)).toEqual({ responsibleMembershipId })
    },
  )

  it.each([
    [400, 'bad-request'],
    [403, 'forbidden'],
    [404, 'not-found'],
    [409, 'conflict'],
    [500, 'unexpected'],
  ] as const)('Status%s_MapsTo%s', async (status, failure) => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValueOnce(csrfResponse()).mockResolvedValueOnce(response(status)),
    )

    await expectFailure(
      changeLegalDeadlineResponsible(organizationId, deadlineId, membershipId, vi.fn()),
      failure,
    )
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

    await expectFailure(
      changeLegalDeadlineResponsible(organizationId, deadlineId, membershipId, vi.fn()),
      'related-responsible-unavailable',
    )
  })
})

describe('reopenLegalDeadline', () => {
  it('Status409_MapsToConflict', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(csrfResponse())
      .mockResolvedValueOnce(
        response(409, {
          title: 'Resource conflict',
          detail: 'The current responsible member is unavailable.',
        }),
      )
    vi.stubGlobal('fetch', fetchMock)

    await expectFailure(
      reopenLegalDeadline(organizationId, deadlineId, vi.fn()),
      'conflict',
    )
    expect(requestedUrl(fetchMock, 1)).toBe(`${deadlinePath}/reopen`)
  })
})
