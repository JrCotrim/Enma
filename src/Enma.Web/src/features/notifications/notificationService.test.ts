import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { clearCsrfToken } from '../authentication/csrfClient'
import {
  getNotifications,
  markAllNotificationsAsRead,
  markNotificationAsRead,
} from './notificationService'

const organizationId = '11111111-1111-4111-8111-111111111111'
const notificationId = '22222222-2222-4222-8222-222222222222'
const sourceId = '33333333-3333-4333-8333-333333333333'

function response(status: number, body?: unknown): Response {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
  })
}

function notification(overrides: Record<string, unknown> = {}) {
  return {
    id: notificationId,
    kind: 'legalDeadlineDueSoon',
    sourceType: 'legalDeadline',
    sourceId,
    sourceTitle: 'Apresentar contestação',
    occurrenceDate: '2026-09-03',
    occurrenceAt: null,
    generatedAt: '2026-09-01T12:00:00Z',
    readAt: null,
    ...overrides,
  }
}

beforeEach(clearCsrfToken)

afterEach(() => {
  clearCsrfToken()
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

describe('Notifications API client', () => {
  it('GetNotifications_ParsesStableKindsAndPreservesDateOnlyString', async () => {
    const items = [
      notification(),
      notification({
        id: '44444444-4444-4444-8444-444444444444',
        kind: 'legalTaskDueSoon',
        sourceType: 'legalTask',
        sourceId: '55555555-5555-4555-8555-555555555555',
        sourceTitle: 'Revisar documentos',
      }),
      notification({
        id: '66666666-6666-4666-8666-666666666666',
        kind: 'calendarEventStartingSoon',
        sourceType: 'calendarEvent',
        sourceId: '77777777-7777-4777-8777-777777777777',
        sourceTitle: 'Audiência',
        occurrenceDate: null,
        occurrenceAt: '2026-09-03T15:30:00Z',
      }),
    ]
    const fetchMock = vi.fn().mockResolvedValue(response(200, { items, unreadCount: 27 }))
    vi.stubGlobal('fetch', fetchMock)

    const result = await getNotifications(organizationId, vi.fn())

    expect(result.items.map((item) => item.kind)).toEqual([
      'legalDeadlineDueSoon',
      'legalTaskDueSoon',
      'calendarEventStartingSoon',
    ])
    expect(result.items[0]?.occurrenceDate).toBe('2026-09-03')
    expect(result.unreadCount).toBe(27)
    expect(fetchMock).toHaveBeenCalledWith(
      `/api/organizations/${organizationId}/notifications`,
      {
        method: 'GET',
        cache: 'no-store',
        signal: undefined,
        credentials: 'same-origin',
      },
    )
  })

  it.each([
    [{ items: 'invalid', unreadCount: 0 }],
    [{ items: [notification({ kind: 'unknown' })], unreadCount: 1 }],
    [{ items: [notification({ occurrenceDate: '2026-02-30' })], unreadCount: 1 }],
    [
      {
        items: [
          notification({
            kind: 'calendarEventStartingSoon',
            sourceType: 'calendarEvent',
            occurrenceDate: '2026-09-03',
            occurrenceAt: null,
          }),
        ],
        unreadCount: 1,
      },
    ],
    [{ items: [notification()], unreadCount: -1 }],
    [{ items: Array.from({ length: 21 }, () => notification()), unreadCount: 21 }],
  ])('GetNotifications_RejectsMalformedPayload %#', async (body) => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(response(200, body)))

    await expect(
      getNotifications(organizationId, vi.fn()),
    ).rejects.toMatchObject({ failure: 'unexpected' })
  })

  it('ReadMutations_UseCsrfAndExactBodylessPutRoutes', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(response(200, { requestToken: 'csrf-token' }))
      .mockResolvedValueOnce(response(204))
      .mockResolvedValueOnce(response(204))
    vi.stubGlobal('fetch', fetchMock)

    await markNotificationAsRead(
      organizationId,
      notificationId,
      vi.fn(),
    )
    await markAllNotificationsAsRead(organizationId, vi.fn())

    expect(fetchMock).toHaveBeenNthCalledWith(1, '/api/auth/csrf', {
      method: 'GET',
      credentials: 'same-origin',
      cache: 'no-store',
    })
    expect(fetchMock).toHaveBeenNthCalledWith(
      2,
      `/api/organizations/${organizationId}/notifications/${notificationId}/read`,
      {
        method: 'PUT',
        headers: { 'X-CSRF-TOKEN': 'csrf-token' },
        cache: 'no-store',
        signal: undefined,
        credentials: 'same-origin',
      },
    )
    expect(fetchMock).toHaveBeenNthCalledWith(
      3,
      `/api/organizations/${organizationId}/notifications/read-all`,
      {
        method: 'PUT',
        headers: { 'X-CSRF-TOKEN': 'csrf-token' },
        cache: 'no-store',
        signal: undefined,
        credentials: 'same-origin',
      },
    )
  })

  it('ReadMutation_CsrfAcquisitionFailure_DoesNotSendMutation', async () => {
    const fetchMock = vi.fn().mockResolvedValue(response(500))
    vi.stubGlobal('fetch', fetchMock)

    await expect(
      markNotificationAsRead(organizationId, notificationId, vi.fn()),
    ).rejects.toThrow('CSRF')
    expect(fetchMock).toHaveBeenCalledTimes(1)
  })
})
