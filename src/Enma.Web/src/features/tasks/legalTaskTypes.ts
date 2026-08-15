import type { LegalProcessLookupItem } from '../deadlines/legalDeadlineTypes'

export type LegalTaskState = 'pending' | 'completed'
export type LegalTaskStateFilter = 'pending' | 'completed'
export type LegalTaskMembershipAssigneeFilter = string & {
  readonly legalTaskMembershipAssigneeFilter: unique symbol
}
export type LegalTaskAssigneeFilter =
  | 'any'
  | 'self'
  | 'unassigned'
  | LegalTaskMembershipAssigneeFilter

export interface LegalTaskListItem {
  readonly id: string
  readonly title: string
  readonly dueDate: string | null
  readonly processId: string | null
  readonly processTitle: string | null
  readonly clientName: string | null
  readonly assigneeMembershipId: string | null
  readonly assigneeDisplayName: string | null
  readonly createdByMembershipId: string
  readonly state: LegalTaskState
  readonly createdAt: string
}

export interface LegalTaskListResponse {
  readonly items: readonly LegalTaskListItem[]
  readonly pageNumber: number
  readonly pageSize: number
  readonly hasNext: boolean
}

export interface LegalTaskDetail {
  readonly id: string
  readonly title: string
  readonly description: string | null
  readonly dueDate: string | null
  readonly processId: string | null
  readonly processTitle: string | null
  readonly clientName: string | null
  readonly assigneeMembershipId: string | null
  readonly assigneeDisplayName: string | null
  readonly createdByMembershipId: string
  readonly createdByDisplayName: string
  readonly state: LegalTaskState
  readonly createdAt: string
  readonly completedAt: string | null
}

export interface LegalTaskListQuery {
  readonly state: LegalTaskStateFilter
  readonly processId?: string
  readonly assignee: LegalTaskAssigneeFilter
  readonly pageNumber: number
  readonly pageSize: number
}

export interface CreateLegalTaskRequest {
  readonly title: string
  readonly description: string | null
  readonly dueDate: string | null
  readonly processId: string | null
  readonly assigneeMembershipId: string | null
}

export interface CreateLegalTaskResponse {
  readonly id: string
}

export interface UpdateLegalTaskRequest {
  readonly title: string
  readonly description: string | null
  readonly dueDate: string | null
  readonly processId: string | null
}

export interface ChangeLegalTaskAssigneeRequest {
  readonly assigneeMembershipId: string | null
}

export interface OrganizationMemberLookupItem {
  readonly id: string
  readonly displayName: string
}

export interface OrganizationMemberLookupResponse {
  readonly items: readonly OrganizationMemberLookupItem[]
  readonly pageNumber: number
  readonly pageSize: number
  readonly hasNext: boolean
}

export type { LegalProcessLookupItem }
