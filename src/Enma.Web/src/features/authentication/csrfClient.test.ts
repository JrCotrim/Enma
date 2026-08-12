import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { clearCsrfToken, getCsrfToken } from './csrfClient'
import { fetchWithSession } from './sessionClient'

function response(status: number, body?: unknown): Response {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
  })
}

beforeEach(() => {
  clearCsrfToken()
  window.localStorage.clear()
  window.sessionStorage.clear()
})

afterEach(() => {
  clearCsrfToken()
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

describe('CSRF client', () => {
  it('GetCsrfToken_ConcurrentCallsUseOneTransientRequest', async () => {
    const localStorageSpy = vi.spyOn(window.localStorage, 'setItem')
    const sessionStorageSpy = vi.spyOn(window.sessionStorage, 'setItem')
    const fetchMock = vi.fn(() =>
      Promise.resolve(response(200, { requestToken: 'transient-token' })),
    )
    vi.stubGlobal('fetch', fetchMock)

    const firstRequest = getCsrfToken()
    const secondRequest = getCsrfToken()

    await expect(firstRequest).resolves.toBe('transient-token')
    await expect(secondRequest).resolves.toBe('transient-token')
    expect(fetchMock).toHaveBeenCalledTimes(1)
    expect(localStorageSpy).not.toHaveBeenCalled()
    expect(sessionStorageSpy).not.toHaveBeenCalled()
  })

  it('ClearCsrfToken_AfterCachedTokenFetchesAgain', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(response(200, { requestToken: 'first-token' }))
      .mockResolvedValueOnce(response(200, { requestToken: 'second-token' }))
    vi.stubGlobal('fetch', fetchMock)

    await getCsrfToken()
    clearCsrfToken()
    await getCsrfToken()

    expect(fetchMock).toHaveBeenCalledTimes(2)
  })

  it('FetchWithSession_UnauthorizedInvalidatesSessionAndCsrfState', async () => {
    const onUnauthorized = vi.fn()
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(response(200, { requestToken: 'first-token' }))
      .mockResolvedValueOnce(response(401))
      .mockResolvedValueOnce(response(200, { requestToken: 'second-token' }))
    vi.stubGlobal('fetch', fetchMock)

    await getCsrfToken()
    await fetchWithSession('/api/protected-resource', {}, onUnauthorized)
    await getCsrfToken()

    expect(onUnauthorized).toHaveBeenCalledOnce()
    expect(fetchMock).toHaveBeenCalledTimes(3)
  })
})
