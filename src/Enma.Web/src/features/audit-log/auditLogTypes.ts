export const auditEventTypes = [
  'organization.renamed',
  'organization_membership.role_changed',
  'organization_membership.deactivated',
  'organization_membership.reactivated',
  'client.created',
  'client.renamed',
  'client.deactivated',
  'client.reactivated',
  'legal_process.created',
  'legal_process.title_changed',
  'legal_deadline.created',
  'legal_deadline.details_changed',
  'legal_deadline.completed',
  'legal_deadline.reopened',
  'legal_task.created',
  'legal_task.details_changed',
  'legal_task.assignee_changed',
  'legal_task.completed',
  'legal_task.reopened',
  'calendar_event.created',
  'calendar_event.updated',
  'calendar_event.assignee_changed',
  'calendar_event.deleted',
  'legal_document.uploaded',
] as const

export type AuditEventType = (typeof auditEventTypes)[number]

export const auditEntityTypes = [
  'organization',
  'organization_membership',
  'client',
  'legal_process',
  'legal_deadline',
  'legal_task',
  'calendar_event',
  'legal_document',
] as const

export type AuditEntityType = (typeof auditEntityTypes)[number]

export type AuditLogDetails =
  | {
      readonly type: 'organization.renamed'
      readonly oldName: string
      readonly newName: string
    }
  | {
      readonly type: 'organization_membership.role_changed'
      readonly oldRole: string
      readonly newRole: string
    }
  | {
      readonly type:
        | 'legal_deadline.details_changed'
        | 'legal_task.details_changed'
        | 'calendar_event.updated'
      readonly changedFields: readonly string[]
    }
  | {
      readonly type:
        | 'legal_task.assignee_changed'
        | 'calendar_event.assignee_changed'
      readonly oldAssigneeMembershipId: string | null
      readonly newAssigneeMembershipId: string | null
    }
  | { readonly type: 'unsupported' }

export interface AuditLogItem {
  readonly id: string
  readonly actorMembershipId: string
  readonly actorRoleAtOccurrence: string
  readonly eventType: string
  readonly entityType: string
  readonly entityId: string
  readonly occurredAt: string
  readonly details: AuditLogDetails | null
}

export interface AuditLogPageResponse {
  readonly items: readonly AuditLogItem[]
  readonly pageNumber: number
  readonly pageSize: number
  readonly totalCount: number
}

export interface AuditLogFilters {
  readonly eventType?: AuditEventType
  readonly entityType?: AuditEntityType
  readonly entityId?: string
  readonly pageNumber: number
  readonly pageSize: number
}
