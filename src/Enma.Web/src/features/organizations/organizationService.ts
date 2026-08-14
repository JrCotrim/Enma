import {
  fetchWithSession,
  type UnauthorizedHandler,
} from '../authentication/sessionClient'
import type {
  OrganizationNavigationItem,
  OrganizationRole,
} from './organizationTypes'

export class OrganizationDiscoveryUnauthorizedError extends Error {}

function isOrganizationRole(value: unknown): value is OrganizationRole {
  return value === 'Owner' || value === 'Administrator' || value === 'Member'
}

function parseOrganizationNavigationItem(
  value: unknown,
): OrganizationNavigationItem | undefined {
  if (typeof value !== 'object' || value === null) {
    return undefined
  }

  const candidate = value as Record<string, unknown>

  if (
    typeof candidate.id !== 'string' ||
    typeof candidate.name !== 'string' ||
    !isOrganizationRole(candidate.role) ||
    typeof candidate.membershipId !== 'string'
  ) {
    return undefined
  }

  return {
    id: candidate.id,
    name: candidate.name,
    role: candidate.role,
    membershipId: candidate.membershipId,
  }
}

function parseOrganizationResponse(value: unknown): OrganizationNavigationItem[] {
  if (typeof value !== 'object' || value === null) {
    throw new Error('The organization discovery response was invalid.')
  }

  const items = (value as Record<string, unknown>).items

  if (!Array.isArray(items)) {
    throw new Error('The organization discovery response was invalid.')
  }

  const organizations = items.map(parseOrganizationNavigationItem)

  if (organizations.some((organization) => organization === undefined)) {
    throw new Error('The organization discovery response was invalid.')
  }

  return organizations as OrganizationNavigationItem[]
}

export async function getCurrentUserOrganizations(
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<OrganizationNavigationItem[]> {
  const response = await fetchWithSession(
    '/api/me/organizations',
    {
      method: 'GET',
      cache: 'no-store',
      signal,
    },
    onUnauthorized,
  )

  if (response.status === 401) {
    throw new OrganizationDiscoveryUnauthorizedError()
  }

  if (response.status !== 200) {
    throw new Error('Organization discovery failed.')
  }

  return parseOrganizationResponse(await response.json())
}
