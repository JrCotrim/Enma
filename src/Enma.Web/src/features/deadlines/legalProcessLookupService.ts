import {
  fetchWithSession,
  type UnauthorizedHandler,
} from '../authentication/sessionClient'
import { LegalDeadlineRequestError } from './legalDeadlineService'
import type {
  LegalProcessLookupItem,
  LegalProcessLookupResponse,
} from './legalDeadlineTypes'

function parseLegalProcessLookupItem(
  value: unknown,
): LegalProcessLookupItem | undefined {
  if (typeof value !== 'object' || value === null) {
    return undefined
  }

  const candidate = value as Record<string, unknown>

  if (
    typeof candidate.id !== 'string' ||
    candidate.id.length === 0 ||
    typeof candidate.title !== 'string' ||
    candidate.title.length === 0 ||
    typeof candidate.clientName !== 'string' ||
    candidate.clientName.length === 0
  ) {
    return undefined
  }

  return {
    id: candidate.id,
    title: candidate.title,
    clientName: candidate.clientName,
  }
}

function parseLegalProcessLookupResponse(
  value: unknown,
): LegalProcessLookupResponse {
  if (typeof value !== 'object' || value === null) {
    throw new LegalDeadlineRequestError('unexpected')
  }

  const candidate = value as Record<string, unknown>
  const items = Array.isArray(candidate.items)
    ? candidate.items.map(parseLegalProcessLookupItem)
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
    throw new LegalDeadlineRequestError('unexpected')
  }

  return {
    items: items as LegalProcessLookupItem[],
    pageNumber: candidate.pageNumber,
    pageSize: candidate.pageSize,
    hasNext: candidate.hasNext,
  }
}

export async function lookupLegalProcesses(
  organizationId: string,
  search: string,
  pageNumber: number,
  pageSize: number,
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<LegalProcessLookupResponse> {
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
    search,
    pageNumber: pageNumber.toString(),
    pageSize: pageSize.toString(),
  })
  const response = await fetchWithSession(
    `/api/organizations/${encodeURIComponent(organizationId)}/processes/lookup?${query.toString()}`,
    {
      method: 'GET',
      cache: 'no-store',
      signal,
    },
    onUnauthorized,
  )

  if (response.status === 401) {
    throw new LegalDeadlineRequestError('unauthorized')
  }

  if (response.status === 403) {
    throw new LegalDeadlineRequestError('forbidden')
  }

  if (response.status !== 200) {
    throw new LegalDeadlineRequestError('unexpected')
  }

  const result = parseLegalProcessLookupResponse(await response.json())

  if (result.pageNumber !== pageNumber || result.pageSize !== pageSize) {
    throw new LegalDeadlineRequestError('unexpected')
  }

  return result
}
