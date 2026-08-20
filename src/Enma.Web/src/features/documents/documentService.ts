import {
  fetchWithSession,
  type UnauthorizedHandler,
} from '../authentication/sessionClient'
import type {
  LegalDocumentListOptions,
  LegalDocumentListResponse,
  LegalDocumentMetadata,
} from './documentTypes'

export type DocumentRequestFailure =
  | 'unauthorized'
  | 'forbidden'
  | 'bad-request'
  | 'not-found'
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

export function getDocumentDownloadUrl(
  organizationId: string,
  documentId: string,
): string {
  return `${getDocumentEndpoint(organizationId, documentId)}/content`
}
