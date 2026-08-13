import { clearCsrfToken, getCsrfToken } from '../authentication/csrfClient'
import {
  fetchWithSession,
  type UnauthorizedHandler,
} from '../authentication/sessionClient'
import type {
  CreateLegalProcessRequest,
  CreateLegalProcessResponse,
  LegalProcess,
  LegalProcessListItem,
  LegalProcessListResponse,
  UpdateLegalProcessRequest,
} from './legalProcessTypes'

export type LegalProcessRequestFailure =
  | 'unauthorized'
  | 'forbidden'
  | 'not-found'
  | 'bad-request'
  | 'unexpected'

export class LegalProcessRequestError extends Error {
  constructor(readonly failure: LegalProcessRequestFailure) {
    super('The legal process request failed.')
  }
}

function parseLegalProcess(value: unknown): LegalProcess | undefined {
  if (typeof value !== 'object' || value === null) {
    return undefined
  }

  const candidate = value as Record<string, unknown>

  if (
    typeof candidate.id !== 'string' ||
    candidate.id.length === 0 ||
    typeof candidate.title !== 'string' ||
    candidate.title.length === 0 ||
    typeof candidate.clientId !== 'string' ||
    candidate.clientId.length === 0 ||
    typeof candidate.clientName !== 'string' ||
    candidate.clientName.length === 0 ||
    typeof candidate.createdAt !== 'string' ||
    Number.isNaN(Date.parse(candidate.createdAt))
  ) {
    return undefined
  }

  return {
    id: candidate.id,
    title: candidate.title,
    clientId: candidate.clientId,
    clientName: candidate.clientName,
    createdAt: candidate.createdAt,
  }
}

function parseLegalProcessListResponse(
  value: unknown,
): LegalProcessListResponse {
  if (typeof value !== 'object' || value === null) {
    throw new LegalProcessRequestError('unexpected')
  }

  const candidate = value as Record<string, unknown>
  const items = Array.isArray(candidate.items)
    ? candidate.items.map(parseLegalProcess)
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
    throw new LegalProcessRequestError('unexpected')
  }

  return {
    items: items as LegalProcessListItem[],
    pageNumber: candidate.pageNumber,
    pageSize: candidate.pageSize,
  }
}

function parseCreateLegalProcessResponse(
  value: unknown,
): CreateLegalProcessResponse {
  if (typeof value !== 'object' || value === null) {
    throw new LegalProcessRequestError('unexpected')
  }

  const id = (value as Record<string, unknown>).id

  if (typeof id !== 'string' || id.length === 0) {
    throw new LegalProcessRequestError('unexpected')
  }

  return { id }
}

function throwForStatus(status: number): never {
  if (status === 401) {
    throw new LegalProcessRequestError('unauthorized')
  }

  if (status === 403) {
    throw new LegalProcessRequestError('forbidden')
  }

  if (status === 404) {
    throw new LegalProcessRequestError('not-found')
  }

  if (status === 400) {
    throw new LegalProcessRequestError('bad-request')
  }

  throw new LegalProcessRequestError('unexpected')
}

function getLegalProcessesEndpoint(organizationId: string): string {
  return `/api/organizations/${encodeURIComponent(organizationId)}/processes`
}

function getLegalProcessEndpoint(
  organizationId: string,
  processId: string,
): string {
  return `${getLegalProcessesEndpoint(organizationId)}/${encodeURIComponent(processId)}`
}

export async function listLegalProcesses(
  organizationId: string,
  pageNumber: number,
  pageSize: number,
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<LegalProcessListResponse> {
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
    pageNumber: pageNumber.toString(),
    pageSize: pageSize.toString(),
  })
  const response = await fetchWithSession(
    `${getLegalProcessesEndpoint(organizationId)}?${query.toString()}`,
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

  const result = parseLegalProcessListResponse(await response.json())

  if (result.pageNumber !== pageNumber || result.pageSize !== pageSize) {
    throw new LegalProcessRequestError('unexpected')
  }

  return result
}

export async function createLegalProcess(
  organizationId: string,
  clientId: string,
  title: string,
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<CreateLegalProcessResponse> {
  const requestToken = await getCsrfToken()
  const body: CreateLegalProcessRequest = { clientId, title }
  const response = await fetchWithSession(
    getLegalProcessesEndpoint(organizationId),
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

  return parseCreateLegalProcessResponse(await response.json())
}

export async function getLegalProcess(
  organizationId: string,
  processId: string,
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<LegalProcess> {
  const response = await fetchWithSession(
    getLegalProcessEndpoint(organizationId, processId),
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

  const legalProcess = parseLegalProcess(await response.json())

  if (!legalProcess || legalProcess.id.toLowerCase() !== processId.toLowerCase()) {
    throw new LegalProcessRequestError('unexpected')
  }

  return legalProcess
}

export async function updateLegalProcess(
  organizationId: string,
  processId: string,
  title: string,
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<void> {
  const requestToken = await getCsrfToken()
  const body: UpdateLegalProcessRequest = { title }
  const response = await fetchWithSession(
    getLegalProcessEndpoint(organizationId, processId),
    {
      method: 'PUT',
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

  if (response.status !== 204) {
    if (response.status === 400) {
      clearCsrfToken()
    }

    throwForStatus(response.status)
  }
}
