import { clearCsrfToken, getCsrfToken } from '../authentication/csrfClient'
import {
  fetchWithSession,
  type UnauthorizedHandler,
} from '../authentication/sessionClient'
import type {
  Client,
  ClientListResponse,
  CreateClientRequest,
  CreateClientResponse,
} from './clientTypes'

export type ClientRequestFailure =
  | 'unauthorized'
  | 'forbidden'
  | 'bad-request'
  | 'unexpected'

export class ClientRequestError extends Error {
  constructor(readonly failure: ClientRequestFailure) {
    super('The client request failed.')
  }
}

function parseClient(value: unknown): Client | undefined {
  if (typeof value !== 'object' || value === null) {
    return undefined
  }

  const candidate = value as Record<string, unknown>

  if (
    typeof candidate.id !== 'string' ||
    typeof candidate.name !== 'string' ||
    typeof candidate.isActive !== 'boolean' ||
    typeof candidate.createdAt !== 'string' ||
    Number.isNaN(Date.parse(candidate.createdAt))
  ) {
    return undefined
  }

  return {
    id: candidate.id,
    name: candidate.name,
    isActive: candidate.isActive,
    createdAt: candidate.createdAt,
  }
}

function parseClientListResponse(value: unknown): ClientListResponse {
  if (typeof value !== 'object' || value === null) {
    throw new ClientRequestError('unexpected')
  }

  const candidate = value as Record<string, unknown>
  const items = Array.isArray(candidate.items)
    ? candidate.items.map(parseClient)
    : undefined

  if (
    !items ||
    items.some((item) => item === undefined) ||
    !Number.isInteger(candidate.pageNumber) ||
    typeof candidate.pageNumber !== 'number' ||
    candidate.pageNumber < 1 ||
    !Number.isInteger(candidate.pageSize) ||
    typeof candidate.pageSize !== 'number' ||
    candidate.pageSize < 1 ||
    candidate.pageSize > 100
  ) {
    throw new ClientRequestError('unexpected')
  }

  return {
    items: items as Client[],
    pageNumber: candidate.pageNumber,
    pageSize: candidate.pageSize,
  }
}

function parseCreateClientResponse(value: unknown): CreateClientResponse {
  if (typeof value !== 'object' || value === null) {
    throw new ClientRequestError('unexpected')
  }

  const id = (value as Record<string, unknown>).id

  if (typeof id !== 'string' || id.length === 0) {
    throw new ClientRequestError('unexpected')
  }

  return { id }
}

function throwForStatus(status: number): never {
  if (status === 401) {
    throw new ClientRequestError('unauthorized')
  }

  if (status === 403) {
    throw new ClientRequestError('forbidden')
  }

  if (status === 400) {
    throw new ClientRequestError('bad-request')
  }

  throw new ClientRequestError('unexpected')
}

function getClientsEndpoint(organizationId: string): string {
  return `/api/organizations/${encodeURIComponent(organizationId)}/clients`
}

export async function listClients(
  organizationId: string,
  pageNumber: number,
  pageSize: number,
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<ClientListResponse> {
  if (
    !Number.isInteger(pageNumber) ||
    pageNumber < 1 ||
    !Number.isInteger(pageSize) ||
    pageSize < 1 ||
    pageSize > 100
  ) {
    throw new ClientRequestError('bad-request')
  }

  const query = new URLSearchParams({
    pageNumber: pageNumber.toString(),
    pageSize: pageSize.toString(),
  })
  const response = await fetchWithSession(
    `${getClientsEndpoint(organizationId)}?${query.toString()}`,
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

  const result = parseClientListResponse(await response.json())

  if (result.pageNumber !== pageNumber || result.pageSize !== pageSize) {
    throw new ClientRequestError('unexpected')
  }

  return result
}

export async function createClient(
  organizationId: string,
  name: string,
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<CreateClientResponse> {
  const requestToken = await getCsrfToken()
  const body: CreateClientRequest = { name }
  const response = await fetchWithSession(
    getClientsEndpoint(organizationId),
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

  return parseCreateClientResponse(await response.json())
}
