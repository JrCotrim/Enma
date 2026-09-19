import { getCsrfToken } from '../authentication/csrfClient'
import {
  fetchWithSession,
  type UnauthorizedHandler,
} from '../authentication/sessionClient'

export type RegistrationResult =
  | 'registeredEmailSent'
  | 'registeredEmailDeliveryFailed'
  | 'invalid'
  | 'invalidInvitation'
  | 'wrongInvitationRecipient'
  | 'existingAccount'
  | 'slugConflict'
  | 'compromisedPassword'
  | 'conflict'
  | 'unavailable'
  | 'failure'

export interface RegistrationInput {
  readonly organizationName: string
  readonly organizationSlug: string
  readonly ownerName: string
  readonly ownerEmail: string
  readonly password: string
}

interface RegistrationResponse {
  readonly verificationEmailSent?: boolean
}

export interface InvitedRegistrationInput {
  readonly name: string
  readonly email: string
  readonly password: string
}

export async function registerOrganizationOwner(
  input: RegistrationInput,
  signal?: AbortSignal,
): Promise<RegistrationResult> {
  try {
    const response = await fetch('/api/onboarding/register', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(input),
      credentials: 'same-origin',
      cache: 'no-store',
      signal,
    })

    switch (response.status) {
      case 201: {
        const registration = (await response.json()) as RegistrationResponse
        return registration.verificationEmailSent === false
          ? 'registeredEmailDeliveryFailed'
          : 'registeredEmailSent'
      }
      case 400:
        return (await readProblemCode(response)) === 'password_compromised'
          ? 'compromisedPassword'
          : 'invalid'
      case 409:
        return (await readProblemCode(response)) === 'organization_slug_conflict'
          ? 'slugConflict'
          : 'conflict'
      case 503:
        return 'unavailable'
      default:
        return 'failure'
    }
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') {
      throw error
    }

    return 'failure'
  }
}

export async function registerInvitedUser(
  invitationToken: string,
  input: InvitedRegistrationInput,
  signal?: AbortSignal,
): Promise<RegistrationResult> {
  try {
    const response = await fetch('/api/onboarding/register-invited', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ invitationToken, ...input }),
      credentials: 'same-origin',
      cache: 'no-store',
      signal,
    })

    switch (response.status) {
      case 201: {
        const registration = (await response.json()) as RegistrationResponse
        return registration.verificationEmailSent === false
          ? 'registeredEmailDeliveryFailed'
          : 'registeredEmailSent'
      }
      case 400: {
        const code = await readProblemCode(response)
        if (code === 'invited_registration_invalid') return 'invalidInvitation'
        if (code === 'invited_registration_wrong_recipient') {
          return 'wrongInvitationRecipient'
        }
        if (code === 'password_compromised') return 'compromisedPassword'
        return 'invalid'
      }
      case 409:
        return 'existingAccount'
      case 503:
        return 'unavailable'
      default:
        return 'failure'
    }
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') {
      throw error
    }

    return 'failure'
  }
}

async function readProblemCode(response: Response): Promise<string | undefined> {
  try {
    const problem = (await response.json()) as { code?: unknown }
    return typeof problem.code === 'string' ? problem.code : undefined
  } catch {
    return undefined
  }
}

export type InitialOrganizationResult =
  | { readonly status: 'created'; readonly organizationId: string }
  | {
      readonly status:
        | 'invalid'
        | 'slugConflict'
        | 'conflict'
        | 'ineligible'
        | 'failure'
    }

export async function createInitialOrganization(
  organizationName: string,
  organizationSlug: string,
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<InitialOrganizationResult> {
  try {
    const csrfToken = await getCsrfToken()
    const response = await fetchWithSession(
      '/api/onboarding/initial-organization',
      {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'X-CSRF-TOKEN': csrfToken,
        },
        body: JSON.stringify({ organizationName, organizationSlug }),
        cache: 'no-store',
        signal,
      },
      onUnauthorized,
    )

    if (response.status === 201) {
      const body = (await response.json()) as { organizationId?: unknown }
      return typeof body.organizationId === 'string'
        ? { status: 'created', organizationId: body.organizationId }
        : { status: 'failure' }
    }
    if (response.status === 400) return { status: 'invalid' }
    if (response.status === 409) {
      return {
        status:
          (await readProblemCode(response)) === 'organization_slug_conflict'
            ? 'slugConflict'
            : 'conflict',
      }
    }
    if (response.status === 403) return { status: 'ineligible' }
    return { status: 'failure' }
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') throw error
    return { status: 'failure' }
  }
}
export type GoogleProfileCompletionResult =
  | 'authenticated'
  | 'invalid'
  | 'linkRequired'
  | 'expired'
  | 'invalidInvitation'
  | 'wrongInvitation'
  | 'failure'

export async function completeGoogleProfile(
  name: string,
  signal?: AbortSignal,
): Promise<GoogleProfileCompletionResult> {
  try {
    const csrfToken = await getCsrfToken()
    const response = await fetch('/api/auth/google/complete-profile', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'X-CSRF-TOKEN': csrfToken,
      },
      body: JSON.stringify({ name }),
      credentials: 'same-origin',
      cache: 'no-store',
      signal,
    })

    if (response.status === 204) return 'authenticated'
    if (response.status === 401) return 'expired'
    if (response.status === 409) return 'linkRequired'
    if (response.status === 410) return 'invalidInvitation'
    if (response.status === 403) return 'wrongInvitation'
    if (response.status === 422) return 'invalid'
    return 'failure'
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') throw error
    return 'failure'
  }
}
