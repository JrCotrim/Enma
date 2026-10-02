import type { LegalProcessStatus } from '../processes/legalProcessTypes'

export type LegalDeadlineState = 'Pending' | 'Completed'

export interface LegalDeadlineListItem {
  readonly id: string
  readonly title: string
  readonly dueDate: string
  readonly processId: string
  readonly processTitle: string
  readonly clientName: string
  readonly state: LegalDeadlineState
  readonly responsibleMembershipId: string | null
  readonly responsibleDisplayName: string | null
}

export interface LegalDeadlineListResponse {
  readonly items: readonly LegalDeadlineListItem[]
  readonly pageNumber: number
  readonly pageSize: number
}

export interface LegalDeadline extends LegalDeadlineListItem {
  readonly createdAt: string
  readonly completedAt: string | null
}

export interface LegalDeadlineListFilters {
  readonly responsible?: string
}

export interface CreateLegalDeadlineOptions {
  readonly responsibleMembershipId?: string
}

export interface CreateLegalDeadlineRequest extends CreateLegalDeadlineOptions {
  readonly processId: string
  readonly title: string
  readonly dueDate: string
}

export interface CreateLegalDeadlineResponse {
  readonly id: string
}

export interface UpdateLegalDeadlineRequest {
  readonly title: string
  readonly dueDate: string
}

export interface ChangeLegalDeadlineResponsibleRequest {
  readonly responsibleMembershipId: string | null
}

export interface LegalProcessLookupItem {
  readonly id: string
  readonly title: string
  readonly clientName: string
  readonly processNumber?: string | null
  readonly status?: LegalProcessStatus
  readonly responsibleMembershipId?: string | null
}

export interface LegalProcessLookupResponse {
  readonly items: readonly LegalProcessLookupItem[]
  readonly pageNumber: number
  readonly pageSize: number
  readonly hasNext: boolean
}
