export type EmailVerificationState =
  | 'verifying'
  | 'verified'
  | 'invalid'
  | 'rateLimited'
  | 'temporaryFailure'

export interface EmailVerificationFlow {
  readonly initialState: EmailVerificationState
  readonly completion?: Promise<EmailVerificationState>
}

export type EmailVerificationRequestResult =
  | 'accepted'
  | 'invalid'
  | 'rateLimited'
  | 'temporaryFailure'

export async function requestEmailVerification(
  email: string,
  signal?: AbortSignal,
): Promise<EmailVerificationRequestResult> {
  try {
    const response = await fetch('/api/auth/email-verification/resend', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ email }),
      credentials: 'same-origin',
      cache: 'no-store',
      signal,
    })

    if (response.status === 202) return 'accepted'
    if (response.status === 400) return 'invalid'
    if (response.status === 429) return 'rateLimited'
    return 'temporaryFailure'
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') {
      throw error
    }

    return 'temporaryFailure'
  }
}

async function sendVerificationRequest(
  body: string,
): Promise<EmailVerificationState> {
  try {
    const response = await fetch('/api/auth/email-verification/verify', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
      },
      body,
      cache: 'no-store',
    })

    if (response.status === 204) {
      return 'verified'
    }

    if (response.status === 400) {
      return 'invalid'
    }

    if (response.status === 429) {
      return 'rateLimited'
    }

    return 'temporaryFailure'
  } catch {
    return 'temporaryFailure'
  }
}

export function createEmailVerificationFlow(
  token: string | undefined,
): EmailVerificationFlow {
  if (!token) {
    return { initialState: 'invalid' }
  }

  const body = JSON.stringify({ token })

  return {
    initialState: 'verifying',
    completion: sendVerificationRequest(body),
  }
}
