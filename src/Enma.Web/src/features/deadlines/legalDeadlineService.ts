import { clearCsrfToken, getCsrfToken } from '../authentication/csrfClient'
import {
  fetchWithSession,
  type UnauthorizedHandler,
} from '../authentication/sessionClient'
import { isValidDateOnly } from './legalDeadlineFormatting'
import type {
  CreateLegalDeadlineRequest,
  CreateLegalDeadlineResponse,
  LegalDeadlineListItem,
  LegalDeadlineListResponse,
} from './legalDeadlineTypes'

export type LegalDeadlineRequestFailure =
  | 'unauthorized'
  | 'forbidden'
  | 'not-found'
  | 'bad-request'
  | 'unexpected'

export class LegalDeadlineRequestError extends Error {
  constructor(readonly failure: LegalDeadlineRequestFailure) {
    super('The legal deadline request failed.')
  }
}

function parseLegalDeadlineListItem(
  value: unknown,
): LegalDeadlineListItem | undefined {
  if (typeof value !== 'object' || value === null) {
    return undefined
  }

  const candidate = value as Record<string, unknown>

  if (
    typeof candidate.id !== 'string' ||
    candidate.id.length === 0 ||
    typeof candidate.title !== 'string' ||
    candidate.title.length === 0 ||
    typeof candidate.dueDate !== 'string' ||
    !isValidDateOnly(candidate.dueDate) ||
    typeof candidate.processId !== 'string' ||
    candidate.processId.length === 0 ||
    typeof candidate.processTitle !== 'string' ||
    candidate.processTitle.length === 0 ||
    typeof candidate.clientName !== 'string' ||
    candidate.clientName.length === 0 ||
    (candidate.state !== 'Pending' && candidate.state !== 'Completed')
  ) {
    return undefined
  }

  return {
    id: candidate.id,
    title: candidate.title,
    dueDate: candidate.dueDate,
    processId: candidate.processId,
    processTitle: candidate.processTitle,
    clientName: candidate.clientName,
    state: candidate.state,
  }
}

function parseLegalDeadlineListResponse(
  value: unknown,
): LegalDeadlineListResponse {
  if (typeof value !== 'object' || value === null) {
    throw new LegalDeadlineRequestError('unexpected')
  }

  const candidate = value as Record<string, unknown>
  const items = Array.isArray(candidate.items)
    ? candidate.items.map(parseLegalDeadlineListItem)
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
    candidate.pageSize > 100
  ) {
    throw new LegalDeadlineRequestError('unexpected')
  }

  return {
    items: items as LegalDeadlineListItem[],
    pageNumber: candidate.pageNumber,
    pageSize: candidate.pageSize,
  }
}

function parseCreateLegalDeadlineResponse(
  value: unknown,
): CreateLegalDeadlineResponse {
  if (typeof value !== 'object' || value === null) {
    throw new LegalDeadlineRequestError('unexpected')
  }

  const id = (value as Record<string, unknown>).id

  if (typeof id !== 'string' || id.length === 0) {
    throw new LegalDeadlineRequestError('unexpected')
  }

  return { id }
}

function throwForStatus(status: number): never {
  if (status === 401) {
    throw new LegalDeadlineRequestError('unauthorized')
  }

  if (status === 403) {
    throw new LegalDeadlineRequestError('forbidden')
  }

  if (status === 404) {
    throw new LegalDeadlineRequestError('not-found')
  }

  if (status === 400) {
    throw new LegalDeadlineRequestError('bad-request')
  }

  throw new LegalDeadlineRequestError('unexpected')
}

function getLegalDeadlinesEndpoint(organizationId: string): string {
  return `/api/organizations/${encodeURIComponent(organizationId)}/deadlines`
}

export async function listLegalDeadlines(
  organizationId: string,
  pageNumber: number,
  pageSize: number,
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<LegalDeadlineListResponse> {
  if (
    !Number.isInteger(pageNumber) ||
    pageNumber < 1 ||
    !Number.isInteger(pageSize) ||
    pageSize < 1 ||
    pageSize > 100
  ) {
    throw new LegalDeadlineRequestError('bad-request')
  }

  const query = new URLSearchParams({
    pageNumber: pageNumber.toString(),
    pageSize: pageSize.toString(),
  })
  const response = await fetchWithSession(
    `${getLegalDeadlinesEndpoint(organizationId)}?${query.toString()}`,
    {
      method: 'GET',
      cache: 'no-store',
      signal,
    },
    onUnauthorized,
  )

  if (response.status !== 200) {
    throwForStatus(response.status)
  }

  const result = parseLegalDeadlineListResponse(await response.json())

  if (result.pageNumber !== pageNumber || result.pageSize !== pageSize) {
    throw new LegalDeadlineRequestError('unexpected')
  }

  return result
}

export async function createLegalDeadline(
  organizationId: string,
  processId: string,
  title: string,
  dueDate: string,
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<CreateLegalDeadlineResponse> {
  const requestToken = await getCsrfToken()
  const body: CreateLegalDeadlineRequest = { processId, title, dueDate }
  const response = await fetchWithSession(
    getLegalDeadlinesEndpoint(organizationId),
    {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'X-CSRF-TOKEN': requestToken,
      },
      body: JSON.stringify(body),
      cache: 'no-store',
      signal,
    },
    onUnauthorized,
  )

  if (response.status !== 201) {
    if (response.status === 400) {
      clearCsrfToken()
    }

    throwForStatus(response.status)
  }

  return parseCreateLegalDeadlineResponse(await response.json())
}
