import { clearCsrfToken, getCsrfToken } from '../authentication/csrfClient'
import {
  fetchWithSession,
  type UnauthorizedHandler,
} from '../authentication/sessionClient'
import type {
  InvitationDeliveryStatus,
  InvitationMutationResult,
  InvitationRole,
  InvitationStatus,
  OrganizationInvitation,
  OrganizationInvitationPage,
} from './invitationTypes'

export type InvitationRequestFailure =
  | 'unauthorized'
  | 'forbidden'
  | 'not-found'
  | 'bad-request'
  | 'conflict'
  | 'rate-limited'
  | 'unexpected'

export class InvitationRequestError extends Error {
  constructor(
    readonly failure: InvitationRequestFailure,
    readonly retryAfterSeconds?: number,
    readonly responseProcessedAt = Date.now(),
  ) {
    super('The organization invitation request failed.')
  }
}

interface ListInvitationsOptions {
  readonly pageNumber: number
  readonly pageSize: number
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null
}

function isInvitationRole(value: unknown): value is InvitationRole {
  return value === 'Administrator' || value === 'Member'
}

function isInvitationStatus(value: unknown): value is InvitationStatus {
  return (
    value === 'Pending' ||
    value === 'Accepted' ||
    value === 'Revoked' ||
    value === 'Expired'
  )
}

function isDeliveryStatus(value: unknown): value is InvitationDeliveryStatus {
  return value === 'accepted' || value === 'failed'
}

function isTimestamp(value: unknown): value is string {
  return typeof value === 'string' && Number.isFinite(Date.parse(value))
}

function parseInvitation(value: unknown): OrganizationInvitation | undefined {
  if (!isRecord(value)) return undefined

  if (
    typeof value.id !== 'string' ||
    typeof value.invitedEmail !== 'string' ||
    !isInvitationRole(value.role) ||
    !isInvitationStatus(value.status) ||
    !isTimestamp(value.createdAt) ||
    !isTimestamp(value.expiresAt)
  ) {
    return undefined
  }

  return {
    id: value.id,
    invitedEmail: value.invitedEmail,
    role: value.role,
    status: value.status,
    createdAt: value.createdAt,
    expiresAt: value.expiresAt,
  }
}

function parseInvitationPage(
  value: unknown,
  options: ListInvitationsOptions,
): OrganizationInvitationPage {
  if (!isRecord(value)) throw new InvitationRequestError('unexpected')

  const items = Array.isArray(value.items)
    ? value.items.map(parseInvitation)
    : undefined

  if (
    !items ||
    items.some((item) => item === undefined) ||
    !Number.isInteger(value.pageNumber) ||
    value.pageNumber !== options.pageNumber ||
    !Number.isInteger(value.pageSize) ||
    value.pageSize !== options.pageSize ||
    !Number.isInteger(value.totalCount) ||
    typeof value.totalCount !== 'number' ||
    value.totalCount < 0
  ) {
    throw new InvitationRequestError('unexpected')
  }

  return {
    items: items as OrganizationInvitation[],
    pageNumber: value.pageNumber as number,
    pageSize: value.pageSize as number,
    totalCount: value.totalCount,
  }
}

function parseMutationResult(value: unknown): InvitationMutationResult {
  if (!isRecord(value) || !isDeliveryStatus(value.deliveryStatus)) {
    throw new InvitationRequestError('unexpected')
  }

  return { deliveryStatus: value.deliveryStatus }
}

function parseRetryAfter(response: Response): number | undefined {
  const value = response.headers.get('Retry-After')
  if (!value) return undefined

  const seconds = Number(value)
  if (Number.isFinite(seconds) && seconds >= 0) return Math.ceil(seconds)

  const retryAt = Date.parse(value)
  return Number.isFinite(retryAt)
    ? Math.max(0, Math.ceil((retryAt - Date.now()) / 1000))
    : undefined
}

function throwForResponse(response: Response): never {
  switch (response.status) {
    case 400:
      throw new InvitationRequestError('bad-request')
    case 401:
      throw new InvitationRequestError('unauthorized')
    case 403:
      throw new InvitationRequestError('forbidden')
    case 404:
      throw new InvitationRequestError('not-found')
    case 409:
      throw new InvitationRequestError('conflict')
    case 429:
      throw new InvitationRequestError(
        'rate-limited',
        parseRetryAfter(response),
      )
    default:
      throw new InvitationRequestError('unexpected')
  }
}

function getInvitationsEndpoint(organizationId: string): string {
  return `/api/organizations/${encodeURIComponent(organizationId)}/invitations`
}

export async function listInvitations(
  organizationId: string,
  options: ListInvitationsOptions,
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<OrganizationInvitationPage> {
  const query = new URLSearchParams({
    pageNumber: options.pageNumber.toString(),
    pageSize: options.pageSize.toString(),
  })
  const response = await fetchWithSession(
    `${getInvitationsEndpoint(organizationId)}?${query.toString()}`,
    { method: 'GET', cache: 'no-store', signal },
    onUnauthorized,
  )

  if (response.status !== 200) throwForResponse(response)
  return parseInvitationPage(await response.json(), options)
}

async function sendMutation(
  endpoint: string,
  onUnauthorized: UnauthorizedHandler,
  body?: object,
  signal?: AbortSignal,
): Promise<Response> {
  const requestToken = await getCsrfToken()
  const response = await fetchWithSession(
    endpoint,
    {
      method: 'POST',
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

  if (response.status === 400) clearCsrfToken()
  return response
}

export async function createInvitation(
  organizationId: string,
  email: string,
  role: InvitationRole,
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<InvitationMutationResult> {
  const response = await sendMutation(
    getInvitationsEndpoint(organizationId),
    onUnauthorized,
    { email, role },
    signal,
  )

  if (response.status !== 201) throwForResponse(response)
  return parseMutationResult(await response.json())
}

export async function revokeInvitation(
  organizationId: string,
  invitationId: string,
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<void> {
  const response = await sendMutation(
    `${getInvitationsEndpoint(organizationId)}/${encodeURIComponent(invitationId)}/revoke`,
    onUnauthorized,
    undefined,
    signal,
  )

  if (response.status !== 204) throwForResponse(response)
}

export async function resendInvitation(
  organizationId: string,
  invitationId: string,
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<InvitationMutationResult> {
  const response = await sendMutation(
    `${getInvitationsEndpoint(organizationId)}/${encodeURIComponent(invitationId)}/resend`,
    onUnauthorized,
    undefined,
    signal,
  )

  if (response.status !== 200) throwForResponse(response)
  return parseMutationResult(await response.json())
}
