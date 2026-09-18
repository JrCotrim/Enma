export type RegistrationResult =
  | 'registeredEmailSent'
  | 'registeredEmailDeliveryFailed'
  | 'invalid'
  | 'invalidInvitation'
  | 'wrongInvitationRecipient'
  | 'existingAccount'
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
        return 'invalid'
      case 409:
        return 'conflict'
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
