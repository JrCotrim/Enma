import { clearCsrfToken, getCsrfToken } from '../authentication/csrfClient'
import {
  fetchWithSession,
  type UnauthorizedHandler,
} from '../authentication/sessionClient'
import { isValidDateOnly, isValidGuid } from '../deadlines/legalDeadlineFormatting'
import type {
  CreateLegalTaskRequest,
  CreateLegalTaskResponse,
  ChangeLegalTaskAssigneeRequest,
  LegalTaskDetail,
  LegalTaskListItem,
  LegalTaskListQuery,
  LegalTaskListResponse,
  UpdateLegalTaskRequest,
} from './legalTaskTypes'

export type LegalTaskRequestFailure =
  | 'unauthorized'
  | 'forbidden'
  | 'not-found'
  | 'bad-request'
  | 'conflict'
  | 'related-process-unavailable'
  | 'related-assignee-unavailable'
  | 'unexpected'

export class LegalTaskRequestError extends Error {
  constructor(readonly failure: LegalTaskRequestFailure) {
    super('The legal task request failed.')
  }
}

function isNullableString(value: unknown): value is string | null {
  return value === null || typeof value === 'string'
}

function isNullableGuid(value: unknown): value is string | null {
  return value === null || (typeof value === 'string' && isValidGuid(value))
}

function isValidTimestamp(value: unknown): value is string {
  return (
    typeof value === 'string' &&
    value.length > 0 &&
    !Number.isNaN(new Date(value).getTime())
  )
}

function parseLegalTaskListItem(value: unknown): LegalTaskListItem | undefined {
  if (typeof value !== 'object' || value === null) {
    return undefined
  }

  const candidate = value as Record<string, unknown>
  const hasConsistentProcess =
    (candidate.processId === null &&
      candidate.processTitle === null &&
      candidate.clientName === null) ||
    (typeof candidate.processId === 'string' &&
      isValidGuid(candidate.processId) &&
      typeof candidate.processTitle === 'string' &&
      candidate.processTitle.length > 0 &&
      typeof candidate.clientName === 'string' &&
      candidate.clientName.length > 0)
  const hasConsistentAssignee =
    (candidate.assigneeMembershipId === null &&
      candidate.assigneeDisplayName === null) ||
    (typeof candidate.assigneeMembershipId === 'string' &&
      isValidGuid(candidate.assigneeMembershipId) &&
      typeof candidate.assigneeDisplayName === 'string' &&
      candidate.assigneeDisplayName.length > 0)

  if (
    typeof candidate.id !== 'string' ||
    !isValidGuid(candidate.id) ||
    typeof candidate.title !== 'string' ||
    candidate.title.length === 0 ||
    !isNullableString(candidate.dueDate) ||
    (candidate.dueDate !== null && !isValidDateOnly(candidate.dueDate)) ||
    !isNullableGuid(candidate.processId) ||
    !isNullableString(candidate.processTitle) ||
    !isNullableString(candidate.clientName) ||
    !hasConsistentProcess ||
    !isNullableGuid(candidate.assigneeMembershipId) ||
    !isNullableString(candidate.assigneeDisplayName) ||
    !hasConsistentAssignee ||
    typeof candidate.createdByMembershipId !== 'string' ||
    !isValidGuid(candidate.createdByMembershipId) ||
    (candidate.state !== 'pending' && candidate.state !== 'completed') ||
    !isValidTimestamp(candidate.createdAt)
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
    assigneeMembershipId: candidate.assigneeMembershipId,
    assigneeDisplayName: candidate.assigneeDisplayName,
    createdByMembershipId: candidate.createdByMembershipId,
    state: candidate.state,
    createdAt: candidate.createdAt,
  }
}

function parseLegalTaskListResponse(value: unknown): LegalTaskListResponse {
  if (typeof value !== 'object' || value === null) {
    throw new LegalTaskRequestError('unexpected')
  }

  const candidate = value as Record<string, unknown>
  const items = Array.isArray(candidate.items)
    ? candidate.items.map(parseLegalTaskListItem)
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
    items: items as LegalTaskListItem[],
    pageNumber: candidate.pageNumber,
    pageSize: candidate.pageSize,
    hasNext: candidate.hasNext,
  }
}

function parseLegalTaskDetail(value: unknown): LegalTaskDetail {
  if (typeof value !== 'object' || value === null) {
    throw new LegalTaskRequestError('unexpected')
  }

  const candidate = value as Record<string, unknown>
  const item = parseLegalTaskListItem(candidate)
  const completedAtIsValid =
    (candidate.state === 'pending' && candidate.completedAt === null) ||
    (candidate.state === 'completed' && isValidTimestamp(candidate.completedAt))

  if (
    !item ||
    !isNullableString(candidate.description) ||
    typeof candidate.createdByDisplayName !== 'string' ||
    candidate.createdByDisplayName.length === 0 ||
    !completedAtIsValid
  ) {
    throw new LegalTaskRequestError('unexpected')
  }

  return {
    id: item.id,
    title: item.title,
    description: candidate.description,
    dueDate: item.dueDate,
    processId: item.processId,
    processTitle: item.processTitle,
    clientName: item.clientName,
    assigneeMembershipId: item.assigneeMembershipId,
    assigneeDisplayName: item.assigneeDisplayName,
    createdByMembershipId: item.createdByMembershipId,
    createdByDisplayName: candidate.createdByDisplayName,
    state: item.state,
    createdAt: item.createdAt,
    completedAt: candidate.completedAt as string | null,
  }
}

function parseCreateResponse(value: unknown): CreateLegalTaskResponse {
  if (typeof value !== 'object' || value === null) {
    throw new LegalTaskRequestError('unexpected')
  }

  const id = (value as Record<string, unknown>).id
  if (typeof id !== 'string' || !isValidGuid(id)) {
    throw new LegalTaskRequestError('unexpected')
  }

  return { id }
}

function throwForStatus(status: number): never {
  if (status === 401) throw new LegalTaskRequestError('unauthorized')
  if (status === 403) throw new LegalTaskRequestError('forbidden')
  if (status === 404) throw new LegalTaskRequestError('not-found')
  if (status === 400) throw new LegalTaskRequestError('bad-request')
  if (status === 409) throw new LegalTaskRequestError('conflict')
  throw new LegalTaskRequestError('unexpected')
}

function getTaskEndpoint(organizationId: string, taskId: string): string {
  return `${getTasksEndpoint(organizationId)}/${encodeURIComponent(taskId)}`
}

async function sendLegalTaskMutation(
  endpoint: string,
  method: 'PUT' | 'POST',
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
  body?: UpdateLegalTaskRequest | ChangeLegalTaskAssigneeRequest,
  notFoundFailure: LegalTaskRequestFailure = 'not-found',
): Promise<void> {
  const requestToken = await getCsrfToken()
  const response = await fetchWithSession(
    endpoint,
    {
      method,
      headers: {
        ...(body ? { 'Content-Type': 'application/json' } : {}),
        'X-CSRF-TOKEN': requestToken,
      },
      ...(body ? { body: JSON.stringify(body) } : {}),
      cache: 'no-store',
      signal,
    },
    onUnauthorized,
  )

  if (response.status === 204) return

  if (response.status === 400) {
    clearCsrfToken()
    try {
      const problem = (await response.json()) as Record<string, unknown>
      if (problem.title === 'Related assignee unavailable') {
        throw new LegalTaskRequestError('related-assignee-unavailable')
      }
    } catch (error) {
      if (error instanceof LegalTaskRequestError) throw error
    }
  }

  if (response.status === 404) {
    throw new LegalTaskRequestError(notFoundFailure)
  }
  throwForStatus(response.status)
}

function getTasksEndpoint(organizationId: string): string {
  return `/api/organizations/${encodeURIComponent(organizationId)}/tasks`
}

export async function listLegalTasks(
  organizationId: string,
  query: LegalTaskListQuery,
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<LegalTaskListResponse> {
  const search = new URLSearchParams({
    state: query.state,
    assignee: query.assignee,
    pageNumber: query.pageNumber.toString(),
    pageSize: query.pageSize.toString(),
  })
  if (query.processId) search.set('processId', query.processId)

  const response = await fetchWithSession(
    `${getTasksEndpoint(organizationId)}?${search.toString()}`,
    { method: 'GET', cache: 'no-store', signal },
    onUnauthorized,
  )
  if (response.status !== 200) throwForStatus(response.status)

  const result = parseLegalTaskListResponse(await response.json())
  if (
    result.pageNumber !== query.pageNumber ||
    result.pageSize !== query.pageSize
  ) {
    throw new LegalTaskRequestError('unexpected')
  }
  return result
}

export async function createLegalTask(
  organizationId: string,
  body: CreateLegalTaskRequest,
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<CreateLegalTaskResponse> {
  const requestToken = await getCsrfToken()
  const response = await fetchWithSession(
    getTasksEndpoint(organizationId),
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
      try {
        const problem = (await response.json()) as Record<string, unknown>
        if (problem.title === 'Related assignee unavailable') {
          throw new LegalTaskRequestError('related-assignee-unavailable')
        }
      } catch (error) {
        if (error instanceof LegalTaskRequestError) throw error
      }
    }
    throwForStatus(response.status)
  }

  return parseCreateResponse(await response.json())
}

export async function getLegalTask(
  organizationId: string,
  taskId: string,
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<LegalTaskDetail> {
  const response = await fetchWithSession(
    getTaskEndpoint(organizationId, taskId),
    { method: 'GET', cache: 'no-store', signal },
    onUnauthorized,
  )
  if (response.status !== 200) throwForStatus(response.status)
  return parseLegalTaskDetail(await response.json())
}

export function updateLegalTask(
  organizationId: string,
  taskId: string,
  body: UpdateLegalTaskRequest,
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<void> {
  return sendLegalTaskMutation(
    getTaskEndpoint(organizationId, taskId),
    'PUT',
    onUnauthorized,
    signal,
    body,
    'related-process-unavailable',
  )
}

export function changeLegalTaskAssignee(
  organizationId: string,
  taskId: string,
  assigneeMembershipId: string | null,
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<void> {
  return sendLegalTaskMutation(
    `${getTaskEndpoint(organizationId, taskId)}/assignee`,
    'PUT',
    onUnauthorized,
    signal,
    { assigneeMembershipId },
  )
}

export function completeLegalTask(
  organizationId: string,
  taskId: string,
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<void> {
  return sendLegalTaskMutation(
    `${getTaskEndpoint(organizationId, taskId)}/complete`,
    'POST',
    onUnauthorized,
    signal,
  )
}

export function reopenLegalTask(
  organizationId: string,
  taskId: string,
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<void> {
  return sendLegalTaskMutation(
    `${getTaskEndpoint(organizationId, taskId)}/reopen`,
    'POST',
    onUnauthorized,
    signal,
  )
}
