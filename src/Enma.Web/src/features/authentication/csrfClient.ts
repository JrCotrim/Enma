const csrfEndpoint = '/api/auth/csrf'

let cachedToken: string | undefined
let inFlightRequest: Promise<string> | undefined
let generation = 0

function readRequestToken(value: unknown): string {
  if (
    typeof value !== 'object' ||
    value === null ||
    !('requestToken' in value) ||
    typeof value.requestToken !== 'string' ||
    value.requestToken.length === 0
  ) {
    throw new Error('The CSRF response was invalid.')
  }

  return value.requestToken
}

export function clearCsrfToken() {
  generation += 1
  cachedToken = undefined
  inFlightRequest = undefined
}

export function getCsrfToken(): Promise<string> {
  if (cachedToken) {
    return Promise.resolve(cachedToken)
  }

  if (inFlightRequest) {
    return inFlightRequest
  }

  const requestGeneration = generation
  const tokenRequest = fetch(csrfEndpoint, {
    method: 'GET',
    credentials: 'same-origin',
    cache: 'no-store',
  })
    .then(async (response) => {
      if (!response.ok) {
        throw new Error('The CSRF request failed.')
      }

      return readRequestToken(await response.json())
    })
    .then((token) => {
      if (generation === requestGeneration) {
        cachedToken = token
      }

      return token
    })

  const trackedRequest = tokenRequest.finally(() => {
    if (inFlightRequest === trackedRequest) {
      inFlightRequest = undefined
    }
  })

  inFlightRequest = trackedRequest
  return trackedRequest
}
