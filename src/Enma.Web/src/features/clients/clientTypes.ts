export interface Client {
  readonly id: string
  readonly name: string
  readonly isActive: boolean
  readonly createdAt: string
}

export interface ClientListResponse {
  readonly items: readonly Client[]
  readonly pageNumber: number
  readonly pageSize: number
}

export interface CreateClientRequest {
  readonly name: string
}

export interface CreateClientResponse {
  readonly id: string
}
