import { clearCsrfToken, getCsrfToken } from '../authentication/csrfClient'
import {
  fetchWithSession,
  type UnauthorizedHandler,
} from '../authentication/sessionClient'
import { isValidDateOnly, isValidGuid } from '../deadlines/legalDeadlineFormatting'
import type {
  NotificationFeed,
  NotificationItem,
  NotificationKind,
  NotificationSourceType,
} from './notificationTypes'

export type NotificationRequestFailure =
  | 'unauthorized'
  | 'forbidden'
  | 'not-found'
  | 'bad-request'
  | 'unexpected'

export class NotificationRequestError extends Error {
  constructor(readonly failure: NotificationRequestFailure) {
    super('The notification request failed.')
  }
}

const notificationKinds: readonly NotificationKind[] = [
  'legalDeadlineDueSoon',
  'legalTaskDueSoon',
  'calendarEventStartingSoon',
]

const notificationSourceTypes: readonly NotificationSourceType[] = [
  'legalDeadline',
  'legalTask',
  'calendarEvent',
]

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

function parseNotificationItem(value: unknown): NotificationItem | undefined {
  if (typeof value !== 'object' || value === null) return undefined

  const item = value as Record<string, unknown>
  if (
    typeof item.id !== 'string' ||
    !isValidGuid(item.id) ||
    !notificationKinds.includes(item.kind as NotificationKind) ||
    !notificationSourceTypes.includes(item.sourceType as NotificationSourceType) ||
    typeof item.sourceId !== 'string' ||
    !isValidGuid(item.sourceId) ||
    typeof item.sourceTitle !== 'string' ||
    item.sourceTitle.trim().length === 0 ||
    (item.occurrenceDate !== null &&
      (typeof item.occurrenceDate !== 'string' ||
        !isValidDateOnly(item.occurrenceDate))) ||
    (item.occurrenceAt !== null && !isTimestamp(item.occurrenceAt)) ||
    !isTimestamp(item.generatedAt) ||
    !isNullableTimestamp(item.readAt)
  ) {
    return undefined
  }

  const isDeadline =
    item.kind === 'legalDeadlineDueSoon' && item.sourceType === 'legalDeadline'
  const isTask =
    item.kind === 'legalTaskDueSoon' && item.sourceType === 'legalTask'
  const isEvent =
    item.kind === 'calendarEventStartingSoon' &&
    item.sourceType === 'calendarEvent'
  const hasDateOnlyOccurrence =
    (isDeadline || isTask) &&
    typeof item.occurrenceDate === 'string' &&
    item.occurrenceAt === null
  const hasInstantOccurrence =
    isEvent && item.occurrenceDate === null && isTimestamp(item.occurrenceAt)

  if (!hasDateOnlyOccurrence && !hasInstantOccurrence) return undefined

  return item as unknown as NotificationItem
}

function parseNotificationFeed(value: unknown): NotificationFeed {
  if (typeof value !== 'object' || value === null) {
    throw new NotificationRequestError('unexpected')
  }

  const candidate = value as Record<string, unknown>
  const items = Array.isArray(candidate.items)
    ? candidate.items.map(parseNotificationItem)
    : undefined

  if (
    !items ||
    items.length > 20 ||
    items.some((item) => item === undefined) ||
    typeof candidate.unreadCount !== 'number' ||
    !Number.isInteger(candidate.unreadCount) ||
    candidate.unreadCount < 0
  ) {
    throw new NotificationRequestError('unexpected')
  }

  return {
    items: items as NotificationItem[],
    unreadCount: candidate.unreadCount,
  }
}

function throwForStatus(status: number): never {
  if (status === 401) throw new NotificationRequestError('unauthorized')
  if (status === 403) throw new NotificationRequestError('forbidden')
  if (status === 404) throw new NotificationRequestError('not-found')
  if (status === 400) throw new NotificationRequestError('bad-request')
  throw new NotificationRequestError('unexpected')
}

function notificationsEndpoint(organizationId: string): string {
  return `/api/organizations/${encodeURIComponent(organizationId)}/notifications`
}

async function sendReadMutation(
  endpoint: string,
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<void> {
  const requestToken = await getCsrfToken()
  const response = await fetchWithSession(
    endpoint,
    {
      method: 'PUT',
      headers: { 'X-CSRF-TOKEN': requestToken },
      cache: 'no-store',
      signal,
    },
    onUnauthorized,
  )

  if (response.status === 204) return
  if (response.status === 400) clearCsrfToken()
  throwForStatus(response.status)
}

export async function getNotifications(
  organizationId: string,
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<NotificationFeed> {
  const response = await fetchWithSession(
    notificationsEndpoint(organizationId),
    { method: 'GET', cache: 'no-store', signal },
    onUnauthorized,
  )

  if (response.status !== 200) throwForStatus(response.status)
  return parseNotificationFeed(await response.json())
}

export function markNotificationAsRead(
  organizationId: string,
  notificationId: string,
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<void> {
  return sendReadMutation(
    `${notificationsEndpoint(organizationId)}/${encodeURIComponent(notificationId)}/read`,
    onUnauthorized,
    signal,
  )
}

export function markAllNotificationsAsRead(
  organizationId: string,
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<void> {
  return sendReadMutation(
    `${notificationsEndpoint(organizationId)}/read-all`,
    onUnauthorized,
    signal,
  )
}
