import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { StrictMode } from 'react'
import { MemoryRouter, useLocation } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { AuthContext, type AuthContextValue } from '../authentication/AuthContext'
import { clearCsrfToken } from '../authentication/csrfClient'
import { NotificationCenter } from './NotificationCenter'

const organizationA = '11111111-1111-4111-8111-111111111111'
const organizationB = '22222222-2222-4222-8222-222222222222'
const notificationId = '33333333-3333-4333-8333-333333333333'
const sourceId = '44444444-4444-4444-8444-444444444444'

const authContextValue: AuthContextValue = {
  state: 'authenticated',
  login: async () => 'failure',
  logout: async () => undefined,
  retrySessionCheck: () => undefined,
  handleUnauthorized: () => undefined,
}

function response(status: number, body?: unknown): Response {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
  })
}

function notification(overrides: Record<string, unknown> = {}) {
  return {
    id: notificationId,
    kind: 'legalDeadlineDueSoon',
    sourceType: 'legalDeadline',
    sourceId,
    sourceTitle: 'Apresentar contestação',
    occurrenceDate: '2026-09-03',
    occurrenceAt: null,
    generatedAt: '2026-09-01T12:00:00Z',
    readAt: null,
    ...overrides,
  }
}

function feed(
  items: readonly ReturnType<typeof notification>[] = [],
  unreadCount = 0,
) {
  return { items, unreadCount }
}

function LocationProbe() {
  const location = useLocation()
  return <output data-testid="location">{location.pathname}</output>
}

function centerTree(organizationId: string) {
  return (
    <AuthContext.Provider value={authContextValue}>
      <MemoryRouter initialEntries={[`/organizations/${organizationId}/agenda`]}>
        <NotificationCenter key={organizationId} organizationId={organizationId} />
        <LocationProbe />
      </MemoryRouter>
    </AuthContext.Provider>
  )
}

function renderCenter(organizationId = organizationA) {
  const result = render(centerTree(organizationId))
  return {
    ...result,
    switchOrganization(nextOrganizationId: string) {
      result.rerender(centerTree(nextOrganizationId))
    },
  }
}

function openCenter() {
  fireEvent.click(screen.getByRole('button', { name: /notificações/i }))
}

async function flushPromises() {
  await act(async () => {
    await Promise.resolve()
    await Promise.resolve()
    await Promise.resolve()
  })
}

beforeEach(() => {
  clearCsrfToken()
})

afterEach(() => {
  clearCsrfToken()
  vi.useRealTimers()
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

describe('NotificationCenter', () => {
  it('Bell_ZeroUnread_HasAccessibleNameAndNoBadge', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(response(200, feed())))
    const { container } = renderCenter()

    expect(await screen.findByRole('button', { name: 'Notificações' })).toHaveAttribute(
      'aria-expanded',
      'false',
    )
    expect(container.querySelector('.notification-badge')).not.toBeInTheDocument()
  })

  it.each([
    [3, '3'],
    [132, '99+'],
  ])('Bell_UnreadCount%s_RendersBoundedBadge', async (unreadCount, badge) => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(response(200, feed([notification()], unreadCount))),
    )
    const { container } = renderCenter()

    expect(
      await screen.findByRole('button', {
        name: `Notificações, ${unreadCount} não lidas`,
      }),
    ).toBeInTheDocument()
    expect(container.querySelector('.notification-badge')).toHaveTextContent(badge)
  })

  it('Panel_LoadingThenEmpty_RendersExplicitStates', async () => {
    let resolveRequest: ((value: Response) => void) | undefined
    const request = new Promise<Response>((resolve) => {
      resolveRequest = resolve
    })
    vi.stubGlobal('fetch', vi.fn().mockReturnValue(request))
    renderCenter()

    openCenter()
    expect(screen.getByText('Carregando notificações...')).toBeVisible()

    await act(async () => {
      resolveRequest?.(response(200, feed()))
      await request
    })
    expect(await screen.findByText('Nenhuma notificação por enquanto.')).toBeVisible()
  })

  it('Panel_GetFailure_ShowsSafeErrorAndRetryRecovers', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(response(500, { detail: 'private detail' }))
      .mockResolvedValueOnce(response(200, feed()))
    vi.stubGlobal('fetch', fetchMock)
    renderCenter()

    openCenter()
    expect(
      await screen.findByText('Não foi possível carregar as notificações.'),
    ).toBeVisible()
    expect(screen.queryByText('private detail')).not.toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'Tentar novamente' }))
    expect(await screen.findByText('Nenhuma notificação por enquanto.')).toBeVisible()
    expect(fetchMock).toHaveBeenCalledTimes(2)
  })

  it('Panel_Populated_DistinguishesUnreadAndFormatsDateOnlyWithoutDateInstant', async () => {
    const readNotification = notification({
      id: '55555555-5555-4555-8555-555555555555',
      sourceId: '66666666-6666-4666-8666-666666666666',
      sourceTitle: 'Prazo já consultado',
      readAt: '2026-09-01T13:00:00Z',
    })
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        response(200, feed([notification(), readNotification], 1)),
      ),
    )
    renderCenter()
    await screen.findByRole('button', { name: 'Notificações, 1 não lidas' })

    openCenter()

    const unread = screen.getByRole('button', { name: /apresentar contestação/i })
    const read = screen.getByRole('button', { name: /prazo já consultado/i })
    expect(unread).toHaveClass('is-unread')
    expect(unread).toHaveTextContent('Não lida')
    expect(unread).toHaveTextContent('03/09/2026')
    expect(read).not.toHaveClass('is-unread')
    expect(screen.getByRole('button', { name: 'Marcar todas como lidas' })).toBeVisible()
  })

  it.each([
    ['legalDeadline', 'deadlines', sourceId],
    ['legalTask', 'tasks', sourceId],
    ['calendarEvent', 'agenda', undefined],
  ])(
    'Navigation_%s_UsesCanonicalDestination',
    async (sourceType, segment, expectedSourceId) => {
      vi.stubGlobal(
        'fetch',
        vi.fn().mockResolvedValue(
          response(
            200,
            feed([
              notification({
                sourceType,
                kind:
                  sourceType === 'legalDeadline'
                    ? 'legalDeadlineDueSoon'
                    : sourceType === 'legalTask'
                      ? 'legalTaskDueSoon'
                      : 'calendarEventStartingSoon',
                occurrenceDate: sourceType === 'calendarEvent' ? null : '2026-09-03',
                occurrenceAt:
                  sourceType === 'calendarEvent' ? '2026-09-03T15:30:00Z' : null,
                readAt: '2026-09-01T13:00:00Z',
              }),
            ]),
          ),
        ),
      )
      renderCenter()
      await screen.findByRole('button', { name: 'Notificações' })
      openCenter()

      fireEvent.click(screen.getByRole('button', { name: /apresentar contestação/i }))

      const suffix = expectedSourceId ? `/${expectedSourceId}` : ''
      expect(screen.getByTestId('location')).toHaveTextContent(
        `/organizations/${organizationA}/${segment}${suffix}`,
      )
      expect(fetch).toHaveBeenCalledTimes(1)
    },
  )

  it('MarkOne_OptimisticallyUpdatesUsesCsrfAndReconciles', async () => {
    const readItem = notification({ readAt: '2026-09-01T13:00:00Z' })
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(response(200, feed([notification()], 1)))
      .mockResolvedValueOnce(response(200, { requestToken: 'csrf-token' }))
      .mockResolvedValueOnce(response(204))
      .mockResolvedValueOnce(response(200, feed([readItem], 0)))
    vi.stubGlobal('fetch', fetchMock)
    renderCenter()
    await screen.findByRole('button', { name: 'Notificações, 1 não lidas' })
    openCenter()

    fireEvent.click(screen.getByRole('button', { name: /apresentar contestação/i }))

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(4))
    expect(fetchMock.mock.calls[2]?.[0]).toBe(
      `/api/organizations/${organizationA}/notifications/${notificationId}/read`,
    )
    expect(fetchMock.mock.calls[2]?.[1]).toMatchObject({
      method: 'PUT',
      headers: { 'X-CSRF-TOKEN': 'csrf-token' },
    })
    expect(screen.getByRole('button', { name: 'Notificações' })).toBeVisible()
  })

  it('MarkOne_FailureRollsBackRefetchesAndSurfacesError', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(response(200, feed([notification()], 1)))
      .mockResolvedValueOnce(response(200, { requestToken: 'csrf-token' }))
      .mockResolvedValueOnce(response(500))
      .mockResolvedValueOnce(response(200, feed([notification()], 1)))
    vi.stubGlobal('fetch', fetchMock)
    renderCenter()
    await screen.findByRole('button', { name: 'Notificações, 1 não lidas' })
    openCenter()

    fireEvent.click(screen.getByRole('button', { name: /apresentar contestação/i }))
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(4))
    openCenter()

    expect(
      await screen.findByText(/não foi possível marcar a notificação como lida/i),
    ).toBeVisible()
    expect(screen.getByRole('button', { name: /apresentar contestação/i })).toHaveClass(
      'is-unread',
    )
  })

  it('MarkAll_IsSingleCsrfMutationAndReconciles', async () => {
    const readItem = notification({ readAt: '2026-09-01T13:00:00Z' })
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(response(200, feed([notification()], 1)))
      .mockResolvedValueOnce(response(200, { requestToken: 'csrf-token' }))
      .mockResolvedValueOnce(response(204))
      .mockResolvedValueOnce(response(200, feed([readItem], 0)))
    vi.stubGlobal('fetch', fetchMock)
    renderCenter()
    await screen.findByRole('button', { name: 'Notificações, 1 não lidas' })
    openCenter()

    fireEvent.click(screen.getByRole('button', { name: 'Marcar todas como lidas' }))

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(4))
    expect(fetchMock.mock.calls[2]?.[0]).toBe(
      `/api/organizations/${organizationA}/notifications/read-all`,
    )
    expect(screen.queryByRole('button', { name: 'Marcar todas como lidas' })).not.toBeInTheDocument()
  })

  it('MutationStart_PreexistingGetCannotOverwriteOptimismWhilePutPending', async () => {
    let resolveOldGet: ((value: Response) => void) | undefined
    const oldGet = new Promise<Response>((resolve) => {
      resolveOldGet = resolve
    })
    let resolvePut: ((value: Response) => void) | undefined
    const pendingPut = new Promise<Response>((resolve) => {
      resolvePut = resolve
    })
    let resolveAuthoritativeGet: ((value: Response) => void) | undefined
    const authoritativeGet = new Promise<Response>((resolve) => {
      resolveAuthoritativeGet = resolve
    })
    const readItem = notification({
      sourceTitle: 'Estado autoritativo',
      readAt: '2026-09-01T13:00:00Z',
    })
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(response(200, feed([notification()], 1)))
      .mockReturnValueOnce(oldGet)
      .mockResolvedValueOnce(response(200, { requestToken: 'csrf-token' }))
      .mockReturnValueOnce(pendingPut)
      .mockReturnValueOnce(authoritativeGet)
    vi.stubGlobal('fetch', fetchMock)
    renderCenter()
    await screen.findByRole('button', { name: 'Notificações, 1 não lidas' })
    openCenter()
    window.dispatchEvent(new Event('focus'))
    fireEvent.click(screen.getByRole('button', { name: 'Marcar todas como lidas' }))

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(4))
    expect(screen.getByRole('button', { name: 'Notificações' })).toBeVisible()

    await act(async () => {
      resolveOldGet?.(response(200, feed([notification()], 1)))
      await oldGet
    })
    expect(screen.getByRole('button', { name: 'Notificações' })).toBeVisible()
    expect(screen.getByRole('button', { name: /apresentar contestação/i })).not.toHaveClass(
      'is-unread',
    )

    await act(async () => {
      resolvePut?.(response(204))
      await pendingPut
    })
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(5))

    await act(async () => {
      resolveAuthoritativeGet?.(response(200, feed([readItem], 0)))
      await authoritativeGet
    })
    expect(await screen.findByText('Estado autoritativo')).toBeVisible()
    expect(screen.getByRole('button', { name: 'Notificações' })).toBeVisible()
  })

  it('Mutation_ReplacesPreexistingGetWithPostMutationReconciliation', async () => {
    let resolveOldGet: ((value: Response) => void) | undefined
    const oldGet = new Promise<Response>((resolve) => {
      resolveOldGet = resolve
    })
    const readItem = notification({ readAt: '2026-09-01T13:00:00Z' })
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(response(200, feed([notification()], 1)))
      .mockReturnValueOnce(oldGet)
      .mockResolvedValueOnce(response(200, { requestToken: 'csrf-token' }))
      .mockResolvedValueOnce(response(204))
      .mockResolvedValueOnce(response(200, feed([readItem], 0)))
    vi.stubGlobal('fetch', fetchMock)
    renderCenter()
    await screen.findByRole('button', { name: 'Notificações, 1 não lidas' })
    openCenter()
    window.dispatchEvent(new Event('focus'))
    fireEvent.click(screen.getByRole('button', { name: 'Marcar todas como lidas' }))

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(5))
    expect(screen.getByRole('button', { name: 'Notificações' })).toBeVisible()

    await act(async () => {
      resolveOldGet?.(response(200, feed([notification()], 1)))
      await oldGet
    })
    expect(screen.getByRole('button', { name: 'Notificações' })).toBeVisible()
  })

  it('MarkAll_FailureRestoresUnreadStateAndSurfacesError', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(response(200, feed([notification()], 1)))
      .mockResolvedValueOnce(response(200, { requestToken: 'csrf-token' }))
      .mockResolvedValueOnce(response(500))
      .mockResolvedValueOnce(response(200, feed([notification()], 1)))
    vi.stubGlobal('fetch', fetchMock)
    renderCenter()
    await screen.findByRole('button', { name: 'Notificações, 1 não lidas' })
    openCenter()

    fireEvent.click(screen.getByRole('button', { name: 'Marcar todas como lidas' }))

    expect(
      await screen.findByText(/não foi possível marcar todas as notificações/i),
    ).toBeVisible()
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(4))
    expect(screen.getByRole('button', { name: 'Marcar todas como lidas' })).toBeVisible()
    expect(screen.getByRole('button', { name: /apresentar contestação/i })).toHaveClass(
      'is-unread',
    )
  })

  it('MarkOne_CsrfFailureIsSurfacedAndNoPutIsSent', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(response(200, feed([notification()], 1)))
      .mockResolvedValueOnce(response(500))
      .mockResolvedValueOnce(response(200, feed([notification()], 1)))
    vi.stubGlobal('fetch', fetchMock)
    renderCenter()
    await screen.findByRole('button', { name: 'Notificações, 1 não lidas' })
    openCenter()

    fireEvent.click(screen.getByRole('button', { name: /apresentar contestação/i }))
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(3))
    openCenter()

    expect(
      await screen.findByText(/não foi possível marcar a notificação como lida/i),
    ).toBeVisible()
    expect(
      fetchMock.mock.calls.some(([, init]) => init?.method === 'PUT'),
    ).toBe(false)
  })

  it('Polling_VisibleOnlyAndVisibilityFocusRefreshWithoutOverlap', async () => {
    vi.useFakeTimers()
    let visibility: DocumentVisibilityState = 'visible'
    vi.spyOn(document, 'visibilityState', 'get').mockImplementation(() => visibility)
    const fetchMock = vi.fn().mockResolvedValue(response(200, feed()))
    vi.stubGlobal('fetch', fetchMock)
    renderCenter()
    await flushPromises()
    expect(fetchMock).toHaveBeenCalledTimes(1)

    await act(async () => vi.advanceTimersByTime(60_000))
    await flushPromises()
    expect(fetchMock).toHaveBeenCalledTimes(2)

    visibility = 'hidden'
    document.dispatchEvent(new Event('visibilitychange'))
    await act(async () => vi.advanceTimersByTime(60_000))
    expect(fetchMock).toHaveBeenCalledTimes(2)

    visibility = 'visible'
    document.dispatchEvent(new Event('visibilitychange'))
    window.dispatchEvent(new Event('focus'))
    await flushPromises()
    expect(fetchMock).toHaveBeenCalledTimes(3)
  })

  it('Polling_ConcurrentTimerFocusAndVisibilityShareOneInflightGet', async () => {
    vi.useFakeTimers()
    let resolveRequest: ((value: Response) => void) | undefined
    const request = new Promise<Response>((resolve) => {
      resolveRequest = resolve
    })
    const fetchMock = vi.fn().mockReturnValue(request)
    vi.stubGlobal('fetch', fetchMock)
    renderCenter()

    await act(async () => vi.advanceTimersByTime(60_000))
    window.dispatchEvent(new Event('focus'))
    document.dispatchEvent(new Event('visibilitychange'))
    expect(fetchMock).toHaveBeenCalledTimes(1)

    await act(async () => {
      resolveRequest?.(response(200, feed()))
      await request
    })
  })

  it('TenantSwitch_ClearsOpenOldFeedImmediatelyAndBFetchWins', async () => {
    let resolveB: ((value: Response) => void) | undefined
    const requestB = new Promise<Response>((resolve) => {
      resolveB = resolve
    })
    const itemB = notification({
      id: '77777777-7777-4777-8777-777777777777',
      sourceId: '88888888-8888-4888-8888-888888888888',
      sourceTitle: 'Notificação da organização B',
    })
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(response(200, feed([notification()], 1)))
      .mockReturnValueOnce(requestB)
    vi.stubGlobal('fetch', fetchMock)
    const view = renderCenter()
    await screen.findByRole('button', { name: 'Notificações, 1 não lidas' })
    openCenter()
    expect(screen.getByText('Apresentar contestação')).toBeVisible()

    view.switchOrganization(organizationB)

    expect(screen.queryByText('Apresentar contestação')).not.toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Notificações' })).toBeVisible()

    await act(async () => {
      resolveB?.(response(200, feed([itemB], 1)))
      await requestB
    })
    openCenter()
    expect(await screen.findByText('Notificação da organização B')).toBeVisible()
  })

  it('TenantSwitch_LateAResponseCannotPopulateB', async () => {
    let resolveA: ((value: Response) => void) | undefined
    const requestA = new Promise<Response>((resolve) => {
      resolveA = resolve
    })
    const itemB = notification({
      id: '77777777-7777-4777-8777-777777777777',
      sourceId: '88888888-8888-4888-8888-888888888888',
      sourceTitle: 'Notificação B',
    })
    const fetchMock = vi
      .fn()
      .mockReturnValueOnce(requestA)
      .mockResolvedValueOnce(response(200, feed([itemB], 1)))
    vi.stubGlobal('fetch', fetchMock)
    const view = renderCenter()

    view.switchOrganization(organizationB)
    await screen.findByRole('button', { name: 'Notificações, 1 não lidas' })
    await act(async () => {
      resolveA?.(response(200, feed([notification()], 1)))
      await requestA
    })
    openCenter()

    expect(screen.getByText('Notificação B')).toBeVisible()
    expect(screen.queryByText('Apresentar contestação')).not.toBeInTheDocument()
  })

  it('StrictMode_AbortedFirstEffectStartsFreshGetAndLateResponseCannotWin', async () => {
    let resolveFirst: ((value: Response) => void) | undefined
    const firstRequest = new Promise<Response>((resolve) => {
      resolveFirst = resolve
    })
    const currentItem = notification({ sourceTitle: 'Resposta atual' })
    const fetchMock = vi
      .fn()
      .mockReturnValueOnce(firstRequest)
      .mockResolvedValueOnce(response(200, feed([currentItem], 1)))
    vi.stubGlobal('fetch', fetchMock)

    render(<StrictMode>{centerTree(organizationA)}</StrictMode>)

    expect(
      await screen.findByRole('button', { name: 'Notificações, 1 não lidas' }),
    ).toBeVisible()
    expect(fetchMock).toHaveBeenCalledTimes(2)

    await act(async () => {
      resolveFirst?.(
        response(
          200,
          feed([notification({ sourceTitle: 'Resposta obsoleta' })], 1),
        ),
      )
      await firstRequest
    })
    openCenter()

    expect(screen.getByText('Resposta atual')).toBeVisible()
    expect(screen.queryByText('Resposta obsoleta')).not.toBeInTheDocument()
  })

  it('TenantSwitch_StaleAMutationCannotChangeBOrRefetchA', async () => {
    let resolveMutationA: ((value: Response) => void) | undefined
    const mutationA = new Promise<Response>((resolve) => {
      resolveMutationA = resolve
    })
    const itemB = notification({
      id: '77777777-7777-4777-8777-777777777777',
      sourceId: '88888888-8888-4888-8888-888888888888',
      sourceTitle: 'Notificação B',
    })
    const fetchMock = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      const url = input.toString()
      if (url === '/api/auth/csrf') {
        return Promise.resolve(response(200, { requestToken: 'csrf-token' }))
      }
      if (url.includes(organizationA) && init?.method === 'PUT') return mutationA
      if (url.includes(organizationA)) {
        return Promise.resolve(response(200, feed([notification()], 1)))
      }
      return Promise.resolve(response(200, feed([itemB], 1)))
    })
    vi.stubGlobal('fetch', fetchMock)
    const view = renderCenter()
    await screen.findByRole('button', { name: 'Notificações, 1 não lidas' })
    openCenter()
    fireEvent.click(screen.getByRole('button', { name: 'Marcar todas como lidas' }))
    await waitFor(() =>
      expect(
        fetchMock.mock.calls.some(
          ([input, init]) =>
            input.toString().includes(`${organizationA}/notifications/read-all`) &&
            init?.method === 'PUT',
        ),
      ).toBe(true),
    )

    view.switchOrganization(organizationB)
    await screen.findByRole('button', { name: 'Notificações, 1 não lidas' })
    await act(async () => {
      resolveMutationA?.(response(204))
      await mutationA
    })
    openCenter()

    expect(screen.getByText('Notificação B')).toBeVisible()
    expect(
      fetchMock.mock.calls.filter(([input]) => input.toString().includes(organizationA)),
    ).toHaveLength(2)
  })

  it('TenantSwitch_StopsOldPollingAndOnlyBContinues', async () => {
    vi.useFakeTimers()
    const fetchMock = vi.fn((input: RequestInfo | URL) => {
      const title = input.toString().includes(organizationA)
        ? 'Notificação A'
        : 'Notificação B'
      return Promise.resolve(
        response(200, feed([notification({ sourceTitle: title })], 1)),
      )
    })
    vi.stubGlobal('fetch', fetchMock)
    const view = renderCenter()
    await flushPromises()

    view.switchOrganization(organizationB)
    await flushPromises()
    await act(async () => vi.advanceTimersByTime(60_000))
    await flushPromises()

    expect(
      fetchMock.mock.calls.filter(([input]) => input.toString().includes(organizationA)),
    ).toHaveLength(1)
    expect(
      fetchMock.mock.calls.filter(([input]) => input.toString().includes(organizationB)),
    ).toHaveLength(2)
  })

  it('Panel_KeyboardActivationAndEscapeCloseReturnsFocus', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(response(200, feed())))
    renderCenter()
    const bell = await screen.findByRole('button', { name: 'Notificações' })
    bell.focus()

    fireEvent.click(bell, { detail: 0 })
    expect(screen.getByRole('dialog', { name: 'Notificações' })).toBeVisible()
    fireEvent.keyDown(document, { key: 'Escape' })

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(bell).toHaveFocus()
  })
})
