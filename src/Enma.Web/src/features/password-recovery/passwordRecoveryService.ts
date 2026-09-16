export type PasswordRecoveryRequestResult =
  | 'accepted'
  | 'rateLimited'
  | 'failure'

export type PasswordResetResult =
  | 'reset'
  | 'invalid'
  | 'invalidPassword'
  | 'currentPasswordReuse'
  | 'compromisedPassword'
  | 'screeningUnavailable'
  | 'rateLimited'
  | 'failure'

export async function requestPasswordRecovery(
  email: string,
  signal?: AbortSignal,
): Promise<PasswordRecoveryRequestResult> {
  try {
    const response = await fetch('/api/auth/password-recovery/request', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ email }),
      cache: 'no-store',
      signal,
    })

    if (response.status === 202) return 'accepted'
    if (response.status === 429) return 'rateLimited'
    return 'failure'
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') throw error
    return 'failure'
  }
}

export async function resetPassword(
  token: string,
  newPassword: string,
  signal?: AbortSignal,
): Promise<PasswordResetResult> {
  try {
    const response = await fetch('/api/auth/password-recovery/reset', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ token, newPassword }),
      cache: 'no-store',
      signal,
    })

    if (response.status === 204) return 'reset'
    if (response.status === 429) return 'rateLimited'
    if (response.status === 503) return 'screeningUnavailable'

    if (response.status === 400) {
      const code = await readProblemCode(response)
      if (code === 'password_invalid') return 'invalidPassword'
      if (code === 'password_current_reuse') return 'currentPasswordReuse'
      if (code === 'password_compromised') return 'compromisedPassword'
      return 'invalid'
    }

    return 'failure'
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') throw error
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
