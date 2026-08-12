import { clearCsrfToken, getCsrfToken } from './csrfClient'
import {
  fetchWithSession,
  type UnauthorizedHandler,
} from './sessionClient'

export type LoginResult = 'authenticated' | 'invalidCredentials' | 'failure'

export async function checkSession(
  signal: AbortSignal,
  onUnauthorized: UnauthorizedHandler,
): Promise<boolean> {
  const response = await fetchWithSession(
    '/api/me/organizations',
    {
      method: 'GET',
      cache: 'no-store',
      signal,
    },
    onUnauthorized,
  )

  if (response.status === 200) {
    return true
  }

  if (response.status === 401) {
    return false
  }

  throw new Error('The session check failed.')
}

export async function login(
  email: string,
  password: string,
  signal?: AbortSignal,
): Promise<LoginResult> {
  try {
    const response = await fetch('/api/auth/login', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify({ email, password }),
      credentials: 'same-origin',
      cache: 'no-store',
      signal,
    })

    if (response.status === 204) {
      clearCsrfToken()
      return 'authenticated'
    }

    if (response.status === 401) {
      return 'invalidCredentials'
    }

    return 'failure'
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') {
      throw error
    }

    return 'failure'
  }
}

export async function logout(
  onUnauthorized: UnauthorizedHandler,
): Promise<void> {
  const csrfToken = await getCsrfToken()
  const response = await fetchWithSession(
    '/api/auth/logout',
    {
      method: 'POST',
      headers: {
        'X-CSRF-TOKEN': csrfToken,
      },
      cache: 'no-store',
    },
    onUnauthorized,
  )

  if (response.status !== 204 && response.status !== 401) {
    clearCsrfToken()
    throw new Error('The logout request failed.')
  }

  clearCsrfToken()
}
