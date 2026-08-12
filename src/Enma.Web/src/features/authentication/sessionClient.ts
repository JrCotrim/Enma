import { clearCsrfToken } from './csrfClient'

export type UnauthorizedHandler = () => void

export async function fetchWithSession(
  input: RequestInfo | URL,
  init: RequestInit = {},
  onUnauthorized?: UnauthorizedHandler,
): Promise<Response> {
  const response = await fetch(input, {
    ...init,
    credentials: 'same-origin',
  })

  if (response.status === 401) {
    clearCsrfToken()
    onUnauthorized?.()
  }

  return response
}
