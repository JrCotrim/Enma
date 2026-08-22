import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { clearCsrfToken } from '../authentication/csrfClient'
import {
  changeCalendarEventAssignee,
  createCalendarEvent,
  deleteCalendarEvent,
  getAgenda,
  updateCalendarEvent,
} from './agendaService'
import type { CreateCalendarEventRequest } from './agendaTypes'

const organizationId = '11111111-1111-4111-8111-111111111111'
const eventId = '22222222-2222-4222-8222-222222222222'
const request: CreateCalendarEventRequest = {
  title: 'Audiência',
  description: null,
  startsAt: '2026-09-01T09:00:00-03:00',
  endsAt: '2026-09-01T10:00:00-03:00',
  location: null,
  clientId: null,
  processId: null,
  assigneeMembershipId: null,
}

function response(status: number, body?: unknown): Response {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
  })
}

beforeEach(clearCsrfToken)
afterEach(() => {
  clearCsrfToken()
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

describe('Agenda API client', () => {
  it('GetAgenda_PreservesOffsetBoundariesAndSessionFetchConventions', async () => {
    const fetchMock = vi.fn().mockResolvedValue(response(200, { items: [] }))
    vi.stubGlobal('fetch', fetchMock)
    await getAgenda(
      organizationId,
      '2026-09-01T00:00:00-03:00',
      '2026-10-01T00:00:00-03:00',
      vi.fn(),
    )
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    const parsed = new URL(url, 'https://enma.test')
    expect(parsed.pathname).toBe(`/api/organizations/${organizationId}/agenda`)
    expect(parsed.searchParams.get('from')).toBe('2026-09-01T00:00:00-03:00')
    expect(parsed.searchParams.get('to')).toBe('2026-10-01T00:00:00-03:00')
    expect(init).toMatchObject({
      method: 'GET',
      cache: 'no-store',
      credentials: 'same-origin',
    })
  })

  it('GetAgenda_RejectsCompletedStateOnCalendarEventAsMalformed', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        response(200, {
          items: [
            {
              kind: 'calendarEvent',
              id: eventId,
              title: 'Evento inválido',
              isAllDay: false,
              date: null,
              startsAt: request.startsAt,
              endsAt: request.endsAt,
              completedAt: '2026-09-01T13:00:00Z',
              clientId: null,
              clientName: null,
              processId: null,
              processTitle: null,
              assigneeMembershipId: null,
              assigneeDisplayName: null,
            },
          ],
        }),
      ),
    )
    await expect(
      getAgenda(
        organizationId,
        '2026-09-01T00:00:00-03:00',
        '2026-10-01T00:00:00-03:00',
        vi.fn(),
      ),
    ).rejects.toMatchObject({ failure: 'unexpected' })
  })

  it('CalendarEventMutations_UseExistingAntiforgeryAndExactRoutes', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(response(200, { requestToken: 'csrf-token' }))
      .mockResolvedValueOnce(response(201, { id: eventId }))
      .mockResolvedValueOnce(response(204))
      .mockResolvedValueOnce(response(204))
      .mockResolvedValueOnce(response(204))
    vi.stubGlobal('fetch', fetchMock)
    await createCalendarEvent(organizationId, request, vi.fn())
    const update = {
      title: request.title,
      description: request.description,
      startsAt: request.startsAt,
      endsAt: request.endsAt,
      location: request.location,
      clientId: request.clientId,
      processId: request.processId,
    }
    await updateCalendarEvent(organizationId, eventId, update, vi.fn())
    await changeCalendarEventAssignee(
      organizationId,
      eventId,
      'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
      vi.fn(),
    )
    await deleteCalendarEvent(organizationId, eventId, vi.fn())

    expect(fetchMock).toHaveBeenNthCalledWith(1, '/api/auth/csrf', {
      method: 'GET',
      credentials: 'same-origin',
      cache: 'no-store',
    })
    expect(fetchMock.mock.calls[1]?.[0]).toBe(
      `/api/organizations/${organizationId}/calendar-events`,
    )
    expect(fetchMock.mock.calls[2]?.[0]).toBe(
      `/api/organizations/${organizationId}/calendar-events/${eventId}`,
    )
    expect(fetchMock.mock.calls[3]?.[0]).toBe(
      `/api/organizations/${organizationId}/calendar-events/${eventId}/assignee`,
    )
    expect(fetchMock.mock.calls[4]?.[0]).toBe(
      `/api/organizations/${organizationId}/calendar-events/${eventId}`,
    )
    for (const call of fetchMock.mock.calls.slice(1)) {
      const init = call[1] as RequestInit
      expect(init.credentials).toBe('same-origin')
      expect(init.headers).toMatchObject({ 'X-CSRF-TOKEN': 'csrf-token' })
    }
    expect(JSON.parse(fetchMock.mock.calls[2]?.[1]?.body as string)).toEqual(update)
    expect(fetchMock.mock.calls[3]?.[1]?.method).toBe('PUT')
    expect(fetchMock.mock.calls[4]?.[1]?.method).toBe('DELETE')
  })
})
