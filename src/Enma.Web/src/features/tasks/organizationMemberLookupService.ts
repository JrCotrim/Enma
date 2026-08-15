import {
  fetchWithSession,
  type UnauthorizedHandler,
} from '../authentication/sessionClient'
import { isValidGuid } from '../deadlines/legalDeadlineFormatting'
import { LegalTaskRequestError } from './legalTaskService'
import type {
  OrganizationMemberLookupItem,
  OrganizationMemberLookupResponse,
} from './legalTaskTypes'

function parseItem(value: unknown): OrganizationMemberLookupItem | undefined {
  if (typeof value !== 'object' || value === null) return undefined
  const candidate = value as Record<string, unknown>
  if (
    typeof candidate.id !== 'string' ||
    !isValidGuid(candidate.id) ||
    typeof candidate.displayName !== 'string' ||
    candidate.displayName.length === 0
  ) {
    return undefined
  }
  return { id: candidate.id, displayName: candidate.displayName }
}

function parseResponse(value: unknown): OrganizationMemberLookupResponse {
  if (typeof value !== 'object' || value === null) {
    throw new LegalTaskRequestError('unexpected')
  }
  const candidate = value as Record<string, unknown>
  const items = Array.isArray(candidate.items)
    ? candidate.items.map(parseItem)
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
    throw new LegalTaskRequestError('unexpected')
  }
  return {
    items: items as OrganizationMemberLookupItem[],
    pageNumber: candidate.pageNumber,
    pageSize: candidate.pageSize,
    hasNext: candidate.hasNext,
  }
}

export async function lookupOrganizationMembers(
  organizationId: string,
  search: string,
  pageNumber: number,
  pageSize: number,
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<OrganizationMemberLookupResponse> {
  const query = new URLSearchParams({
    search,
    pageNumber: pageNumber.toString(),
    pageSize: pageSize.toString(),
  })
  const response = await fetchWithSession(
    `/api/organizations/${encodeURIComponent(organizationId)}/members/lookup?${query.toString()}`,
    { method: 'GET', cache: 'no-store', signal },
    onUnauthorized,
  )
  if (response.status === 401) throw new LegalTaskRequestError('unauthorized')
  if (response.status === 403) throw new LegalTaskRequestError('forbidden')
  if (response.status !== 200) throw new LegalTaskRequestError('unexpected')
  const result = parseResponse(await response.json())
  if (result.pageNumber !== pageNumber || result.pageSize !== pageSize) {
    throw new LegalTaskRequestError('unexpected')
  }
  return result
}
