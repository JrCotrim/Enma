import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { clearCsrfToken } from '../authentication/csrfClient'
import { TeamRequestError, transferOwnership } from './teamService'

const organizationId = '11111111-1111-4111-8111-111111111111'
const membershipId = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbb2'
const transferEndpoint = `/api/organizations/${organizationId}/members/${membershipId}/transfer-ownership`

function response(status: number, body?: unknown): Response {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
  })
}

function stubTransferResponse(transferResponse: Response) {
  const fetchMock = vi
    .fn()
    .mockResolvedValueOnce(response(200, { requestToken: 'csrf-token' }))
    .mockResolvedValueOnce(transferResponse)
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

async function transferFailure(): Promise<TeamRequestError> {
  try {
    await transferOwnership(organizationId, membershipId, vi.fn())
  } catch (error) {
    if (error instanceof TeamRequestError) return error
    throw error
  }

  throw new Error('The transfer was expected to fail.')
}

beforeEach(clearCsrfToken)

afterEach(() => {
  clearCsrfToken()
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

describe('Team ownership transfer API client', () => {
  it('TransferOwnership_PostsExpectedTargetRoleWithCsrfAndResolvesOn204', async () => {
    const fetchMock = stubTransferResponse(response(204))

    await expect(
      transferOwnership(organizationId, membershipId, vi.fn()),
    ).resolves.toBeUndefined()

    expect(fetchMock).toHaveBeenCalledTimes(2)
    expect(fetchMock).toHaveBeenNthCalledWith(
      2,
      transferEndpoint,
      expect.objectContaining({
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'X-CSRF-TOKEN': 'csrf-token',
        },
        body: JSON.stringify({ expectedTargetRole: 'administrator' }),
        cache: 'no-store',
      }),
    )
  })

  it('TransferOwnership_MapsForbiddenAndNotFoundByStatus', async () => {
    stubTransferResponse(response(403, { detail: 'private policy detail' }))
    expect((await transferFailure()).failure).toBe('forbidden')

    clearCsrfToken()
    stubTransferResponse(response(404))
    expect((await transferFailure()).failure).toBe('not-found')
  })

  it('TransferOwnership_MapsConflictOnlyForTheStableProblemCode', async () => {
    stubTransferResponse(
      response(409, {
        detail: 'The selected member cannot receive organization ownership.',
        code: 'ownership_transfer_target_unavailable',
      }),
    )
    expect((await transferFailure()).failure).toBe('conflict')

    clearCsrfToken()
    stubTransferResponse(
      response(409, { detail: 'ownership_transfer_target_unavailable' }),
    )
    expect((await transferFailure()).failure).toBe('unexpected')

    clearCsrfToken()
    stubTransferResponse(response(409))
    expect((await transferFailure()).failure).toBe('unexpected')
  })

  it('TransferOwnership_BadRequestClearsTheCachedCsrfToken', async () => {
    const fetchMock = stubTransferResponse(response(400))
    expect((await transferFailure()).failure).toBe('bad-request')

    fetchMock
      .mockResolvedValueOnce(response(200, { requestToken: 'fresh-token' }))
      .mockResolvedValueOnce(response(204))
    await transferOwnership(organizationId, membershipId, vi.fn())

    expect(fetchMock).toHaveBeenCalledTimes(4)
    expect(fetchMock.mock.calls[3]?.[1]?.headers).toMatchObject({
      'X-CSRF-TOKEN': 'fresh-token',
    })
  })
})
