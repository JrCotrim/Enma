export interface LegalDocumentMetadata {
  readonly id: string
  readonly clientId: string | null
  readonly processId: string | null
  readonly originalFileName: string
  readonly contentType: string
  readonly sizeBytes: number
  readonly createdAt: string
}

export interface LegalDocumentListResponse {
  readonly items: readonly LegalDocumentMetadata[]
  readonly pageNumber: number
  readonly pageSize: number
  readonly hasNext: boolean
}

export interface LegalDocumentListOptions {
  readonly search?: string
  readonly clientId?: string
  readonly processId?: string
  readonly pageNumber: number
  readonly pageSize: number
}

export type LegalDocumentUploadClassification =
  | { readonly kind: 'general' }
  | { readonly kind: 'client'; readonly clientId: string }
  | { readonly kind: 'process'; readonly processId: string }

export interface UploadLegalDocumentResponse {
  readonly id: string
}
