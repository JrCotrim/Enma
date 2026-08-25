import {
  fetchWithSession,
  type UnauthorizedHandler,
} from '../authentication/sessionClient'
import { isValidDateOnly, isValidGuid } from '../deadlines/legalDeadlineFormatting'
import type {
  DashboardAttentionBucket,
  DashboardResponse,
  DashboardUpcomingCalendarEvent,
  DashboardUpcomingDeadline,
  DashboardUpcomingTask,
} from './dashboardTypes'

export type DashboardRequestFailure =
  | 'unauthorized'
  | 'forbidden'
  | 'unexpected'

export class DashboardRequestError extends Error {
  constructor(readonly failure: DashboardRequestFailure) {
    super('The dashboard request failed.')
  }
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null
}

function isNonNegativeInteger(value: unknown): value is number {
  return typeof value === 'number' && Number.isInteger(value) && value >= 0
}

function isNonEmptyString(value: unknown): value is string {
  return typeof value === 'string' && value.trim().length > 0
}

function isNullableString(value: unknown): value is string | null {
  return value === null || typeof value === 'string'
}

function isTimestamp(value: unknown): value is string {
  if (typeof value !== 'string') return false

  const match = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.\d{1,7})?(Z|([+-])(\d{2}):(\d{2}))$/.exec(
    value,
  )
  if (!match) return false

  const [, year, month, day, hour, minute, second, , , offsetHour, offsetMinute] =
    match
  if (
    !isValidDateOnly(`${year}-${month}-${day}`) ||
    Number(hour) > 23 ||
    Number(minute) > 59 ||
    Number(second) > 59 ||
    (offsetHour !== undefined &&
      (Number(offsetHour) > 14 ||
        Number(offsetMinute) > 59 ||
        (Number(offsetHour) === 14 && Number(offsetMinute) !== 0)))
  ) {
    return false
  }

  return !Number.isNaN(new Date(value).getTime())
}

function parseAttentionBucket(value: unknown): DashboardAttentionBucket | undefined {
  if (!isObject(value)) return undefined
  if (
    !isNonNegativeInteger(value.overdue) ||
    !isNonNegativeInteger(value.dueToday) ||
    !isNonNegativeInteger(value.dueInNextSevenDays)
  ) {
    return undefined
  }
  return value as unknown as DashboardAttentionBucket
}

function parseDeadline(value: unknown): DashboardUpcomingDeadline | undefined {
  if (!isObject(value)) return undefined
  if (
    !isNonEmptyString(value.id) ||
    !isValidGuid(value.id) ||
    !isNonEmptyString(value.title) ||
    typeof value.dueDate !== 'string' ||
    !isValidDateOnly(value.dueDate) ||
    typeof value.clientName !== 'string' ||
    typeof value.processTitle !== 'string'
  ) {
    return undefined
  }
  return value as unknown as DashboardUpcomingDeadline
}

function parseTask(value: unknown): DashboardUpcomingTask | undefined {
  if (!isObject(value)) return undefined
  if (
    !isNonEmptyString(value.id) ||
    !isValidGuid(value.id) ||
    !isNonEmptyString(value.title) ||
    typeof value.dueDate !== 'string' ||
    !isValidDateOnly(value.dueDate) ||
    !isNullableString(value.clientName) ||
    !isNullableString(value.processTitle) ||
    !isNullableString(value.assigneeDisplayName)
  ) {
    return undefined
  }
  return value as unknown as DashboardUpcomingTask
}

function parseCalendarEvent(
  value: unknown,
): DashboardUpcomingCalendarEvent | undefined {
  if (!isObject(value)) return undefined
  if (
    !isNonEmptyString(value.id) ||
    !isValidGuid(value.id) ||
    !isNonEmptyString(value.title) ||
    !isTimestamp(value.startsAt) ||
    !isTimestamp(value.endsAt) ||
    new Date(value.endsAt).getTime() <= new Date(value.startsAt).getTime() ||
    !isNullableString(value.clientName) ||
    !isNullableString(value.processTitle) ||
    !isNullableString(value.assigneeDisplayName)
  ) {
    return undefined
  }
  return value as unknown as DashboardUpcomingCalendarEvent
}

function parseArray<T>(
  value: unknown,
  parseItem: (item: unknown) => T | undefined,
): readonly T[] | undefined {
  if (!Array.isArray(value)) return undefined
  const items = value.map(parseItem)
  return items.some((item) => item === undefined)
    ? undefined
    : (items as T[])
}

export function parseDashboardResponse(value: unknown): DashboardResponse {
  if (!isObject(value)) throw new DashboardRequestError('unexpected')
  const { referenceDate, summary, attention, upcoming } = value
  if (
    typeof referenceDate !== 'string' ||
    !isValidDateOnly(referenceDate) ||
    !isObject(summary) ||
    !isNonNegativeInteger(summary.activeClients) ||
    !isNonNegativeInteger(summary.totalLegalProcesses) ||
    !isNonNegativeInteger(summary.pendingDeadlines) ||
    !isNonNegativeInteger(summary.pendingTasks) ||
    !isObject(attention) ||
    !parseAttentionBucket(attention.deadlines) ||
    !parseAttentionBucket(attention.tasks) ||
    !isObject(upcoming) ||
    typeof upcoming.throughDate !== 'string' ||
    !isValidDateOnly(upcoming.throughDate)
  ) {
    throw new DashboardRequestError('unexpected')
  }

  const deadlines = parseArray(upcoming.deadlines, parseDeadline)
  const tasks = parseArray(upcoming.tasks, parseTask)
  const calendarEvents = parseArray(upcoming.calendarEvents, parseCalendarEvent)
  if (!deadlines || !tasks || !calendarEvents) {
    throw new DashboardRequestError('unexpected')
  }

  return {
    referenceDate,
    summary: summary as unknown as DashboardResponse['summary'],
    attention: attention as unknown as DashboardResponse['attention'],
    upcoming: { throughDate: upcoming.throughDate, deadlines, tasks, calendarEvents },
  }
}

function throwForStatus(status: number): never {
  if (status === 401) throw new DashboardRequestError('unauthorized')
  if (status === 403) throw new DashboardRequestError('forbidden')
  throw new DashboardRequestError('unexpected')
}

export async function getDashboard(
  organizationId: string,
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<DashboardResponse> {
  const response = await fetchWithSession(
    `/api/organizations/${encodeURIComponent(organizationId)}/dashboard`,
    { method: 'GET', cache: 'no-store', signal },
    onUnauthorized,
  )
  if (response.status !== 200) throwForStatus(response.status)
  return parseDashboardResponse(await response.json())
}
