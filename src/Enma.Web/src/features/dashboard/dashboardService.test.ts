import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  DashboardRequestError,
  getDashboard,
  parseDashboardResponse,
} from './dashboardService'

const deadlineId = '11111111-1111-4111-8111-111111111111'
const taskId = '22222222-2222-4222-8222-222222222222'
const eventId = '33333333-3333-4333-8333-333333333333'

function completePayload() {
  return {
    referenceDate: '2026-08-24',
    summary: {
      activeClients: 12,
      totalLegalProcesses: 18,
      pendingDeadlines: 5,
      pendingTasks: 9,
    },
    attention: {
      deadlines: { overdue: 1, dueToday: 2, dueInNextSevenDays: 2 },
      tasks: { overdue: 2, dueToday: 1, dueInNextSevenDays: 3 },
    },
    upcoming: {
      throughDate: '2026-08-31',
      deadlines: [
        {
          id: deadlineId,
          title: 'Apresentar contestação',
          dueDate: '2026-08-25',
          clientName: 'Cliente Alfa',
          processTitle: 'Ação contratual',
        },
      ],
      tasks: [
        {
          id: taskId,
          title: 'Revisar documentos',
          dueDate: '2026-08-26',
          clientName: 'Cliente Beta',
          processTitle: 'Inventário',
          assigneeDisplayName: 'Ana Lima',
        },
      ],
      calendarEvents: [
        {
          id: eventId,
          title: 'Reunião de alinhamento',
          startsAt: '2026-08-27T13:00:00Z',
          endsAt: '2026-08-27T14:00:00Z',
          clientName: 'Cliente Gama',
          processTitle: 'Consultoria',
          assigneeDisplayName: 'Bruno Souza',
        },
      ],
    },
  }
}

function response(status: number, body?: unknown): Response {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
  })
}

afterEach(() => {
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

describe('dashboardService', () => {
  it('GetDashboard_ValidCompletePayload_UsesScopedSessionGetAndParses', async () => {
    const fetchMock = vi.fn(() => Promise.resolve(response(200, completePayload())))
    vi.stubGlobal('fetch', fetchMock)
    const onUnauthorized = vi.fn()
    const controller = new AbortController()

    const result = await getDashboard(
      'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
      onUnauthorized,
      controller.signal,
    )

    expect(result.summary.totalLegalProcesses).toBe(18)
    expect(result.upcoming.calendarEvents[0]?.id).toBe(eventId)
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/organizations/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa/dashboard',
      {
        method: 'GET',
        cache: 'no-store',
        signal: controller.signal,
        credentials: 'same-origin',
      },
    )
  })

  it('Parser_ZeroCountsAndEmptyUpcomingArrays_AcceptsValidEmptyOrganization', () => {
    const payload = completePayload()
    payload.summary = {
      activeClients: 0,
      totalLegalProcesses: 0,
      pendingDeadlines: 0,
      pendingTasks: 0,
    }
    payload.attention = {
      deadlines: { overdue: 0, dueToday: 0, dueInNextSevenDays: 0 },
      tasks: { overdue: 0, dueToday: 0, dueInNextSevenDays: 0 },
    }
    payload.upcoming.deadlines = []
    payload.upcoming.tasks = []
    payload.upcoming.calendarEvents = []

    expect(parseDashboardResponse(payload)).toEqual(payload)
  })

  it('Parser_NullableTaskAndEventMetadata_AcceptsNullValues', () => {
    const payload = completePayload()
    Object.assign(payload.upcoming.tasks[0] as object, {
      clientName: null,
      processTitle: null,
      assigneeDisplayName: null,
    })
    Object.assign(payload.upcoming.calendarEvents[0] as object, {
      clientName: null,
      processTitle: null,
      assigneeDisplayName: null,
    })

    expect(parseDashboardResponse(payload).upcoming.tasks[0]?.clientName).toBeNull()
    expect(
      parseDashboardResponse(payload).upcoming.calendarEvents[0]?.processTitle,
    ).toBeNull()
  })

  it('Parser_OffsetTimestampWithFraction_AcceptsDateTimeOffsetContract', () => {
    const payload = completePayload()
    payload.upcoming.calendarEvents[0]!.startsAt =
      '2026-08-27T13:00:00.1234567-03:00'
    payload.upcoming.calendarEvents[0]!.endsAt =
      '2026-08-27T14:00:00.1234567-03:00'

    expect(parseDashboardResponse(payload)).toEqual(payload)
  })

  it.each([null, [], 'dashboard'])('Parser_MalformedRoot_Rejects(%s)', (value) => {
    expect(() => parseDashboardResponse(value)).toThrow(DashboardRequestError)
  })

  it('Parser_MissingRequiredField_RejectsEntirePayload', () => {
    const payload = completePayload() as Record<string, unknown>
    delete payload.attention

    expect(() => parseDashboardResponse(payload)).toThrow(DashboardRequestError)
  })

  it('Parser_NegativeCount_RejectsEntirePayload', () => {
    const payload = completePayload()
    payload.attention.deadlines.overdue = -1

    expect(() => parseDashboardResponse(payload)).toThrow(DashboardRequestError)
  })

  it('Parser_FractionalCount_RejectsEntirePayload', () => {
    const payload = completePayload()
    payload.summary.pendingTasks = 1.5

    expect(() => parseDashboardResponse(payload)).toThrow(DashboardRequestError)
  })

  it.each([
    ['referenceDate', '2026-02-30'],
    ['throughDate', '2026-13-01'],
    ['dueDate', '25/08/2026'],
  ])('Parser_MalformedDateOnly_Rejects%s', (field, value) => {
    const payload = completePayload()
    if (field === 'referenceDate') payload.referenceDate = value
    if (field === 'throughDate') payload.upcoming.throughDate = value
    if (field === 'dueDate') payload.upcoming.deadlines[0]!.dueDate = value

    expect(() => parseDashboardResponse(payload)).toThrow(DashboardRequestError)
  })

  it.each([
    'not-an-instant',
    '2026-08-27',
    '2026-08-27T13:00:00',
    '2026-02-30T13:00:00Z',
    '2026-08-27T13:00:00+14:30',
  ])('Parser_MalformedTimestamp_RejectsEntirePayload(%s)', (startsAt) => {
    const payload = completePayload()
    payload.upcoming.calendarEvents[0]!.startsAt = startsAt

    expect(() => parseDashboardResponse(payload)).toThrow(DashboardRequestError)
  })

  it('Parser_EndBeforeStart_RejectsUsingAgendaTimestampConvention', () => {
    const payload = completePayload()
    payload.upcoming.calendarEvents[0]!.endsAt = '2026-08-27T12:00:00Z'

    expect(() => parseDashboardResponse(payload)).toThrow(DashboardRequestError)
  })

  it('Parser_MalformedGuid_RejectsEntirePayload', () => {
    const payload = completePayload()
    payload.upcoming.tasks[0]!.id = 'not-a-guid'

    expect(() => parseDashboardResponse(payload)).toThrow(DashboardRequestError)
  })

  it.each(['deadlines', 'tasks', 'calendarEvents'] as const)(
    'Parser_Malformed%sArray_RejectsEntirePayload',
    (group) => {
      const payload = completePayload()
      ;(payload.upcoming as unknown as Record<string, unknown>)[group] = {}

      expect(() => parseDashboardResponse(payload)).toThrow(DashboardRequestError)
    },
  )

  it.each(['', '   '])('Parser_EmptyTitle_RejectsEntirePayload(%j)', (title) => {
    const payload = completePayload()
    payload.upcoming.deadlines[0]!.title = title

    expect(() => parseDashboardResponse(payload)).toThrow(DashboardRequestError)
  })

  it.each([
    [403, 'forbidden'],
    [500, 'unexpected'],
  ] as const)('GetDashboard_Http%s_ThrowsTypedFailure', async (status, failure) => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(response(status))))

    await expect(getDashboard('organization-id', vi.fn())).rejects.toMatchObject({
      failure,
    })
  })

  it('GetDashboard_Unauthorized_InvokesSessionHandlerAndThrowsTypedFailure', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(response(401))))
    const onUnauthorized = vi.fn()

    await expect(getDashboard('organization-id', onUnauthorized)).rejects.toMatchObject({
      failure: 'unauthorized',
    })
    expect(onUnauthorized).toHaveBeenCalledOnce()
  })
})
