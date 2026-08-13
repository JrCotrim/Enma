export interface LegalProcessListItem {
  readonly id: string
  readonly title: string
  readonly clientId: string
  readonly clientName: string
  readonly createdAt: string
}

export interface LegalProcessListResponse {
  readonly items: readonly LegalProcessListItem[]
  readonly pageNumber: number
  readonly pageSize: number
}

export interface CreateLegalProcessRequest {
  readonly clientId: string
  readonly title: string
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
