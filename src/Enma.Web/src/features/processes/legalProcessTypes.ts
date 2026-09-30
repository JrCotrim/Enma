export const legalProcessStatuses = ['inProgress', 'suspended', 'closed'] as const

export type LegalProcessStatus = (typeof legalProcessStatuses)[number]

export interface LegalProcess {
  readonly id: string
  readonly title: string
  readonly clientId: string
  readonly clientName: string
  readonly createdAt: string
  readonly processNumber: string | null
  readonly status: LegalProcessStatus
  readonly courtOrAuthority: string | null
  readonly responsibleMembershipId: string | null
  readonly responsibleDisplayName: string | null
}

export type LegalProcessListItem = LegalProcess

export interface LegalProcessListResponse {
  readonly items: readonly LegalProcessListItem[]
  readonly pageNumber: number
  readonly pageSize: number
  readonly hasNext: boolean
}

export type LegalProcessListSort = 'title' | 'newest'

export interface LegalProcessListFilters {
  readonly search?: string
  readonly status?: LegalProcessStatus
  readonly responsible?: string
  readonly sort?: LegalProcessListSort
}

export interface CreateLegalProcessOptions {
  readonly processNumber?: string
  readonly status?: LegalProcessStatus
  readonly courtOrAuthority?: string
  readonly responsibleMembershipId?: string
}

export interface CreateLegalProcessRequest extends CreateLegalProcessOptions {
  readonly clientId: string
  readonly title: string
}

export interface UpdateLegalProcessRequest {
  readonly title: string
}

export interface ChangeLegalProcessDetailsRequest {
  readonly processNumber: string | null
  readonly courtOrAuthority: string | null
}

export interface ChangeLegalProcessStatusRequest {
  readonly status: LegalProcessStatus
}

export interface ChangeLegalProcessResponsibleRequest {
  readonly responsibleMembershipId: string | null
}

export interface CreateLegalProcessResponse {
  readonly id: string
}

export interface ActiveClientLookupItem {
  readonly id: string
  readonly name: string
}

export interface ActiveClientLookupResponse {
  readonly items: readonly ActiveClientLookupItem[]
  readonly pageNumber: number
  readonly pageSize: number
  readonly hasNext: boolean
}
