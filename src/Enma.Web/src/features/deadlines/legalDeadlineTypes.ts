export type LegalDeadlineState = 'Pending' | 'Completed'

export interface LegalDeadlineListItem {
  readonly id: string
  readonly title: string
  readonly dueDate: string
  readonly processId: string
  readonly processTitle: string
  readonly clientName: string
  readonly state: LegalDeadlineState
}

export interface LegalDeadlineListResponse {
  readonly items: readonly LegalDeadlineListItem[]
  readonly pageNumber: number
  readonly pageSize: number
}

export interface CreateLegalDeadlineRequest {
  readonly processId: string
  readonly title: string
  readonly dueDate: string
}

export interface CreateLegalDeadlineResponse {
  readonly id: string
}

export interface LegalProcessLookupItem {
  readonly id: string
  readonly title: string
  readonly clientName: string
}

export interface LegalProcessLookupResponse {
  readonly items: readonly LegalProcessLookupItem[]
  readonly pageNumber: number
  readonly pageSize: number
  readonly hasNext: boolean
}
