import { clearCsrfToken, getCsrfToken } from '../authentication/csrfClient'
import {
  fetchWithSession,
  type UnauthorizedHandler,
} from '../authentication/sessionClient'
import type { OrganizationRole } from '../organizations/organizationTypes'
import type {
  AdministrativeTeamMember,
  BasicTeamMember,
  TeamMember,
  TeamMemberPage,
  TeamMembershipFilter,
  TeamMemberStatus,
} from './teamTypes'

export type TeamRequestFailure =
  | 'unauthorized'
  | 'forbidden'
  | 'not-found'
  | 'bad-request'
  | 'conflict'
  | 'unexpected'

export class TeamRequestError extends Error {
  constructor(readonly failure: TeamRequestFailure) {
    super('The team request failed.')
  }
}

interface ListTeamMembersOptions {
  readonly status: TeamMembershipFilter
  readonly search?: string
  readonly pageNumber: number
  readonly pageSize: number
  readonly expectAdministrativeDetails: boolean
}

function isOrganizationRole(value: unknown): value is OrganizationRole {
  return value === 'Owner' || value === 'Administrator' || value === 'Member'
}

function isTeamMemberStatus(value: unknown): value is TeamMemberStatus {
  return value === 'Active' || value === 'Inactive'
}

function parseTeamMember(
  value: unknown,
  expectAdministrativeDetails: boolean,
): TeamMember | undefined {
  if (typeof value !== 'object' || value === null) {
    return undefined
  }

  const candidate = value as Record<string, unknown>

  if (
    typeof candidate.id !== 'string' ||
    typeof candidate.name !== 'string' ||
    !isOrganizationRole(candidate.role)
  ) {
    return undefined
  }

  if (!expectAdministrativeDetails) {
    if (
      candidate.email !== undefined ||
      candidate.membershipStatus !== undefined ||
      candidate.accountStatus !== undefined
    ) {
      return undefined
    }

    const member: BasicTeamMember = {
      id: candidate.id,
      name: candidate.name,
      role: candidate.role,
    }
    return member
  }

  if (
    typeof candidate.email !== 'string' ||
    !isTeamMemberStatus(candidate.membershipStatus) ||
    !isTeamMemberStatus(candidate.accountStatus)
  ) {
    return undefined
  }

  const member: AdministrativeTeamMember = {
    id: candidate.id,
    name: candidate.name,
    role: candidate.role,
    email: candidate.email,
    membershipStatus: candidate.membershipStatus,
    accountStatus: candidate.accountStatus,
  }
  return member
}

function parseTeamMemberPage(
  value: unknown,
  options: ListTeamMembersOptions,
): TeamMemberPage {
  if (typeof value !== 'object' || value === null) {
    throw new TeamRequestError('unexpected')
  }

  const candidate = value as Record<string, unknown>
  const items = Array.isArray(candidate.items)
    ? candidate.items.map((item) =>
        parseTeamMember(item, options.expectAdministrativeDetails),
      )
    : undefined

  if (
    !items ||
    items.some((item) => item === undefined) ||
    !Number.isInteger(candidate.pageNumber) ||
    typeof candidate.pageNumber !== 'number' ||
    candidate.pageNumber !== options.pageNumber ||
    !Number.isInteger(candidate.pageSize) ||
    typeof candidate.pageSize !== 'number' ||
    candidate.pageSize !== options.pageSize ||
    !Number.isInteger(candidate.totalCount) ||
    typeof candidate.totalCount !== 'number' ||
    candidate.totalCount < 0
  ) {
    throw new TeamRequestError('unexpected')
  }

  return {
    items: items as TeamMember[],
    pageNumber: candidate.pageNumber,
    pageSize: candidate.pageSize,
    totalCount: candidate.totalCount,
  }
}

function throwForStatus(status: number): never {
  switch (status) {
    case 400:
      throw new TeamRequestError('bad-request')
    case 401:
      throw new TeamRequestError('unauthorized')
    case 403:
      throw new TeamRequestError('forbidden')
    case 404:
      throw new TeamRequestError('not-found')
    case 409:
      throw new TeamRequestError('conflict')
    default:
      throw new TeamRequestError('unexpected')
  }
}

function getMembersEndpoint(organizationId: string): string {
  return `/api/organizations/${encodeURIComponent(organizationId)}/members`
}

export async function listTeamMembers(
  organizationId: string,
  options: ListTeamMembersOptions,
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<TeamMemberPage> {
  const query = new URLSearchParams({
    status: options.status,
    pageNumber: options.pageNumber.toString(),
    pageSize: options.pageSize.toString(),
  })

  if (options.search) {
    query.set('search', options.search)
  }

  const response = await fetchWithSession(
    `${getMembersEndpoint(organizationId)}?${query.toString()}`,
    { method: 'GET', cache: 'no-store', signal },
    onUnauthorized,
  )

  if (response.status !== 200) {
    throwForStatus(response.status)
  }

  return parseTeamMemberPage(await response.json(), options)
}

async function mutateTeam(
  endpoint: string,
  method: 'POST' | 'PUT',
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
  body?: object,
): Promise<void> {
  const requestToken = await getCsrfToken()
  const response = await fetchWithSession(
    endpoint,
    {
      method,
      headers: body
        ? {
            'Content-Type': 'application/json',
            'X-CSRF-TOKEN': requestToken,
          }
        : { 'X-CSRF-TOKEN': requestToken },
      body: body ? JSON.stringify(body) : undefined,
      cache: 'no-store',
      signal,
    },
    onUnauthorized,
  )

  if (response.status !== 204) {
    if (response.status === 400) {
      clearCsrfToken()
    }

    throwForStatus(response.status)
  }
}

export function changeTeamMemberRole(
  organizationId: string,
  membershipId: string,
  role: 'Administrator' | 'Member',
  expectedCurrentRole: 'Administrator' | 'Member',
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<void> {
  return mutateTeam(
    `${getMembersEndpoint(organizationId)}/${encodeURIComponent(membershipId)}/role`,
    'PUT',
    onUnauthorized,
    signal,
    { role, expectedCurrentRole },
  )
}

export function changeTeamMemberLifecycle(
  organizationId: string,
  membershipId: string,
  operation: 'deactivate' | 'reactivate',
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<void> {
  return mutateTeam(
    `${getMembersEndpoint(organizationId)}/${encodeURIComponent(membershipId)}/${operation}`,
    'POST',
    onUnauthorized,
    signal,
  )
}

export function updateOrganizationName(
  organizationId: string,
  name: string,
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<void> {
  return mutateTeam(
    `/api/organizations/${encodeURIComponent(organizationId)}`,
    'PUT',
    onUnauthorized,
    signal,
    { name },
  )
}
