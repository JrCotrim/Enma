import { clearCsrfToken, getCsrfToken } from '../authentication/csrfClient'
import {
  fetchWithSession,
  type UnauthorizedHandler,
} from '../authentication/sessionClient'
import type { InvitationRole } from './invitationTypes'

export interface InvitationRecipientPreview {
  readonly organizationName: string
  readonly role: InvitationRole
  readonly invitedEmail: string
}

export type InvitationRecipientPreviewResult =
  | { readonly status: 'usable'; readonly preview: InvitationRecipientPreview }
  | { readonly status: 'expired' }
  | { readonly status: 'invalid' }

export type InvitationRecipientFailure =
  | 'invalid'
  | 'unauthorized'
  | 'rate-limited'
  | 'unexpected'

export class InvitationRecipientRequestError extends Error {
  constructor(readonly failure: InvitationRecipientFailure) {
    super('The invitation recipient request failed.')
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null
}

function isInvitationRole(value: unknown): value is InvitationRole {
  return value === 'Administrator' || value === 'Member'
}

function isMaskedEmail(value: unknown): value is string {
  return typeof value === 'string' && /^[^@\s]\*{3}@[^@\s]+$/.test(value)
}

function parsePreview(value: unknown): InvitationRecipientPreviewResult {
  if (!isRecord(value)) {
    throw new InvitationRecipientRequestError('unexpected')
  }

  if (value.status === 'invalid' || value.status === 'expired') {
    return { status: value.status }
  }

  if (
    value.status !== 'usable' ||
    typeof value.organizationName !== 'string' ||
    value.organizationName.trim().length === 0 ||
    !isInvitationRole(value.role) ||
    !isMaskedEmail(value.invitedEmail)
  ) {
    throw new InvitationRecipientRequestError('unexpected')
  }

  return {
    status: 'usable',
    preview: {
      organizationName: value.organizationName,
      role: value.role,
      invitedEmail: value.invitedEmail,
    },
  }
}

export async function previewInvitationRecipient(
  token: string,
  signal?: AbortSignal,
): Promise<InvitationRecipientPreviewResult> {
  let response: Response

  try {
    response = await fetch('/api/invitations/preview', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ token }),
      cache: 'no-store',
      signal,
    })
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') {
      throw error
    }

    throw new InvitationRecipientRequestError('unexpected')
  }

  if (response.status === 429) {
    throw new InvitationRecipientRequestError('rate-limited')
  }

  if (response.status !== 200) {
    throw new InvitationRecipientRequestError(
      response.status === 400 ? 'invalid' : 'unexpected',
    )
  }

  return parsePreview(await response.json())
}

export async function acceptInvitationRecipient(
  token: string,
  onUnauthorized: UnauthorizedHandler,
): Promise<void> {
  const csrfToken = await getCsrfToken()
  const response = await fetchWithSession(
    '/api/invitations/accept',
    {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'X-CSRF-TOKEN': csrfToken,
      },
      body: JSON.stringify({ token }),
      cache: 'no-store',
    },
    onUnauthorized,
  )

  if (response.status === 204) {
    return
  }

  if (response.status === 400) {
    clearCsrfToken()
    throw new InvitationRecipientRequestError('invalid')
  }

  if (response.status === 401) {
    throw new InvitationRecipientRequestError('unauthorized')
  }

  if (response.status === 429) {
    throw new InvitationRecipientRequestError('rate-limited')
  }

  throw new InvitationRecipientRequestError('unexpected')
}
