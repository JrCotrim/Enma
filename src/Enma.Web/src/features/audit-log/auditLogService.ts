import {
  fetchWithSession,
  type UnauthorizedHandler,
} from '../authentication/sessionClient'
import { isValidGuid } from '../deadlines/legalDeadlineFormatting'
import {
  auditEntityTypes,
  auditEventTypes,
  type AuditEntityType,
  type AuditEventType,
  type AuditLogDetails,
  type AuditLogFilters,
  type AuditLogItem,
  type AuditLogPageResponse,
} from './auditLogTypes'

export type AuditLogRequestFailure =
  | 'unauthorized'
  | 'forbidden'
  | 'bad-request'
  | 'unexpected'

export class AuditLogRequestError extends Error {
  constructor(readonly failure: AuditLogRequestFailure) {
    super('The audit log request failed.')
  }
}

const emptyGuid = '00000000-0000-0000-0000-000000000000'
const knownEventTypes = new Set<string>(auditEventTypes)
const knownEntityTypes = new Set<string>(auditEntityTypes)
const knownOrganizationRoles = new Set(['Owner', 'Administrator', 'Member'])
const knownChangedFields = {
  'legal_deadline.details_changed': new Set(['Title', 'DueDate']),
  'legal_task.details_changed': new Set([
    'Title',
    'Description',
    'DueDate',
    'ProcessId',
  ]),
  'calendar_event.updated': new Set([
    'Title',
    'Description',
    'StartsAt',
    'EndsAt',
    'Location',
    'ClientId',
    'ProcessId',
  ]),
} as const

export function isAuditEventType(value: string): value is AuditEventType {
  return knownEventTypes.has(value)
}

export function isAuditEntityType(value: string): value is AuditEntityType {
  return knownEntityTypes.has(value)
}

export function isUsableGuid(value: string): boolean {
  return isValidGuid(value) && value.toLowerCase() !== emptyGuid
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null
}

function parseStringArray(value: unknown): readonly string[] | undefined {
  return Array.isArray(value) && value.every((item) => typeof item === 'string')
    ? value
    : undefined
}

function parseChangedFields(
  value: unknown,
  eventType: keyof typeof knownChangedFields,
): readonly string[] | undefined {
  const fields = parseStringArray(value)
  if (
    !fields ||
    fields.length === 0 ||
    new Set(fields).size !== fields.length ||
    fields.some((field) => !knownChangedFields[eventType].has(field))
  ) {
    return undefined
  }

  return fields
}

function parseNullableGuid(value: unknown): string | null | undefined {
  if (value === null) return null
  return typeof value === 'string' && isUsableGuid(value) ? value : undefined
}

function parseDetails(value: unknown, eventType: string): AuditLogDetails | null {
  if (value === null) return null
  if (!isRecord(value) || typeof value.type !== 'string') {
    throw new AuditLogRequestError('unexpected')
  }

  if (value.type !== eventType) return { type: 'unsupported' }

  switch (value.type) {
    case 'organization.renamed':
      if (typeof value.oldName === 'string' && typeof value.newName === 'string') {
        return { type: value.type, oldName: value.oldName, newName: value.newName }
      }
      break
    case 'organization_membership.role_changed':
      if (
        typeof value.oldRole === 'string' &&
        typeof value.newRole === 'string' &&
        value.oldRole !== value.newRole &&
        knownOrganizationRoles.has(value.oldRole) &&
        knownOrganizationRoles.has(value.newRole)
      ) {
        return { type: value.type, oldRole: value.oldRole, newRole: value.newRole }
      }
      return { type: 'unsupported' }
    case 'legal_deadline.details_changed':
    case 'legal_task.details_changed':
    case 'calendar_event.updated': {
      const changedFields = parseChangedFields(value.changedFields, value.type)
      if (changedFields) return { type: value.type, changedFields }
      return { type: 'unsupported' }
    }
    case 'legal_task.assignee_changed':
    case 'calendar_event.assignee_changed': {
      const oldAssigneeMembershipId = parseNullableGuid(value.oldAssigneeMembershipId)
      const newAssigneeMembershipId = parseNullableGuid(value.newAssigneeMembershipId)
      if (
        oldAssigneeMembershipId !== undefined &&
        newAssigneeMembershipId !== undefined &&
        oldAssigneeMembershipId !== newAssigneeMembershipId
      ) {
        return {
          type: value.type,
          oldAssigneeMembershipId,
          newAssigneeMembershipId,
        }
      }
      return { type: 'unsupported' }
    }
    default:
      return { type: 'unsupported' }
  }

  throw new AuditLogRequestError('unexpected')
}

function parseItem(value: unknown): AuditLogItem | undefined {
  if (!isRecord(value)) return undefined

  if (
    typeof value.id !== 'string' ||
    !isUsableGuid(value.id) ||
    typeof value.actorMembershipId !== 'string' ||
    !isUsableGuid(value.actorMembershipId) ||
    typeof value.actorRoleAtOccurrence !== 'string' ||
    typeof value.eventType !== 'string' ||
    typeof value.entityType !== 'string' ||
    typeof value.entityId !== 'string' ||
    !isUsableGuid(value.entityId) ||
    typeof value.occurredAt !== 'string' ||
    Number.isNaN(Date.parse(value.occurredAt))
  ) {
    return undefined
  }

  return {
    id: value.id,
    actorMembershipId: value.actorMembershipId,
    actorRoleAtOccurrence: value.actorRoleAtOccurrence,
    eventType: value.eventType,
    entityType: value.entityType,
    entityId: value.entityId,
    occurredAt: value.occurredAt,
    details: parseDetails(value.details, value.eventType),
  }
}

function parseResponse(value: unknown): AuditLogPageResponse {
  if (!isRecord(value)) throw new AuditLogRequestError('unexpected')
  const items = Array.isArray(value.items) ? value.items.map(parseItem) : undefined

  if (
    !items ||
    items.some((item) => item === undefined) ||
    !Number.isInteger(value.pageNumber) ||
    (value.pageNumber as number) < 1 ||
    !Number.isInteger(value.pageSize) ||
    (value.pageSize as number) < 1 ||
    (value.pageSize as number) > 100 ||
    !Number.isInteger(value.totalCount) ||
    (value.totalCount as number) < 0
  ) {
    throw new AuditLogRequestError('unexpected')
  }

  return {
    items: items as AuditLogItem[],
    pageNumber: value.pageNumber as number,
    pageSize: value.pageSize as number,
    totalCount: value.totalCount as number,
  }
}

function throwForStatus(status: number): never {
  if (status === 401) throw new AuditLogRequestError('unauthorized')
  if (status === 403) throw new AuditLogRequestError('forbidden')
  if (status === 400) throw new AuditLogRequestError('bad-request')
  throw new AuditLogRequestError('unexpected')
}

export async function listAuditLogs(
  organizationId: string,
  filters: AuditLogFilters,
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<AuditLogPageResponse> {
  const hasEntityType = filters.entityType !== undefined
  const hasEntityId = filters.entityId !== undefined
  if (
    !isUsableGuid(organizationId) ||
    !Number.isInteger(filters.pageNumber) ||
    filters.pageNumber < 1 ||
    !Number.isInteger(filters.pageSize) ||
    filters.pageSize < 1 ||
    filters.pageSize > 100 ||
    hasEntityType !== hasEntityId ||
    (filters.entityId !== undefined && !isUsableGuid(filters.entityId))
  ) {
    throw new AuditLogRequestError('bad-request')
  }

  const query = new URLSearchParams({
    pageNumber: filters.pageNumber.toString(),
    pageSize: filters.pageSize.toString(),
  })
  if (filters.eventType) query.set('eventType', filters.eventType)
  if (filters.entityType && filters.entityId) {
    query.set('entityType', filters.entityType)
    query.set('entityId', filters.entityId)
  }

  const response = await fetchWithSession(
    `/api/organizations/${encodeURIComponent(organizationId)}/audit-logs?${query.toString()}`,
    { method: 'GET', cache: 'no-store', signal },
    onUnauthorized,
  )
  if (response.status !== 200) throwForStatus(response.status)

  const result = parseResponse(await response.json())
  if (result.pageNumber !== filters.pageNumber || result.pageSize !== filters.pageSize) {
    throw new AuditLogRequestError('unexpected')
  }
  return result
}
