import { clearCsrfToken, getCsrfToken } from '../authentication/csrfClient'
import {
  fetchWithSession,
  type UnauthorizedHandler,
} from '../authentication/sessionClient'
import {
  clientPersonTypes,
  type Client,
  type ClientDetail,
  type ClientListResponse,
  type ClientPersonType,
  type CreateClientRequest,
  type CreateClientResponse,
  type UpdateClientRequest,
} from './clientTypes'

export type ClientRequestFailure =
  | 'unauthorized'
  | 'forbidden'
  | 'not-found'
  | 'bad-request'
  | 'duplicate-document'
  | 'unexpected'

export const clientRequestFields = [
  'name',
  'email',
  'phone',
  'cpf',
  'cnpj',
  'address',
  'notes',
] as const

export type ClientRequestField = (typeof clientRequestFields)[number]

export class ClientRequestError extends Error {
  constructor(
    readonly failure: ClientRequestFailure,
    readonly field?: ClientRequestField,
  ) {
    super('The client request failed.')
  }
}

const knownClientRequestFields = new Set<string>(clientRequestFields)

function isClientRequestField(value: string): value is ClientRequestField {
  return knownClientRequestFields.has(value)
}

// Only the ProblemDetails "field" extension (a parameter name) is read; the
// server text itself is never surfaced to the user.
async function readInvalidField(
  response: Response,
): Promise<ClientRequestField | undefined> {
  try {
    const body: unknown = await response.json()

    if (typeof body !== 'object' || body === null) {
      return undefined
    }

    const field = (body as Record<string, unknown>).field

    return typeof field === 'string' && isClientRequestField(field)
      ? field
      : undefined
  } catch {
    return undefined
  }
}

async function throwForWriteStatus(response: Response): Promise<never> {
  if (response.status === 400) {
    clearCsrfToken()
    throw new ClientRequestError('bad-request', await readInvalidField(response))
  }

  if (response.status === 409) {
    throw new ClientRequestError('duplicate-document')
  }

  throwForStatus(response.status)
}

function toCreateClientBody(
  request: CreateClientRequest,
): Record<string, string | null> {
  // An individual without the new fields keeps the original request shape.
  const body: Record<string, string | null> = {
    name: request.name,
    email: request.email,
    phone: request.phone,
    cpf: request.cpf,
  }

  if (request.personType === 'company') {
    body.personType = request.personType
  }

  if (request.cnpj !== null) {
    body.cnpj = request.cnpj
  }

  if (request.address !== null) {
    body.address = request.address
  }

  if (request.notes !== null) {
    body.notes = request.notes
  }

  return body
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

function isNullableString(value: unknown): value is string | null {
  return value === null || typeof value === 'string'
}

const knownClientPersonTypes = new Set<unknown>(clientPersonTypes)

function isClientPersonType(value: unknown): value is ClientPersonType {
  return knownClientPersonTypes.has(value)
}

function parseClientDetail(value: unknown): ClientDetail | undefined {
  const client = parseClient(value)

  if (!client || typeof value !== 'object' || value === null) {
    return undefined
  }

  const candidate = value as Record<string, unknown>

  if (
    !Object.prototype.hasOwnProperty.call(candidate, 'email') ||
    !Object.prototype.hasOwnProperty.call(candidate, 'phone') ||
    !Object.prototype.hasOwnProperty.call(candidate, 'cpf') ||
    !Object.prototype.hasOwnProperty.call(candidate, 'cnpj') ||
    !Object.prototype.hasOwnProperty.call(candidate, 'address') ||
    !Object.prototype.hasOwnProperty.call(candidate, 'notes') ||
    !isNullableString(candidate.email) ||
    !isNullableString(candidate.phone) ||
    !isNullableString(candidate.cpf) ||
    !isClientPersonType(candidate.personType) ||
    !isNullableString(candidate.cnpj) ||
    !isNullableString(candidate.address) ||
    !isNullableString(candidate.notes)
  ) {
    return undefined
  }

  return {
    ...client,
    email: candidate.email,
    phone: candidate.phone,
    cpf: candidate.cpf,
    personType: candidate.personType,
    cnpj: candidate.cnpj,
    address: candidate.address,
    notes: candidate.notes,
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

  if (status === 404) {
    throw new ClientRequestError('not-found')
  }

  throw new ClientRequestError('unexpected')
}

function getClientsEndpoint(organizationId: string): string {
  return `/api/organizations/${encodeURIComponent(organizationId)}/clients`
}

function getClientEndpoint(organizationId: string, clientId: string): string {
  return `${getClientsEndpoint(organizationId)}/${encodeURIComponent(clientId)}`
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
  request: CreateClientRequest,
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<CreateClientResponse> {
  const requestToken = await getCsrfToken()

  const response = await fetchWithSession(
    getClientsEndpoint(organizationId),
    {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'X-CSRF-TOKEN': requestToken,
      },
      body: JSON.stringify(toCreateClientBody(request)),
      cache: 'no-store',
      signal,
    },
    onUnauthorized,
  )

  if (response.status !== 201) {
    await throwForWriteStatus(response)
  }

  return parseCreateClientResponse(await response.json())
}

export async function getClient(
  organizationId: string,
  clientId: string,
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<ClientDetail> {
  const response = await fetchWithSession(
    getClientEndpoint(organizationId, clientId),
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

  const client = parseClientDetail(await response.json())

  if (!client || client.id.toLowerCase() !== clientId.toLowerCase()) {
    throw new ClientRequestError('unexpected')
  }

  return client
}

async function mutateClient(
  organizationId: string,
  clientId: string,
  path: string,
  method: 'POST' | 'PUT',
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
  body?: UpdateClientRequest,
): Promise<void> {
  const requestToken = await getCsrfToken()

  const response = await fetchWithSession(
    `${getClientEndpoint(organizationId, clientId)}${path}`,
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
    if (body) {
      await throwForWriteStatus(response)
    }

    if (response.status === 400) {
      clearCsrfToken()
    }

    throwForStatus(response.status)
  }
}

export function updateClient(
  organizationId: string,
  clientId: string,
  request: UpdateClientRequest,
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<void> {
  return mutateClient(
    organizationId,
    clientId,
    '',
    'PUT',
    onUnauthorized,
    signal,
    request,
  )
}

export function deactivateClient(
  organizationId: string,
  clientId: string,
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<void> {
  return mutateClient(
    organizationId,
    clientId,
    '/deactivate',
    'POST',
    onUnauthorized,
    signal,
  )
}

export function reactivateClient(
  organizationId: string,
  clientId: string,
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<void> {
  return mutateClient(
    organizationId,
    clientId,
    '/reactivate',
    'POST',
    onUnauthorized,
    signal,
  )
}
