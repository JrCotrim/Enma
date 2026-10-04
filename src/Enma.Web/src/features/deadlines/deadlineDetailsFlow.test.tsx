import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

vi.mock('../notifications/NotificationCenter', () => ({
  NotificationCenter: () => null,
}))
import { createAppRoutes } from '../../app/router'
import { clearCsrfToken } from '../authentication/csrfClient'
import { createEmailVerificationFlow } from '../email-verification/emailVerificationService'
import type { OrganizationNavigationItem } from '../organizations/organizationTypes'
import type { LegalDeadline, LegalDeadlineListItem } from './legalDeadlineTypes'

const organizationA: OrganizationNavigationItem = {
  id: '11111111-1111-4111-8111-111111111111',
  membershipId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa1',
  name: 'Organização Alfa',
  role: 'Owner',
}

const organizationB: OrganizationNavigationItem = {
  id: '22222222-2222-4222-8222-222222222222',
  membershipId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbb2',
  name: 'Organização Beta',
  role: 'Administrator',
}

const deadlineA: LegalDeadline = {
  id: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
  title: 'Apresentar contestação',
  dueDate: '2026-11-01',
  processId: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc',
  processTitle: 'Ação de cobrança',
  clientName: 'Cliente Exemplo',
  state: 'Pending',
  createdAt: '2026-08-12T14:30:00Z',
  completedAt: null,
  responsibleMembershipId: null,
  responsibleDisplayName: null,
}

const deadlineB: LegalDeadline = {
  ...deadlineA,
  id: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',
  title: 'Protocolar recurso',
  dueDate: '2028-02-29',
  processTitle: 'Revisional de contrato',
  clientName: 'Cliente Beta',
  state: 'Completed',
  completedAt: '2026-08-13T16:45:00Z',
}

function response(status: number, body?: unknown): Response {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers:
      body === undefined ? undefined : { 'Content-Type': 'application/json' },
  })
}

function organizationResponse(
  items: readonly OrganizationNavigationItem[],
): Response {
  return response(200, { items })
}

function deadlineListResponse(items: readonly LegalDeadlineListItem[]): Response {
  return response(200, { items, pageNumber: 1, pageSize: 20 })
}

function authenticatedFetch(
  organizations: readonly OrganizationNavigationItem[],
  ...scopedResponses: readonly (Response | Promise<Response>)[]
) {
  const fetchMock = vi
    .fn()
    .mockResolvedValueOnce(organizationResponse([]))
    .mockResolvedValueOnce(organizationResponse(organizations))

  for (const scopedResponse of scopedResponses) {
    fetchMock.mockReturnValueOnce(Promise.resolve(scopedResponse))
  }

  return fetchMock
}

function detailPath(
  organization: OrganizationNavigationItem,
  deadline: LegalDeadline,
): string {
  return `/organizations/${organization.id}/deadlines/${deadline.id}`
}

function renderRoute(path: string) {
  const router = createMemoryRouter(
    createAppRoutes(createEmailVerificationFlow(undefined)),
    { initialEntries: [path] },
  )

  render(<RouterProvider router={router} />)
  return router
}

function openEdit() {
  fireEvent.click(screen.getByRole('button', { name: 'Editar' }))
  return screen.getByRole('button', { name: 'Salvar' }).closest('form')!
}

function submitEdit(title: string, dueDate: string) {
  const form = openEdit()
  fireEvent.change(screen.getByLabelText('Título'), {
    target: { value: title },
  })
  fireEvent.change(screen.getByLabelText('Data do prazo'), {
    target: { value: dueDate },
  })
  fireEvent.submit(form)
  return form
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

describe('Deadlines D2 detail flow', () => {
  it('DeadlineDetail_MemberRoute_RendersContextualReadOnlyFieldsWithSeparateDateSemantics', async () => {
    const member = { ...organizationA, role: 'Member' as const }
    const localStorageSpy = vi.spyOn(window.localStorage, 'setItem')
    const sessionStorageSpy = vi.spyOn(window.sessionStorage, 'setItem')
    const fetchMock = authenticatedFetch([member], response(200, deadlineB))
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(member, deadlineB))

    expect(
      await screen.findByRole('heading', { name: deadlineB.title }),
    ).toBeInTheDocument()
    expect(screen.getByText(deadlineB.processTitle)).toBeInTheDocument()
    expect(screen.getByText(deadlineB.clientName)).toBeInTheDocument()
    expect(screen.getByText('29/02/2028')).toBeInTheDocument()
    expect(screen.getByText('29/02/2028').closest('time')).toHaveAttribute(
      'datetime',
      deadlineB.dueDate,
    )
    expect(screen.getByText('Concluído')).toBeInTheDocument()
    expect(
      screen.getByRole('heading', { name: 'Vínculos e histórico' }),
    ).toBeInTheDocument()
    expect(screen.getByText(/12\/08\/2026/)).toBeInTheDocument()
    expect(screen.getByText(/13\/08\/2026/)).toBeInTheDocument()
    expect(screen.queryByText(deadlineB.id)).not.toBeInTheDocument()
    expect(screen.queryByText(deadlineB.processId)).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Editar' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Reabrir' })).not.toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Voltar para prazos' })).toHaveAttribute(
      'href',
      `/organizations/${member.id}/deadlines`,
    )
    expect(fetchMock).toHaveBeenNthCalledWith(
      3,
      `/api/organizations/${member.id}/deadlines/${deadlineB.id}`,
      {
        method: 'GET',
        cache: 'no-store',
        signal: expect.any(AbortSignal),
        credentials: 'same-origin',
      },
    )
    expect(localStorageSpy).not.toHaveBeenCalled()
    expect(sessionStorageSpy).not.toHaveBeenCalled()
  })

  it.each(['Owner', 'Administrator'] as const)(
    'DeadlineDetail_Pending%s_ShowsEditAndCompleteOnly',
    async (role) => {
      const organization = { ...organizationA, role }
      vi.stubGlobal(
        'fetch',
        authenticatedFetch([organization], response(200, deadlineA)),
      )

      renderRoute(detailPath(organization, deadlineA))

      expect(await screen.findByRole('button', { name: 'Editar' })).toBeInTheDocument()
      expect(screen.getByRole('button', { name: 'Concluir' })).toBeInTheDocument()
      expect(screen.queryByRole('button', { name: 'Reabrir' })).not.toBeInTheDocument()
    },
  )

  it.each(['Owner', 'Administrator'] as const)(
    'DeadlineDetail_Completed%s_ShowsReopenWithoutEdit',
    async (role) => {
      const organization = { ...organizationA, role }
      vi.stubGlobal(
        'fetch',
        authenticatedFetch([organization], response(200, deadlineB)),
      )

      renderRoute(detailPath(organization, deadlineB))

      expect(await screen.findByRole('button', { name: 'Reabrir' })).toBeInTheDocument()
      expect(screen.queryByRole('button', { name: 'Editar' })).not.toBeInTheDocument()
      expect(screen.queryByRole('button', { name: 'Concluir' })).not.toBeInTheDocument()
    },
  )

  it('DeadlineList_TitleLink_NavigatesToCurrentOrganizationDetail', async () => {
    const fetchMock = authenticatedFetch(
      [organizationA],
      deadlineListResponse([deadlineA]),
      response(200, deadlineA),
    )
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(`/organizations/${organizationA.id}/deadlines`)

    fireEvent.click(await screen.findByRole('link', { name: deadlineA.title }))

    expect(
      await screen.findByRole('heading', { name: deadlineA.title }),
    ).toBeInTheDocument()
    expect(router.state.location.pathname).toBe(detailPath(organizationA, deadlineA))
  })

  it('DeadlineDetail_MalformedDeadlineId_DoesNotIssueBusinessRequest', async () => {
    const fetchMock = authenticatedFetch([organizationA])
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(`/organizations/${organizationA.id}/deadlines/not-a-guid`)

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Prazo não encontrado ou indisponível.',
    )
    expect(fetchMock).toHaveBeenCalledTimes(2)
  })

  it.each([
    [404, 'Prazo não encontrado ou indisponível.'],
    [403, 'Não foi possível acessar este prazo.'],
  ] as const)(
    'DeadlineDetail_Status%s_ShowsSafeStateWithoutServerDetail',
    async (status, expectedMessage) => {
      vi.stubGlobal(
        'fetch',
        authenticatedFetch(
          [organizationA],
          response(status, { detail: 'private tenant detail' }),
        ),
      )

      renderRoute(detailPath(organizationA, deadlineA))

      const alert = await screen.findByRole('alert')
      expect(alert).toHaveTextContent(expectedMessage)
      expect(alert).not.toHaveTextContent('private tenant detail')
    },
  )

  it.each([
    { ...deadlineA, dueDate: '2026-02-30' },
    { ...deadlineA, state: 'Completed', completedAt: null },
    { ...deadlineA, createdAt: 'not-a-timestamp' },
    { ...deadlineA, id: 'not-a-guid' },
    { ...deadlineA, processId: 'not-a-guid' },
    { ...deadlineA, processTitle: 42 },
  ])('DeadlineDetail_MalformedResponse_ShowsGenericSafeError', async (body) => {
    vi.stubGlobal(
      'fetch',
      authenticatedFetch([organizationA], response(200, body)),
    )

    renderRoute(detailPath(organizationA, deadlineA))

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Não foi possível carregar o prazo. Tente novamente.',
    )
    expect(screen.queryByRole('heading', { name: deadlineA.title })).not.toBeInTheDocument()
  })

  it('DeadlineDetail_Unauthorized_InvalidatesSessionAndRemovesProtectedDetail', async () => {
    vi.stubGlobal(
      'fetch',
      authenticatedFetch([organizationA], response(401)),
    )

    renderRoute(detailPath(organizationA, deadlineA))

    expect(
      await screen.findByRole('heading', { name: 'Entrar no ENMA' }),
    ).toBeInTheDocument()
    expect(screen.queryByText(deadlineA.title)).not.toBeInTheDocument()
  })

  it('DeadlineEdit_TrimmedTitleAndLiteralDate_SendsExactCsrfBodyWithoutOptimismThenRefetches', async () => {
    let resolveUpdate: ((value: Response) => void) | undefined
    const pendingUpdate = new Promise<Response>((resolve) => {
      resolveUpdate = resolve
    })
    const updated = {
      ...deadlineA,
      title: 'Prazo normalizado pelo servidor',
      dueDate: '2020-02-29',
    }
    const fetchMock = authenticatedFetch(
      [organizationA],
      response(200, deadlineA),
      response(200, { requestToken: 'update-token' }),
      pendingUpdate,
      response(200, updated),
    )
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(organizationA, deadlineA))
    await screen.findByRole('heading', { name: deadlineA.title })
    const form = submitEdit('  Título enviado  ', '2020-02-29')

    expect(await screen.findByRole('button', { name: 'Salvando...' })).toBeDisabled()
    fireEvent.submit(form)
    expect(fetchMock).toHaveBeenCalledTimes(5)
    expect(screen.getByRole('heading', { name: deadlineA.title })).toBeInTheDocument()

    const [url, init] = fetchMock.mock.calls[4] as [string, RequestInit]
    expect(url).toBe(`/api/organizations/${organizationA.id}/deadlines/${deadlineA.id}`)
    expect(init.method).toBe('PUT')
    expect(init.headers).toEqual({
      'Content-Type': 'application/json',
      'X-CSRF-TOKEN': 'update-token',
    })
    expect(JSON.parse(init.body as string)).toEqual({
      title: 'Título enviado',
      dueDate: '2020-02-29',
    })
    expect(Object.keys(JSON.parse(init.body as string))).toEqual(['title', 'dueDate'])

    await act(async () => {
      resolveUpdate?.(response(204))
      await pendingUpdate
    })

    expect(
      await screen.findByRole('heading', { name: updated.title }),
    ).toBeInTheDocument()
    expect(screen.getByText('29/02/2020')).toBeInTheDocument()
    expect(fetchMock.mock.calls[5]?.[0]).toBe(url)
    expect(fetchMock.mock.calls[5]?.[1]?.method).toBe('GET')
  })

  it('DeadlineEdit_ValidationRejectsWhitespaceOverlongAndMissingDateButAccepts150Characters', async () => {
    const acceptedTitle = 'x'.repeat(150)
    const updated = { ...deadlineA, title: acceptedTitle, dueDate: '1999-12-31' }
    const fetchMock = authenticatedFetch(
      [organizationA],
      response(200, deadlineA),
      response(200, { requestToken: 'test-token' }),
      response(204),
      response(200, updated),
    )
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(organizationA, deadlineA))
    await screen.findByRole('heading', { name: deadlineA.title })
    const form = openEdit()
    const titleInput = screen.getByLabelText('Título')
    const dateInput = screen.getByLabelText('Data do prazo')

    fireEvent.change(titleInput, { target: { value: '   ' } })
    fireEvent.submit(form)
    expect(await screen.findByText('Informe o título do prazo.')).toBeInTheDocument()

    fireEvent.change(titleInput, { target: { value: 'x'.repeat(151) } })
    fireEvent.submit(form)
    expect(
      await screen.findByText('O título deve ter no máximo 150 caracteres.'),
    ).toBeInTheDocument()

    fireEvent.change(titleInput, { target: { value: acceptedTitle } })
    fireEvent.change(dateInput, { target: { value: '' } })
    fireEvent.submit(form)
    expect(await screen.findByText('Informe uma data do prazo válida.')).toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(3)

    fireEvent.change(dateInput, { target: { value: '1999-12-31' } })
    fireEvent.submit(form)
    expect(
      await screen.findByRole('heading', { name: acceptedTitle }),
    ).toBeInTheDocument()
  })

  it('DeadlineEdit_ConflictRefetchesCompletedStateWithoutRetryOrAutomaticReopen', async () => {
    const completed = {
      ...deadlineA,
      state: 'Completed' as const,
      completedAt: '2026-08-14T12:00:00Z',
    }
    const fetchMock = authenticatedFetch(
      [organizationA],
      response(200, deadlineA),
      response(200, { requestToken: 'test-token' }),
      response(409, { detail: 'internal conflict detail' }),
      response(200, completed),
    )
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(organizationA, deadlineA))
    await screen.findByRole('heading', { name: deadlineA.title })
    submitEdit('Título que não será salvo', deadlineA.dueDate)

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent(
      'Este prazo foi concluído e precisa ser reaberto antes de ser editado.',
    )
    expect(alert).not.toHaveTextContent('internal conflict detail')
    expect(screen.getByRole('heading', { name: deadlineA.title })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Reabrir' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Editar' })).not.toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(6)
    expect(
      fetchMock.mock.calls.filter(([, init]) => init?.method === 'PUT'),
    ).toHaveLength(1)
  })

  it.each([
    [403, 'Você não tem permissão para alterar este prazo.'],
    [400, 'Não foi possível validar a solicitação.'],
  ] as const)(
    'DeadlineEdit_Status%s_KeepsAuthoritativeResourceWithoutRetry',
    async (status, expectedMessage) => {
      const fetchMock = authenticatedFetch(
        [organizationA],
        response(200, deadlineA),
        response(200, { requestToken: 'test-token' }),
        response(status, { detail: 'private mutation detail' }),
      )
      vi.stubGlobal('fetch', fetchMock)

      renderRoute(detailPath(organizationA, deadlineA))
      await screen.findByRole('heading', { name: deadlineA.title })
      submitEdit('Mudança não confirmada', deadlineA.dueDate)

      const alert = await screen.findByRole('alert')
      expect(alert).toHaveTextContent(expectedMessage)
      expect(alert).not.toHaveTextContent('private mutation detail')
      expect(screen.getByRole('heading', { name: deadlineA.title })).toBeInTheDocument()
      expect(fetchMock).toHaveBeenCalledTimes(5)
    },
  )

  it('DeadlineMutation_NotFoundRemovesResourceWithoutTenantInference', async () => {
    const fetchMock = authenticatedFetch(
      [organizationA],
      response(200, deadlineA),
      response(200, { requestToken: 'test-token' }),
      response(404, { detail: 'cross tenant' }),
    )
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(organizationA, deadlineA))
    await screen.findByRole('heading', { name: deadlineA.title })
    fireEvent.click(screen.getByRole('button', { name: 'Concluir' }))

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Prazo não encontrado ou indisponível.')
    expect(alert).not.toHaveTextContent('cross tenant')
    expect(screen.queryByText(deadlineA.title)).not.toBeInTheDocument()
  })

  it('DeadlineMutation_NetworkFailureShowsUncertainStateWithoutRetryOrFakeTransition', async () => {
    const fetchMock = authenticatedFetch(
      [organizationA],
      response(200, deadlineA),
      response(200, { requestToken: 'test-token' }),
    ).mockRejectedValueOnce(new Error('private network detail'))
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(organizationA, deadlineA))
    await screen.findByRole('heading', { name: deadlineA.title })
    fireEvent.click(screen.getByRole('button', { name: 'Concluir' }))

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Atualize os dados antes de tentar novamente.')
    expect(alert).not.toHaveTextContent('private network detail')
    expect(screen.getByText('Pendente')).toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(5)
  })

  it.each(['complete', 'reopen'] as const)(
    'DeadlineLifecycle_%sForbidden_ShowsPermissionWithoutFakeTransitionOrRetry',
    async (kind) => {
      const source = kind === 'complete' ? deadlineA : deadlineB
      const fetchMock = authenticatedFetch(
        [organizationA],
        response(200, source),
        response(200, { requestToken: 'test-token' }),
        response(403, { detail: 'private stale role detail' }),
      )
      vi.stubGlobal('fetch', fetchMock)

      renderRoute(detailPath(organizationA, source))
      await screen.findByRole('heading', { name: source.title })
      fireEvent.click(
        screen.getByRole('button', {
          name: kind === 'complete' ? 'Concluir' : 'Reabrir',
        }),
      )

      const alert = await screen.findByRole('alert')
      expect(alert).toHaveTextContent(
        'Você não tem permissão para alterar este prazo.',
      )
      expect(alert).not.toHaveTextContent('private stale role detail')
      expect(screen.getByText(kind === 'complete' ? 'Pendente' : 'Concluído')).toBeInTheDocument()
      expect(fetchMock).toHaveBeenCalledTimes(5)
    },
  )

  it('DeadlineMutation_Unauthorized_InvalidatesSessionWithoutRetainingDeadline', async () => {
    const fetchMock = authenticatedFetch(
      [organizationA],
      response(200, deadlineA),
      response(200, { requestToken: 'test-token' }),
      response(401),
    )
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(organizationA, deadlineA))
    await screen.findByRole('heading', { name: deadlineA.title })
    fireEvent.click(screen.getByRole('button', { name: 'Concluir' }))

    expect(
      await screen.findByRole('heading', { name: 'Entrar no ENMA' }),
    ).toBeInTheDocument()
    expect(screen.queryByText(deadlineA.title)).not.toBeInTheDocument()
  })

  it('DeadlineComplete_UsesCsrfNoBodyAndChangesOnlyAfterAuthoritativeRefetch', async () => {
    let resolveComplete: ((value: Response) => void) | undefined
    const pendingComplete = new Promise<Response>((resolve) => {
      resolveComplete = resolve
    })
    const completed = {
      ...deadlineA,
      state: 'Completed' as const,
      completedAt: '2026-08-14T12:00:00Z',
    }
    const fetchMock = authenticatedFetch(
      [organizationA],
      response(200, deadlineA),
      response(200, { requestToken: 'complete-token' }),
      pendingComplete,
      response(200, completed),
    )
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(organizationA, deadlineA))
    await screen.findByRole('heading', { name: deadlineA.title })
    fireEvent.click(screen.getByRole('button', { name: 'Concluir' }))

    expect(await screen.findByRole('button', { name: 'Concluindo...' })).toBeDisabled()
    expect(screen.getByText('Pendente')).toBeInTheDocument()
    const [url, init] = fetchMock.mock.calls[4] as [string, RequestInit]
    expect(url).toBe(`/api/organizations/${organizationA.id}/deadlines/${deadlineA.id}/complete`)
    expect(init.method).toBe('POST')
    expect(init.headers).toEqual({ 'X-CSRF-TOKEN': 'complete-token' })
    expect(init.body).toBeUndefined()

    await act(async () => {
      resolveComplete?.(response(204))
      await pendingComplete
    })

    expect(await screen.findByText('Concluído')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Reabrir' })).toBeInTheDocument()
    expect(screen.getByText(/14\/08\/2026/)).toBeInTheDocument()
  })

  it('DeadlineReopen_UsesCsrfNoBodyAndChangesOnlyAfterAuthoritativeRefetch', async () => {
    let resolveReopen: ((value: Response) => void) | undefined
    const pendingReopen = new Promise<Response>((resolve) => {
      resolveReopen = resolve
    })
    const reopened = { ...deadlineB, state: 'Pending' as const, completedAt: null }
    const fetchMock = authenticatedFetch(
      [organizationA],
      response(200, deadlineB),
      response(200, { requestToken: 'reopen-token' }),
      pendingReopen,
      response(200, reopened),
    )
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(organizationA, deadlineB))
    await screen.findByRole('heading', { name: deadlineB.title })
    fireEvent.click(screen.getByRole('button', { name: 'Reabrir' }))

    expect(await screen.findByRole('button', { name: 'Reabrindo...' })).toBeDisabled()
    expect(screen.getByText('Concluído')).toBeInTheDocument()
    const [url, init] = fetchMock.mock.calls[4] as [string, RequestInit]
    expect(url).toBe(`/api/organizations/${organizationA.id}/deadlines/${deadlineB.id}/reopen`)
    expect(init.method).toBe('POST')
    expect(init.headers).toEqual({ 'X-CSRF-TOKEN': 'reopen-token' })
    expect(init.body).toBeUndefined()

    await act(async () => {
      resolveReopen?.(response(204))
      await pendingReopen
    })

    expect(await screen.findByText('Pendente')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Editar' })).toBeInTheDocument()
    expect(screen.queryByText('Concluído em')).not.toBeInTheDocument()
  })

  it.each([
    ['same organization', organizationA, deadlineB],
    ['cross organization', organizationB, deadlineB],
  ] as const)(
    'DeadlineDetail_OldResponseAfterNavigation_%s_RemainsOnNewContext',
    async (_scenario, targetOrganization, targetDeadline) => {
      let resolveA: ((value: Response) => void) | undefined
      const pendingA = new Promise<Response>((resolve) => {
        resolveA = resolve
      })
      const organizations =
        targetOrganization.id === organizationA.id
          ? [organizationA]
          : [organizationA, organizationB]
      const fetchMock = authenticatedFetch(
        organizations,
        pendingA,
        response(200, targetDeadline),
      )
      vi.stubGlobal('fetch', fetchMock)
      const router = renderRoute(detailPath(organizationA, deadlineA))

      await screen.findByText('Carregando prazo...')
      await act(async () => router.navigate(detailPath(targetOrganization, targetDeadline)))
      expect(
        await screen.findByRole('heading', { name: targetDeadline.title }),
      ).toBeInTheDocument()

      await act(async () => {
        resolveA?.(response(200, deadlineA))
        await pendingA
      })

      expect(screen.getByRole('heading', { name: targetDeadline.title })).toBeInTheDocument()
      expect(screen.queryByRole('heading', { name: deadlineA.title })).not.toBeInTheDocument()
    },
  )

  it('DeadlineDetail_SameIdAcrossOrganizations_UsesFullContextIdentity', async () => {
    let resolveA: ((value: Response) => void) | undefined
    const pendingA = new Promise<Response>((resolve) => {
      resolveA = resolve
    })
    const sameIdB = { ...deadlineB, id: deadlineA.id }
    const fetchMock = authenticatedFetch(
      [organizationA, organizationB],
      pendingA,
      response(200, sameIdB),
    )
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(detailPath(organizationA, deadlineA))

    await screen.findByText('Carregando prazo...')
    await act(async () => router.navigate(detailPath(organizationB, sameIdB)))
    expect(await screen.findByRole('heading', { name: sameIdB.title })).toBeInTheDocument()

    await act(async () => {
      resolveA?.(response(200, deadlineA))
      await pendingA
    })

    expect(screen.getByRole('heading', { name: sameIdB.title })).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: deadlineA.title })).not.toBeInTheDocument()
  })

  it.each(['update', 'complete', 'reopen'] as const)(
    'DeadlineMutation_%sLateResponseAfterNavigation_DoesNotAffectTarget',
    async (kind) => {
      let resolveMutation: ((value: Response) => void) | undefined
      const pendingMutation = new Promise<Response>((resolve) => {
        resolveMutation = resolve
      })
      const source = kind === 'reopen' ? deadlineB : deadlineA
      const target = kind === 'reopen' ? deadlineA : deadlineB
      const fetchMock = authenticatedFetch(
        [organizationA],
        response(200, source),
        response(200, { requestToken: 'test-token' }),
        pendingMutation,
        response(200, target),
      )
      vi.stubGlobal('fetch', fetchMock)
      const router = renderRoute(detailPath(organizationA, source))

      await screen.findByRole('heading', { name: source.title })
      if (kind === 'update') {
        submitEdit('Rascunho atrasado', source.dueDate)
      } else {
        fireEvent.click(
          screen.getByRole('button', {
            name: kind === 'complete' ? 'Concluir' : 'Reabrir',
          }),
        )
      }

      await screen.findByRole('button', {
        name:
          kind === 'update'
            ? 'Salvando...'
            : kind === 'complete'
              ? 'Concluindo...'
              : 'Reabrindo...',
      })
      await act(async () => router.navigate(detailPath(organizationA, target)))
      expect(await screen.findByRole('heading', { name: target.title })).toBeInTheDocument()

      await act(async () => {
        resolveMutation?.(response(204))
        await pendingMutation
      })

      await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(6))
      expect(screen.getByRole('heading', { name: target.title })).toBeInTheDocument()
      expect(screen.queryByText('Rascunho atrasado')).not.toBeInTheDocument()
      expect(screen.queryByText(/sucesso\./)).not.toBeInTheDocument()
    },
  )

  it('DeadlineUpdate_LateResponseAfterOrganizationChange_DoesNotAlterNewTenant', async () => {
    let resolveUpdate: ((value: Response) => void) | undefined
    const pendingUpdate = new Promise<Response>((resolve) => {
      resolveUpdate = resolve
    })
    const fetchMock = authenticatedFetch(
      [organizationA, organizationB],
      response(200, deadlineA),
      response(200, { requestToken: 'test-token' }),
      pendingUpdate,
      response(200, deadlineB),
    )
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(detailPath(organizationA, deadlineA))

    await screen.findByRole('heading', { name: deadlineA.title })
    submitEdit('Mudança da organização A', deadlineA.dueDate)
    await screen.findByRole('button', { name: 'Salvando...' })
    await act(async () => router.navigate(detailPath(organizationB, deadlineB)))
    expect(await screen.findByRole('heading', { name: deadlineB.title })).toBeInTheDocument()

    await act(async () => {
      resolveUpdate?.(response(204))
      await pendingUpdate
    })

    expect(screen.getByRole('heading', { name: deadlineB.title })).toBeInTheDocument()
    expect(screen.queryByText('Mudança da organização A')).not.toBeInTheDocument()
  })

  it.each(['update', 'complete'] as const)(
    'DeadlineMutation_%sRefetchCompletesAfterNavigation_DoesNotOverwriteTarget',
    async (kind) => {
      let resolveRefetch: ((value: Response) => void) | undefined
      const pendingRefetch = new Promise<Response>((resolve) => {
        resolveRefetch = resolve
      })
      const authoritativeA =
        kind === 'update'
          ? { ...deadlineA, title: 'Resultado confirmado em A' }
          : {
              ...deadlineA,
              state: 'Completed' as const,
              completedAt: '2026-08-14T12:00:00Z',
            }
      const fetchMock = authenticatedFetch(
        [organizationA],
        response(200, deadlineA),
        response(200, { requestToken: 'test-token' }),
        response(204),
        pendingRefetch,
        response(200, deadlineB),
      )
      vi.stubGlobal('fetch', fetchMock)
      const router = renderRoute(detailPath(organizationA, deadlineA))

      await screen.findByRole('heading', { name: deadlineA.title })
      if (kind === 'update') {
        submitEdit(authoritativeA.title, deadlineA.dueDate)
      } else {
        fireEvent.click(screen.getByRole('button', { name: 'Concluir' }))
      }
      await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(6))
      await act(async () => router.navigate(detailPath(organizationA, deadlineB)))
      expect(await screen.findByRole('heading', { name: deadlineB.title })).toBeInTheDocument()

      await act(async () => {
        resolveRefetch?.(response(200, authoritativeA))
        await pendingRefetch
      })

      expect(screen.getByRole('heading', { name: deadlineB.title })).toBeInTheDocument()
      expect(screen.queryByRole('heading', { name: authoritativeA.title })).not.toBeInTheDocument()
    },
  )

  it('DeadlineEdit_NavigationResetsDraftValidationAndLifecycleControls', async () => {
    const fetchMock = authenticatedFetch(
      [organizationA],
      response(200, deadlineA),
      response(200, deadlineB),
    )
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(detailPath(organizationA, deadlineA))

    await screen.findByRole('heading', { name: deadlineA.title })
    const form = openEdit()
    fireEvent.change(screen.getByLabelText('Título'), {
      target: { value: 'Rascunho exclusivo de A' },
    })
    fireEvent.change(screen.getByLabelText('Data do prazo'), {
      target: { value: '' },
    })
    fireEvent.submit(form)
    expect(await screen.findByRole('alert')).toBeInTheDocument()

    await act(async () => router.navigate(detailPath(organizationA, deadlineB)))

    expect(await screen.findByRole('heading', { name: deadlineB.title })).toBeInTheDocument()
    expect(screen.queryByDisplayValue('Rascunho exclusivo de A')).not.toBeInTheDocument()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Reabrir' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Concluir' })).not.toBeInTheDocument()
  })
})

const responsibleMember = {
  id: 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee',
  displayName: 'Bruna Costa',
}

function memberLookupResponse(
  items: readonly { readonly id: string; readonly displayName: string }[],
): Response {
  return response(200, { items, pageNumber: 1, pageSize: 20, hasNext: false })
}

function memberLookupUrl(organizationId: string): string {
  return `/api/organizations/${organizationId}/members/lookup?search=&pageNumber=1&pageSize=20`
}

function responsibleUrl(deadline: LegalDeadline): string {
  return `/api/organizations/${organizationA.id}/deadlines/${deadline.id}/responsible`
}

function metadataEntries(): [string | null, string | null][] {
  const metadata = document.querySelector('.deadline-details-metadata')!
  return [...metadata.children].map((entry) => [
    entry.querySelector('dt')!.textContent,
    entry.querySelector('dd')!.textContent,
  ])
}

function responsibleModeSelect(): HTMLSelectElement {
  return screen.getByLabelText('Novo responsável') as HTMLSelectElement
}

describe('Deadlines responsible detail (Phase 9C.2e)', () => {
  it('DeadlineDetail_Metadata_ListsResponsibleInVerticalLabelValueList', async () => {
    const assigned = {
      ...deadlineA,
      responsibleMembershipId: responsibleMember.id,
      responsibleDisplayName: responsibleMember.displayName,
    }
    vi.stubGlobal('fetch', authenticatedFetch([organizationA], response(200, assigned)))

    renderRoute(detailPath(organizationA, assigned))
    await screen.findByRole('heading', { name: assigned.title })

    expect(metadataEntries()).toEqual([
      ['Vencimento', '01/11/2026'],
      ['Estado', 'Pendente'],
      ['Responsável', responsibleMember.displayName],
    ])
    expect(screen.queryByText(responsibleMember.id)).not.toBeInTheDocument()
  })

  it('DeadlineDetail_Member_SeesResponsibleWithoutAnyMutationAction', async () => {
    const member = { ...organizationA, role: 'Member' as const }
    const fetchMock = authenticatedFetch([member], response(200, deadlineA))
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(member, deadlineA))
    await screen.findByRole('heading', { name: deadlineA.title })

    expect(metadataEntries()).toContainEqual(['Responsável', 'Sem responsável'])
    for (const action of [
      'Alterar responsável',
      'Editar',
      'Concluir',
      'Reabrir',
    ]) {
      expect(screen.queryByRole('button', { name: action })).not.toBeInTheDocument()
    }
    expect(screen.queryByLabelText('Novo responsável')).not.toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(3)
  })

  it('DeadlineResponsible_UnassignedToSelf_SendsCsrfPutAndRefetchesAuthoritatively', async () => {
    let resolveChange: ((value: Response) => void) | undefined
    const pendingChange = new Promise<Response>((resolve) => {
      resolveChange = resolve
    })
    const assignedToSelf = {
      ...deadlineA,
      responsibleMembershipId: organizationA.membershipId,
      responsibleDisplayName: 'Ana Dona',
    }
    const fetchMock = authenticatedFetch(
      [organizationA],
      response(200, deadlineA),
      response(200, { requestToken: 'responsible-token' }),
      pendingChange,
      response(200, assignedToSelf),
    )
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(organizationA, deadlineA))
    await screen.findByRole('heading', { name: deadlineA.title })
    fireEvent.click(screen.getByRole('button', { name: 'Alterar responsável' }))

    expect(responsibleModeSelect()).toHaveValue('unassigned')
    expect(
      within(responsibleModeSelect())
        .getAllByRole('option')
        .map((option) => option.textContent),
    ).toEqual(['Sem responsável', 'Eu', 'Outra pessoa'])
    expect(screen.queryByRole('button', { name: 'Concluir' })).not.toBeInTheDocument()
    fireEvent.change(responsibleModeSelect(), { target: { value: 'self' } })
    expect(screen.queryByLabelText('Buscar novo responsável')).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Salvar responsável' }))

    expect(await screen.findByRole('button', { name: 'Salvando...' })).toBeDisabled()
    expect(metadataEntries()).toContainEqual(['Responsável', 'Sem responsável'])
    const [url, init] = fetchMock.mock.calls[4] as [string, RequestInit]
    expect(url).toBe(responsibleUrl(deadlineA))
    expect(init.method).toBe('PUT')
    expect(init.headers).toEqual({
      'Content-Type': 'application/json',
      'X-CSRF-TOKEN': 'responsible-token',
    })
    expect(JSON.parse(init.body as string)).toEqual({
      responsibleMembershipId: organizationA.membershipId,
    })

    await act(async () => {
      resolveChange?.(response(204))
      await pendingChange
    })

    expect(
      await screen.findByText('Responsável atualizado com sucesso.'),
    ).toBeInTheDocument()
    expect(metadataEntries()).toContainEqual(['Responsável', 'Ana Dona'])
    expect(screen.queryByLabelText('Novo responsável')).not.toBeInTheDocument()
    expect(fetchMock.mock.calls[5]?.[0]).toBe(
      `/api/organizations/${organizationA.id}/deadlines/${deadlineA.id}`,
    )
    expect(fetchMock).toHaveBeenCalledTimes(6)
  })

  it('DeadlineResponsible_OtherPersonViaPicker_SendsSelectedMembership', async () => {
    const assigned = {
      ...deadlineA,
      responsibleMembershipId: responsibleMember.id,
      responsibleDisplayName: responsibleMember.displayName,
    }
    const fetchMock = authenticatedFetch(
      [organizationA],
      response(200, deadlineA),
      memberLookupResponse([responsibleMember]),
      response(200, { requestToken: 'test-token' }),
      response(204),
      response(200, assigned),
    )
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(organizationA, deadlineA))
    await screen.findByRole('heading', { name: deadlineA.title })
    fireEvent.click(screen.getByRole('button', { name: 'Alterar responsável' }))
    fireEvent.change(responsibleModeSelect(), { target: { value: 'other' } })

    fireEvent.click(
      await screen.findByRole('button', { name: responsibleMember.displayName }),
    )
    expect(fetchMock.mock.calls[3]?.[0]).toBe(memberLookupUrl(organizationA.id))
    expect(screen.getByText(/Responsável selecionado:/)).toHaveTextContent(
      `Responsável selecionado: ${responsibleMember.displayName}`,
    )
    fireEvent.click(screen.getByRole('button', { name: 'Salvar responsável' }))

    expect(
      await screen.findByText('Responsável atualizado com sucesso.'),
    ).toBeInTheDocument()
    const init = fetchMock.mock.calls[5]?.[1] as RequestInit
    expect(JSON.parse(init.body as string)).toEqual({
      responsibleMembershipId: responsibleMember.id,
    })
    expect(metadataEntries()).toContainEqual([
      'Responsável',
      responsibleMember.displayName,
    ])
  })

  it('DeadlineResponsible_OtherPersonWithoutSelection_ShowsValidationWithoutRequest', async () => {
    const fetchMock = authenticatedFetch(
      [organizationA],
      response(200, deadlineA),
      memberLookupResponse([responsibleMember]),
    )
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(organizationA, deadlineA))
    await screen.findByRole('heading', { name: deadlineA.title })
    fireEvent.click(screen.getByRole('button', { name: 'Alterar responsável' }))
    fireEvent.change(responsibleModeSelect(), { target: { value: 'other' } })
    await screen.findByRole('button', { name: responsibleMember.displayName })
    fireEvent.click(screen.getByRole('button', { name: 'Salvar responsável' }))

    expect(
      await screen.findByText('Selecione uma pessoa responsável.'),
    ).toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(4)
  })

  it('DeadlineResponsible_CompletedDeadline_AllowsChangeToUnassigned', async () => {
    const completedAssigned = {
      ...deadlineB,
      responsibleMembershipId: responsibleMember.id,
      responsibleDisplayName: responsibleMember.displayName,
    }
    const fetchMock = authenticatedFetch(
      [organizationA],
      response(200, completedAssigned),
      memberLookupResponse([responsibleMember]),
      response(200, { requestToken: 'test-token' }),
      response(204),
      response(200, deadlineB),
    )
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(organizationA, completedAssigned))
    await screen.findByRole('heading', { name: completedAssigned.title })
    expect(screen.getByRole('button', { name: 'Reabrir' })).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Alterar responsável' }))

    expect(responsibleModeSelect()).toHaveValue('other')
    expect(screen.getByText(/Responsável selecionado:/)).toHaveTextContent(
      `Responsável selecionado: ${responsibleMember.displayName}`,
    )
    expect(
      await screen.findByRole('button', { name: responsibleMember.displayName }),
    ).toHaveAttribute('aria-pressed', 'true')

    fireEvent.change(responsibleModeSelect(), { target: { value: 'unassigned' } })
    fireEvent.click(screen.getByRole('button', { name: 'Salvar responsável' }))

    expect(
      await screen.findByText('Responsável atualizado com sucesso.'),
    ).toBeInTheDocument()
    const [url, init] = fetchMock.mock.calls[5] as [string, RequestInit]
    expect(url).toBe(responsibleUrl(completedAssigned))
    expect(JSON.parse(init.body as string)).toEqual({ responsibleMembershipId: null })
    expect(metadataEntries()).toContainEqual(['Responsável', 'Sem responsável'])
    expect(screen.getByText('Concluído')).toBeInTheDocument()
  })

  it.each([
    ['sem responsável', null, 'unassigned'],
    ['eu', organizationA.membershipId, 'self'],
  ] as const)(
    'DeadlineResponsible_Unchanged_%s_ClosesWithoutRequest',
    async (_, currentResponsible, expectedMode) => {
      const deadline = {
        ...deadlineA,
        responsibleMembershipId: currentResponsible,
        responsibleDisplayName: currentResponsible === null ? null : 'Ana Dona',
      }
      const fetchMock = authenticatedFetch([organizationA], response(200, deadline))
      vi.stubGlobal('fetch', fetchMock)

      renderRoute(detailPath(organizationA, deadline))
      await screen.findByRole('heading', { name: deadline.title })
      fireEvent.click(screen.getByRole('button', { name: 'Alterar responsável' }))
      expect(responsibleModeSelect()).toHaveValue(expectedMode)
      fireEvent.click(screen.getByRole('button', { name: 'Salvar responsável' }))

      expect(screen.queryByLabelText('Novo responsável')).not.toBeInTheDocument()
      expect(
        screen.getByRole('button', { name: 'Alterar responsável' }),
      ).toBeInTheDocument()
      expect(screen.queryByText('Responsável atualizado com sucesso.')).not.toBeInTheDocument()
      expect(fetchMock).toHaveBeenCalledTimes(3)
    },
  )

  it('DeadlineResponsible_RelatedResponsibleUnavailable_ClearsSelectionAndRefetches', async () => {
    const fetchMock = authenticatedFetch(
      [organizationA],
      response(200, deadlineA),
      memberLookupResponse([responsibleMember]),
      response(200, { requestToken: 'test-token' }),
      response(400, {
        title: 'Related responsible member unavailable',
        detail: 'private server reason',
        code: 'related_responsible_unavailable',
      }),
      response(200, deadlineA),
      memberLookupResponse([]),
    )
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(organizationA, deadlineA))
    await screen.findByRole('heading', { name: deadlineA.title })
    fireEvent.click(screen.getByRole('button', { name: 'Alterar responsável' }))
    fireEvent.change(responsibleModeSelect(), { target: { value: 'other' } })
    fireEvent.click(
      await screen.findByRole('button', { name: responsibleMember.displayName }),
    )
    fireEvent.click(screen.getByRole('button', { name: 'Salvar responsável' }))

    expect(
      await screen.findByText(
        'O responsável selecionado não está mais disponível. Escolha outra pessoa.',
      ),
    ).toBeInTheDocument()
    expect(screen.queryByText('private server reason')).not.toBeInTheDocument()
    expect(screen.queryByText(/Responsável selecionado:/)).not.toBeInTheDocument()
    expect(responsibleModeSelect()).toHaveValue('other')
    expect(fetchMock.mock.calls[6]?.[0]).toBe(
      `/api/organizations/${organizationA.id}/deadlines/${deadlineA.id}`,
    )
    expect(await screen.findByText('Não há responsáveis disponíveis.')).toBeInTheDocument()
  })

  it('DeadlineReopen_CurrentResponsibleUnavailable_ExplainsAndRefetches', async () => {
    const completedAssigned = {
      ...deadlineB,
      responsibleMembershipId: responsibleMember.id,
      responsibleDisplayName: responsibleMember.displayName,
    }
    const fetchMock = authenticatedFetch(
      [organizationA],
      response(200, completedAssigned),
      response(200, { requestToken: 'test-token' }),
      response(409, {
        title: 'Resource conflict',
        detail: 'private conflict detail',
      }),
      response(200, completedAssigned),
    )
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(organizationA, completedAssigned))
    await screen.findByRole('heading', { name: completedAssigned.title })
    fireEvent.click(screen.getByRole('button', { name: 'Reabrir' }))

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent(
      'Não foi possível reabrir: o responsável atual está indisponível. Altere o responsável e tente novamente.',
    )
    expect(alert).not.toHaveTextContent('private conflict detail')
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(6))
    expect(fetchMock.mock.calls[5]?.[0]).toBe(
      `/api/organizations/${organizationA.id}/deadlines/${completedAssigned.id}`,
    )
    expect(screen.getByText('Concluído')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Reabrir' })).toBeEnabled()
    expect(screen.getByRole('button', { name: 'Alterar responsável' })).toBeEnabled()
  })
})
