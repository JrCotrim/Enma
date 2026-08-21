import { clearCsrfToken, getCsrfToken } from '../authentication/csrfClient'
import {
  fetchWithSession,
  type UnauthorizedHandler,
} from '../authentication/sessionClient'
import type {
  LegalDocumentListOptions,
  LegalDocumentListResponse,
  LegalDocumentMetadata,
  LegalDocumentUploadClassification,
  UploadLegalDocumentResponse,
} from './documentTypes'

const guidPattern =
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

export type DocumentRequestFailure =
  | 'unauthorized'
  | 'forbidden'
  | 'bad-request'
  | 'not-found'
  | 'too-large'
  | 'unavailable'
  | 'outcome-unknown'
  | 'unexpected'

export class DocumentRequestError extends Error {
  constructor(readonly failure: DocumentRequestFailure) {
    super(`Document request failed: ${failure}`)
  }
}

function parseOptionalId(value: unknown): string | null | undefined {
  return value === null || (typeof value === 'string' && value.length > 0)
    ? value
    : undefined
}

function parseDocumentMetadata(
  value: unknown,
): LegalDocumentMetadata | undefined {
  if (typeof value !== 'object' || value === null) {
    return undefined
  }

  const candidate = value as Record<string, unknown>
  const clientId = parseOptionalId(candidate.clientId)
  const processId = parseOptionalId(candidate.processId)
  const createdAt =
    typeof candidate.createdAt === 'string'
      ? Date.parse(candidate.createdAt)
      : Number.NaN

  if (
    typeof candidate.id !== 'string' ||
    candidate.id.length === 0 ||
    clientId === undefined ||
    processId === undefined ||
    (clientId !== null && processId !== null) ||
    typeof candidate.originalFileName !== 'string' ||
    candidate.originalFileName.length === 0 ||
    typeof candidate.contentType !== 'string' ||
    candidate.contentType.length === 0 ||
    typeof candidate.sizeBytes !== 'number' ||
    !Number.isSafeInteger(candidate.sizeBytes) ||
    candidate.sizeBytes < 0 ||
    !Number.isFinite(createdAt)
  ) {
    return undefined
  }

  return {
    id: candidate.id,
    clientId,
    processId,
    originalFileName: candidate.originalFileName,
    contentType: candidate.contentType,
    sizeBytes: candidate.sizeBytes,
    createdAt: candidate.createdAt as string,
  }
}

function parseDocumentListResponse(value: unknown): LegalDocumentListResponse {
  if (typeof value !== 'object' || value === null) {
    throw new DocumentRequestError('unexpected')
  }

  const candidate = value as Record<string, unknown>
  const items = Array.isArray(candidate.items)
    ? candidate.items.map(parseDocumentMetadata)
    : undefined

  if (
    !items ||
    items.some((item) => item === undefined) ||
    typeof candidate.pageNumber !== 'number' ||
    !Number.isInteger(candidate.pageNumber) ||
    candidate.pageNumber < 1 ||
    typeof candidate.pageSize !== 'number' ||
    !Number.isInteger(candidate.pageSize) ||
    candidate.pageSize < 1 ||
    candidate.pageSize > 100 ||
    typeof candidate.hasNext !== 'boolean'
  ) {
    throw new DocumentRequestError('unexpected')
  }

  return {
    items: items as LegalDocumentMetadata[],
    pageNumber: candidate.pageNumber,
    pageSize: candidate.pageSize,
    hasNext: candidate.hasNext,
  }
}

function parseUploadResponse(value: unknown): UploadLegalDocumentResponse {
  if (typeof value !== 'object' || value === null) {
    throw new DocumentRequestError('unexpected')
  }

  const id = (value as Record<string, unknown>).id
  if (typeof id !== 'string' || !guidPattern.test(id)) {
    throw new DocumentRequestError('unexpected')
  }

  return { id }
}

async function hasOutcomeUnknownTitle(response: Response): Promise<boolean> {
  try {
    const value = (await response.json()) as unknown
    return (
      typeof value === 'object' &&
      value !== null &&
      (value as Record<string, unknown>).title ===
        'Document upload outcome unknown'
    )
  } catch {
    return false
  }
}

function throwForStatus(status: number): never {
  if (status === 401) throw new DocumentRequestError('unauthorized')
  if (status === 403) throw new DocumentRequestError('forbidden')
  if (status === 400) throw new DocumentRequestError('bad-request')
  if (status === 404) throw new DocumentRequestError('not-found')
  throw new DocumentRequestError('unexpected')
}

function getDocumentsEndpoint(organizationId: string): string {
  return `/api/organizations/${encodeURIComponent(organizationId)}/documents`
}

function getDocumentEndpoint(
  organizationId: string,
  documentId: string,
): string {
  return `${getDocumentsEndpoint(organizationId)}/${encodeURIComponent(documentId)}`
}

export async function listDocuments(
  organizationId: string,
  options: LegalDocumentListOptions,
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<LegalDocumentListResponse> {
  if (
    options.clientId !== undefined &&
    options.processId !== undefined
  ) {
    throw new DocumentRequestError('bad-request')
  }

  const query = new URLSearchParams({
    page: options.pageNumber.toString(),
    pageSize: options.pageSize.toString(),
  })
  if (options.search) query.set('search', options.search)
  if (options.clientId) query.set('clientId', options.clientId)
  if (options.processId) query.set('processId', options.processId)

  const response = await fetchWithSession(
    `${getDocumentsEndpoint(organizationId)}?${query.toString()}`,
    { method: 'GET', cache: 'no-store', signal },
    onUnauthorized,
  )

  if (response.status !== 200) throwForStatus(response.status)

  const result = parseDocumentListResponse(await response.json())
  if (
    result.pageNumber !== options.pageNumber ||
    result.pageSize !== options.pageSize
  ) {
    throw new DocumentRequestError('unexpected')
  }

  return result
}

export async function getDocument(
  organizationId: string,
  documentId: string,
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<LegalDocumentMetadata> {
  const response = await fetchWithSession(
    getDocumentEndpoint(organizationId, documentId),
    { method: 'GET', cache: 'no-store', signal },
    onUnauthorized,
  )

  if (response.status !== 200) throwForStatus(response.status)

  const document = parseDocumentMetadata(await response.json())
  if (!document || document.id.toLowerCase() !== documentId.toLowerCase()) {
    throw new DocumentRequestError('unexpected')
  }

  return document
}

export async function uploadDocument(
  organizationId: string,
  file: File,
  classification: LegalDocumentUploadClassification,
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<UploadLegalDocumentResponse> {
  const formData = new FormData()
  formData.append('file', file)

  if (classification.kind === 'client') {
    if (classification.clientId.length === 0) {
      throw new DocumentRequestError('bad-request')
    }
    formData.append('clientId', classification.clientId)
  } else if (classification.kind === 'process') {
    if (classification.processId.length === 0) {
      throw new DocumentRequestError('bad-request')
    }
    formData.append('processId', classification.processId)
  }

  const requestToken = await getCsrfToken()
  const response = await fetchWithSession(
    getDocumentsEndpoint(organizationId),
    {
      method: 'POST',
      headers: { 'X-CSRF-TOKEN': requestToken },
      body: formData,
      cache: 'no-store',
      signal,
    },
    onUnauthorized,
  )

  if (response.status === 201) {
    return parseUploadResponse(await response.json())
  }

  if (response.status === 400) {
    clearCsrfToken()
    throw new DocumentRequestError('bad-request')
  }
  if (response.status === 401) throw new DocumentRequestError('unauthorized')
  if (response.status === 403) throw new DocumentRequestError('forbidden')
  if (response.status === 404) throw new DocumentRequestError('not-found')
  if (response.status === 413) throw new DocumentRequestError('too-large')
  if (response.status === 503) throw new DocumentRequestError('unavailable')
  if (response.status === 500 && await hasOutcomeUnknownTitle(response)) {
    throw new DocumentRequestError('outcome-unknown')
  }

  throw new DocumentRequestError('unexpected')
}

export function getDocumentDownloadUrl(
  organizationId: string,
  documentId: string,
): string {
  return `${getDocumentEndpoint(organizationId, documentId)}/content`
}
