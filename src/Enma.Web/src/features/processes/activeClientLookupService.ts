import {
  fetchWithSession,
  type UnauthorizedHandler,
} from '../authentication/sessionClient'
import type {
  ActiveClientLookupItem,
  ActiveClientLookupResponse,
} from './legalProcessTypes'
import { LegalProcessRequestError } from './legalProcessService'

function parseActiveClientLookupItem(
  value: unknown,
): ActiveClientLookupItem | undefined {
  if (typeof value !== 'object' || value === null) {
    return undefined
  }

  const candidate = value as Record<string, unknown>

  if (
    typeof candidate.id !== 'string' ||
    candidate.id.length === 0 ||
    typeof candidate.name !== 'string'
  ) {
    return undefined
  }

  return { id: candidate.id, name: candidate.name }
}

function parseActiveClientLookupResponse(
  value: unknown,
): ActiveClientLookupResponse {
  if (typeof value !== 'object' || value === null) {
    throw new LegalProcessRequestError('unexpected')
  }

  const candidate = value as Record<string, unknown>
  const items = Array.isArray(candidate.items)
    ? candidate.items.map(parseActiveClientLookupItem)
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
    throw new LegalProcessRequestError('unexpected')
  }

  return {
    items: items as ActiveClientLookupItem[],
    pageNumber: candidate.pageNumber,
    pageSize: candidate.pageSize,
    hasNext: candidate.hasNext,
  }
}

export async function lookupActiveClients(
  organizationId: string,
  search: string,
  pageNumber: number,
  pageSize: number,
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<ActiveClientLookupResponse> {
  if (
    !Number.isInteger(pageNumber) ||
    pageNumber < 1 ||
    !Number.isInteger(pageSize) ||
    pageSize < 1 ||
    pageSize > 100
  ) {
    throw new LegalProcessRequestError('bad-request')
  }

  const query = new URLSearchParams({
    search,
    pageNumber: pageNumber.toString(),
    pageSize: pageSize.toString(),
  })
  const response = await fetchWithSession(
    `/api/organizations/${encodeURIComponent(organizationId)}/clients/lookup?${query.toString()}`,
    {
      method: 'GET',
      cache: 'no-store',
      signal,
    },
    onUnauthorized,
  )

  if (response.status === 401) {
    throw new LegalProcessRequestError('unauthorized')
  }

  if (response.status === 403) {
    throw new LegalProcessRequestError('forbidden')
  }

  if (response.status !== 200) {
    throw new LegalProcessRequestError('unexpected')
  }

  const result = parseActiveClientLookupResponse(await response.json())

  if (result.pageNumber !== pageNumber || result.pageSize !== pageSize) {
    throw new LegalProcessRequestError('unexpected')
  }

  return result
}
