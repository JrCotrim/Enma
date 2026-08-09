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
