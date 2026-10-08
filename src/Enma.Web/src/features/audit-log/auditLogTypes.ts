export const auditEventTypes = [
  'organization.renamed',
  'organization_membership.role_changed',
  'organization_membership.deactivated',
  'organization_membership.reactivated',
  'organization_invitation.created',
  'organization_invitation.revoked',
  'organization_invitation.accepted',
  'organization_invitation.resent',
  'client.created',
  'client.renamed',
  'client.profile_updated',
  'client.deactivated',
  'client.reactivated',
  'legal_process.created',
  'legal_process.title_changed',
  'legal_process.details_changed',
  'legal_process.status_changed',
  'legal_process.responsible_changed',
  'legal_deadline.created',
  'legal_deadline.details_changed',
  'legal_deadline.completed',
  'legal_deadline.reopened',
  'legal_deadline.responsible_changed',
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
  'legal_document.deleted',
  'payment_plan.created',
  'payment_installment.paid',
  'payment_installment.payment_reversed',
  'organization.ownership_transferred',
] as const

export type AuditEventType = (typeof auditEventTypes)[number]

export const auditEntityTypes = [
  'organization',
  'organization_membership',
  'organization_invitation',
  'client',
  'legal_process',
  'legal_deadline',
  'legal_task',
  'calendar_event',
  'legal_document',
  'client_payment_plan',
  'payment_installment',
] as const

export type AuditEntityType = (typeof auditEntityTypes)[number]

export const auditLegalProcessStatuses = ['InProgress', 'Suspended', 'Closed'] as const

export type AuditLegalProcessStatus = (typeof auditLegalProcessStatuses)[number]

export const auditPaymentReversalReasons = [
  'RegisteredByMistake',
  'WrongInstallment',
  'PaymentNotCompleted',
  'Other',
] as const

export type AuditPaymentReversalReason =
  (typeof auditPaymentReversalReasons)[number]

export const auditInvitationRoles = ['Administrator', 'Member'] as const

export type AuditInvitationRole = (typeof auditInvitationRoles)[number]

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
      readonly type: 'organization_invitation.created'
      readonly role: AuditInvitationRole
    }
  | {
      readonly type:
        | 'legal_deadline.details_changed'
        | 'legal_task.details_changed'
        | 'calendar_event.updated'
        | 'legal_process.details_changed'
      readonly changedFields: readonly string[]
    }
  | {
      readonly type:
        | 'legal_task.assignee_changed'
        | 'calendar_event.assignee_changed'
      readonly oldAssigneeMembershipId: string | null
      readonly newAssigneeMembershipId: string | null
    }
  | {
      readonly type: 'legal_process.status_changed'
      readonly oldStatus: AuditLegalProcessStatus
      readonly newStatus: AuditLegalProcessStatus
    }
  | {
      readonly type:
        | 'legal_process.responsible_changed'
        | 'legal_deadline.responsible_changed'
      readonly oldResponsibleMembershipId: string | null
      readonly newResponsibleMembershipId: string | null
    }
  | {
      readonly type: 'payment_installment.payment_reversed'
      readonly reason: AuditPaymentReversalReason
    }
  | {
      readonly type: 'organization.ownership_transferred'
      readonly previousOwnerMembershipId: string
      readonly newOwnerMembershipId: string
    }
  | { readonly type: 'unsupported' }

export interface AuditLogItem {
  readonly id: string
  readonly actorMembershipId: string
  readonly actorRoleAtOccurrence: string
  readonly actorDisplayName: string | null
  readonly actorMembershipActive: boolean | null
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
