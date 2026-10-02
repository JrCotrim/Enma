export const clientPersonTypes = ['individual', 'company'] as const

export type ClientPersonType = (typeof clientPersonTypes)[number]

export interface Client {
  readonly id: string
  readonly name: string
  readonly isActive: boolean
  readonly createdAt: string
}

export interface ClientDetail extends Client {
  readonly email: string | null
  readonly phone: string | null
  readonly cpf: string | null
  readonly personType: ClientPersonType
  readonly cnpj: string | null
  readonly address: string | null
  readonly notes: string | null
}

export interface ClientListResponse {
  readonly items: readonly Client[]
  readonly pageNumber: number
  readonly pageSize: number
}

export interface CreateClientRequest {
  readonly name: string
  readonly email: string | null
  readonly phone: string | null
  readonly cpf: string | null
}

export interface UpdateClientRequest {
  readonly name: string
  readonly email: string | null
  readonly phone: string | null
  readonly cpf: string | null
  readonly personType: ClientPersonType
  readonly cnpj: string | null
  readonly address: string | null
  readonly notes: string | null
}

export interface CreateClientResponse {
  readonly id: string
}
