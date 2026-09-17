export type RegistrationResult =
  | 'registeredEmailSent'
  | 'registeredEmailDeliveryFailed'
  | 'invalid'
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
