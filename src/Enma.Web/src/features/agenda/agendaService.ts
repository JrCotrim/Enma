import { clearCsrfToken, getCsrfToken } from '../authentication/csrfClient'
import {
  fetchWithSession,
  type UnauthorizedHandler,
} from '../authentication/sessionClient'
import { isValidDateOnly, isValidGuid } from '../deadlines/legalDeadlineFormatting'
import type {
  AgendaItem,
  AgendaResponse,
  CalendarEventDetail,
  CreateCalendarEventRequest,
  UpdateCalendarEventRequest,
} from './agendaTypes'

export type AgendaRequestFailure =
  | 'unauthorized'
  | 'forbidden'
  | 'not-found'
  | 'bad-request'
  | 'related-assignee-unavailable'
  | 'unexpected'

export class AgendaRequestError extends Error {
  constructor(readonly failure: AgendaRequestFailure) {
    super('The agenda request failed.')
  }
}

function isNullableString(value: unknown): value is string | null {
  return value === null || typeof value === 'string'
}

function isNullableGuid(value: unknown): value is string | null {
  return value === null || (typeof value === 'string' && isValidGuid(value))
}

function isTimestamp(value: unknown): value is string {
  return (
    typeof value === 'string' &&
    value.length > 0 &&
    !Number.isNaN(new Date(value).getTime())
  )
}

function isNullableTimestamp(value: unknown): value is string | null {
  return value === null || isTimestamp(value)
}

function parseAgendaItem(value: unknown): AgendaItem | undefined {
  if (typeof value !== 'object' || value === null) return undefined
  const item = value as Record<string, unknown>
  if (
    (item.kind !== 'deadline' &&
      item.kind !== 'task' &&
      item.kind !== 'calendarEvent') ||
    typeof item.id !== 'string' ||
    !isValidGuid(item.id) ||
    typeof item.title !== 'string' ||
    item.title.length === 0 ||
    typeof item.isAllDay !== 'boolean' ||
    !isNullableString(item.date) ||
    (item.date !== null && !isValidDateOnly(item.date)) ||
    !isNullableTimestamp(item.startsAt) ||
    !isNullableTimestamp(item.endsAt) ||
    !isNullableTimestamp(item.completedAt) ||
    !isNullableGuid(item.clientId) ||
    !isNullableString(item.clientName) ||
    !isNullableGuid(item.processId) ||
    !isNullableString(item.processTitle) ||
    !isNullableGuid(item.assigneeMembershipId) ||
    !isNullableString(item.assigneeDisplayName)
  ) {
    return undefined
  }

  const isDateOnlyItem = item.kind === 'deadline' || item.kind === 'task'
  if (
    (isDateOnlyItem &&
      (!item.isAllDay ||
        item.date === null ||
        item.startsAt !== null ||
        item.endsAt !== null)) ||
    (!isDateOnlyItem &&
      (item.isAllDay ||
        item.date !== null ||
        item.startsAt === null ||
        item.endsAt === null ||
        item.completedAt !== null ||
        new Date(item.endsAt).getTime() <= new Date(item.startsAt).getTime()))
  ) {
    return undefined
  }

  return item as unknown as AgendaItem
}

function parseAgendaResponse(value: unknown): AgendaResponse {
  if (typeof value !== 'object' || value === null) {
    throw new AgendaRequestError('unexpected')
  }
  const itemsValue = (value as Record<string, unknown>).items
  const items = Array.isArray(itemsValue)
    ? itemsValue.map(parseAgendaItem)
    : undefined
  if (!items || items.some((item) => item === undefined)) {
    throw new AgendaRequestError('unexpected')
  }
  return { items: items as AgendaItem[] }
}

function parseCalendarEventDetail(value: unknown): CalendarEventDetail {
  if (typeof value !== 'object' || value === null) {
    throw new AgendaRequestError('unexpected')
  }
  const event = value as Record<string, unknown>
  if (
    typeof event.id !== 'string' ||
    !isValidGuid(event.id) ||
    typeof event.title !== 'string' ||
    event.title.length === 0 ||
    !isNullableString(event.description) ||
    !isTimestamp(event.startsAt) ||
    !isTimestamp(event.endsAt) ||
    new Date(event.endsAt).getTime() <= new Date(event.startsAt).getTime() ||
    !isNullableString(event.location) ||
    !isNullableGuid(event.clientId) ||
    !isNullableString(event.clientName) ||
    !isNullableGuid(event.processId) ||
    !isNullableString(event.processTitle) ||
    !isNullableGuid(event.assigneeMembershipId) ||
    !isNullableString(event.assigneeDisplayName) ||
    typeof event.createdByMembershipId !== 'string' ||
    !isValidGuid(event.createdByMembershipId) ||
    typeof event.createdByDisplayName !== 'string' ||
    event.createdByDisplayName.length === 0 ||
    !isTimestamp(event.createdAt)
  ) {
    throw new AgendaRequestError('unexpected')
  }
  return event as unknown as CalendarEventDetail
}

function throwForStatus(status: number): never {
  if (status === 401) throw new AgendaRequestError('unauthorized')
  if (status === 403) throw new AgendaRequestError('forbidden')
  if (status === 404) throw new AgendaRequestError('not-found')
  if (status === 400) throw new AgendaRequestError('bad-request')
  throw new AgendaRequestError('unexpected')
}

function calendarEventsEndpoint(organizationId: string): string {
  return `/api/organizations/${encodeURIComponent(organizationId)}/calendar-events`
}

function calendarEventEndpoint(
  organizationId: string,
  calendarEventId: string,
): string {
  return `${calendarEventsEndpoint(organizationId)}/${encodeURIComponent(calendarEventId)}`
}

async function sendMutation(
  endpoint: string,
  method: 'PUT' | 'DELETE',
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
  body?: UpdateCalendarEventRequest | { readonly assigneeMembershipId: string | null },
): Promise<void> {
  const requestToken = await getCsrfToken()
  const response = await fetchWithSession(
    endpoint,
    {
      method,
      headers: {
        ...(body ? { 'Content-Type': 'application/json' } : {}),
        'X-CSRF-TOKEN': requestToken,
      },
      ...(body ? { body: JSON.stringify(body) } : {}),
      cache: 'no-store',
      signal,
    },
    onUnauthorized,
  )
  if (response.status === 204) return
  if (response.status === 400) clearCsrfToken()
  throwForStatus(response.status)
}

export async function getAgenda(
  organizationId: string,
  from: string,
  to: string,
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<AgendaResponse> {
  const query = new URLSearchParams({ from, to })
  const response = await fetchWithSession(
    `/api/organizations/${encodeURIComponent(organizationId)}/agenda?${query.toString()}`,
    { method: 'GET', cache: 'no-store', signal },
    onUnauthorized,
  )
  if (response.status !== 200) throwForStatus(response.status)
  return parseAgendaResponse(await response.json())
}

export async function createCalendarEvent(
  organizationId: string,
  body: CreateCalendarEventRequest,
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<{ readonly id: string }> {
  const requestToken = await getCsrfToken()
  const response = await fetchWithSession(
    calendarEventsEndpoint(organizationId),
    {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'X-CSRF-TOKEN': requestToken,
      },
      body: JSON.stringify(body),
      cache: 'no-store',
      signal,
    },
    onUnauthorized,
  )
  if (response.status !== 201) {
    if (response.status === 400) {
      clearCsrfToken()
      try {
        const problem = (await response.json()) as Record<string, unknown>
        if (problem.title === 'Related assignee unavailable') {
          throw new AgendaRequestError('related-assignee-unavailable')
        }
      } catch (error) {
        if (error instanceof AgendaRequestError) throw error
      }
    }
    throwForStatus(response.status)
  }
  const value = (await response.json()) as Record<string, unknown>
  if (typeof value.id !== 'string' || !isValidGuid(value.id)) {
    throw new AgendaRequestError('unexpected')
  }
  return { id: value.id }
}

export async function getCalendarEvent(
  organizationId: string,
  calendarEventId: string,
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<CalendarEventDetail> {
  const response = await fetchWithSession(
    calendarEventEndpoint(organizationId, calendarEventId),
    { method: 'GET', cache: 'no-store', signal },
    onUnauthorized,
  )
  if (response.status !== 200) throwForStatus(response.status)
  return parseCalendarEventDetail(await response.json())
}

export function updateCalendarEvent(
  organizationId: string,
  calendarEventId: string,
  body: UpdateCalendarEventRequest,
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<void> {
  return sendMutation(
    calendarEventEndpoint(organizationId, calendarEventId),
    'PUT',
    onUnauthorized,
    signal,
    body,
  )
}

export function changeCalendarEventAssignee(
  organizationId: string,
  calendarEventId: string,
  assigneeMembershipId: string | null,
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<void> {
  return sendMutation(
    `${calendarEventEndpoint(organizationId, calendarEventId)}/assignee`,
    'PUT',
    onUnauthorized,
    signal,
    { assigneeMembershipId },
  )
}

export function deleteCalendarEvent(
  organizationId: string,
  calendarEventId: string,
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<void> {
  return sendMutation(
    calendarEventEndpoint(organizationId, calendarEventId),
    'DELETE',
    onUnauthorized,
    signal,
  )
}
