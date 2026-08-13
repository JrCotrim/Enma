import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createAppRoutes } from '../../app/router'
import { clearCsrfToken } from '../authentication/csrfClient'
import { createEmailVerificationFlow } from '../email-verification/emailVerificationService'
import type { OrganizationNavigationItem } from '../organizations/organizationTypes'
import type { LegalProcess } from './legalProcessTypes'

const organizationA: OrganizationNavigationItem = {
  id: '11111111-1111-4111-8111-111111111111',
  name: 'Organização Alfa',
  role: 'Owner',
}

const organizationB: OrganizationNavigationItem = {
  id: '22222222-2222-4222-8222-222222222222',
  name: 'Organização Beta',
  role: 'Administrator',
}

const processA: LegalProcess = {
  id: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
  title: 'Ação de cobrança',
  clientId: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc',
  clientName: 'Cliente inativo permanece relacionado',
  createdAt: '2026-08-12T14:30:00Z',
}

const processB: LegalProcess = {
  id: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',
  title: 'Revisional de contrato',
  clientId: 'dddddddd-dddd-4ddd-8ddd-dddddddddddd',
  clientName: 'Cliente Beta',
  createdAt: '2026-08-11T12:00:00Z',
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

function processListResponse(items: readonly LegalProcess[]): Response {
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
  legalProcess: LegalProcess,
): string {
  return `/organizations/${organization.id}/processes/${legalProcess.id}`
}

function renderRoute(path: string) {
  const router = createMemoryRouter(
    createAppRoutes(createEmailVerificationFlow(undefined)),
    { initialEntries: [path] },
  )

  render(<RouterProvider router={router} />)
  return router
}

function openEditAndSubmit(title: string) {
  fireEvent.click(screen.getByRole('button', { name: 'Editar processo' }))
  const input = screen.getByLabelText('Título')
  fireEvent.change(input, { target: { value: title } })
  const form = screen
    .getByRole('button', { name: 'Salvar alterações' })
    .closest('form')!
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

describe('Processes D2 flow', () => {
  it('ProcessDetail_MemberRoute_TargetsContextAndRendersReadOnlyDisplayFields', async () => {
    const member = { ...organizationA, role: 'Member' as const }
    const localStorageSpy = vi.spyOn(window.localStorage, 'setItem')
    const sessionStorageSpy = vi.spyOn(window.sessionStorage, 'setItem')
    const fetchMock = authenticatedFetch([member], response(200, processA))
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(member, processA))

    expect(
      await screen.findByRole('heading', { name: processA.title }),
    ).toBeInTheDocument()
    expect(screen.getByText(processA.clientName)).toBeInTheDocument()
    expect(screen.getByText(/12\/08\/2026/)).toBeInTheDocument()
    expect(screen.queryByText(processA.id)).not.toBeInTheDocument()
    expect(screen.queryByText(processA.clientId)).not.toBeInTheDocument()
    expect(
      screen.queryByRole('button', { name: 'Editar processo' }),
    ).not.toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Voltar para processos' })).toHaveAttribute(
      'href',
      `/organizations/${member.id}/processes`,
    )
    expect(fetchMock).toHaveBeenNthCalledWith(
      3,
      `/api/organizations/${member.id}/processes/${processA.id}`,
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
    'ProcessDetail_%sRole_ShowsTitleEditControl',
    async (role) => {
      const organization = { ...organizationA, role }
      vi.stubGlobal(
        'fetch',
        authenticatedFetch([organization], response(200, processA)),
      )

      renderRoute(detailPath(organization, processA))

      expect(
        await screen.findByRole('button', { name: 'Editar processo' }),
      ).toBeInTheDocument()
    },
  )

  it('ProcessList_TitleLink_NavigatesToCurrentOrganizationDetail', async () => {
    const fetchMock = authenticatedFetch(
      [organizationA],
      processListResponse([processA]),
      response(200, processA),
    )
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(`/organizations/${organizationA.id}/processes`)

    fireEvent.click(await screen.findByRole('link', { name: processA.title }))

    expect(
      await screen.findByRole('heading', { name: processA.title }),
    ).toBeInTheDocument()
    expect(router.state.location.pathname).toBe(
      detailPath(organizationA, processA),
    )
  })

  it('ProcessDetail_MalformedProcessId_DoesNotIssueBusinessRequest', async () => {
    const fetchMock = authenticatedFetch([organizationA])
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(`/organizations/${organizationA.id}/processes/not-a-guid`)

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Processo não encontrado ou indisponível.',
    )
    expect(fetchMock).toHaveBeenCalledTimes(2)
  })

  it.each([
    [404, 'Processo não encontrado ou indisponível.'],
    [403, 'Não foi possível acessar este processo.'],
  ] as const)(
    'ProcessDetail_Status%s_ShowsSafeStateWithoutServerDetail',
    async (status, expectedMessage) => {
      vi.stubGlobal(
        'fetch',
        authenticatedFetch(
          [organizationA],
          response(status, { detail: 'private tenant information' }),
        ),
      )

      renderRoute(detailPath(organizationA, processA))

      const alert = await screen.findByRole('alert')
      expect(alert).toHaveTextContent(expectedMessage)
      expect(alert).not.toHaveTextContent('private tenant information')
    },
  )

  it('ProcessDetail_MalformedResponse_ShowsGenericSafeError', async () => {
    vi.stubGlobal(
      'fetch',
      authenticatedFetch(
        [organizationA],
        response(200, { ...processA, clientName: 42 }),
      ),
    )

    renderRoute(detailPath(organizationA, processA))

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Não foi possível carregar o processo. Tente novamente.',
    )
    expect(screen.queryByText(processA.title)).not.toBeInTheDocument()
  })

  it('ProcessDetail_Unauthorized_InvalidatesSessionAndRemovesProtectedDetail', async () => {
    vi.stubGlobal(
      'fetch',
      authenticatedFetch([organizationA], response(401)),
    )

    renderRoute(detailPath(organizationA, processA))

    expect(
      await screen.findByRole('heading', { name: 'Entrar no ENMA' }),
    ).toBeInTheDocument()
    expect(screen.queryByText(processA.title)).not.toBeInTheDocument()
  })

  it('ProcessDetail_NetworkFailure_RetriesOnlyCurrentContext', async () => {
    const fetchMock = authenticatedFetch([organizationA])
      .mockRejectedValueOnce(new Error('private network detail'))
      .mockResolvedValueOnce(response(200, processA))
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(organizationA, processA))

    const alert = await screen.findByRole('alert')
    expect(alert).not.toHaveTextContent('private network detail')
    fireEvent.click(screen.getByRole('button', { name: 'Tentar novamente' }))

    expect(
      await screen.findByRole('heading', { name: processA.title }),
    ).toBeInTheDocument()
    expect(fetchMock.mock.calls[3]?.[0]).toBe(
      `/api/organizations/${organizationA.id}/processes/${processA.id}`,
    )
  })

  it('ProcessEdit_TrimmedTitle_SendsExactCsrfBodyWithoutOptimismAndUsesRefetch', async () => {
    let resolveUpdate: ((value: Response) => void) | undefined
    const pendingUpdate = new Promise<Response>((resolve) => {
      resolveUpdate = resolve
    })
    const normalizedProcess = { ...processA, title: 'Novo título normalizado' }
    const fetchMock = authenticatedFetch(
      [organizationA],
      response(200, processA),
      response(200, { requestToken: 'test-token' }),
      pendingUpdate,
      response(200, normalizedProcess),
    )
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(organizationA, processA))
    await screen.findByRole('heading', { name: processA.title })
    const form = openEditAndSubmit('  Novo título normalizado  ')

    expect(await screen.findByRole('button', { name: 'Salvando...' })).toBeDisabled()
    fireEvent.submit(form)
    expect(fetchMock).toHaveBeenCalledTimes(5)
    expect(screen.getByRole('heading', { name: processA.title })).toBeInTheDocument()
    expect(
      screen.queryByRole('heading', { name: normalizedProcess.title }),
    ).not.toBeInTheDocument()

    const [url, init] = fetchMock.mock.calls[4] as [string, RequestInit]
    expect(url).toBe(
      `/api/organizations/${organizationA.id}/processes/${processA.id}`,
    )
    expect(init.method).toBe('PUT')
    expect(init.headers).toEqual({
      'Content-Type': 'application/json',
      'X-CSRF-TOKEN': 'test-token',
    })
    expect(JSON.parse(init.body as string)).toEqual({
      title: normalizedProcess.title,
    })
    expect(Object.keys(JSON.parse(init.body as string))).toEqual(['title'])

    await act(async () => {
      resolveUpdate?.(response(204))
      await pendingUpdate
    })

    expect(
      await screen.findByRole('heading', { name: normalizedProcess.title }),
    ).toBeInTheDocument()
    expect(fetchMock.mock.calls[5]?.[0]).toBe(url)
    expect(fetchMock.mock.calls[5]?.[1]?.method).toBe('GET')
  })

  it('ProcessEdit_InvalidTitlesRejectWhitespaceAndOverlongButAccept150Characters', async () => {
    const acceptedTitle = 'x'.repeat(150)
    const fetchMock = authenticatedFetch(
      [organizationA],
      response(200, processA),
      response(200, { requestToken: 'test-token' }),
      response(204),
      response(200, { ...processA, title: acceptedTitle }),
    )
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(organizationA, processA))
    await screen.findByRole('heading', { name: processA.title })
    fireEvent.click(screen.getByRole('button', { name: 'Editar processo' }))
    const input = screen.getByLabelText('Título')
    const form = screen
      .getByRole('button', { name: 'Salvar alterações' })
      .closest('form')!

    fireEvent.change(input, { target: { value: '   ' } })
    fireEvent.submit(form)
    expect(await screen.findByText('Informe o título do processo.')).toBeInTheDocument()

    fireEvent.change(input, { target: { value: 'x'.repeat(151) } })
    fireEvent.submit(form)
    expect(
      await screen.findByText('O título deve ter no máximo 150 caracteres.'),
    ).toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(3)

    fireEvent.change(input, { target: { value: acceptedTitle } })
    fireEvent.submit(form)

    expect(
      await screen.findByRole('heading', { name: acceptedTitle }),
    ).toBeInTheDocument()
    expect(JSON.parse(fetchMock.mock.calls[4]?.[1]?.body as string)).toEqual({
      title: acceptedTitle,
    })
  })

  it.each([
    [403, 'Você não tem permissão para alterar este processo.'],
    [400, 'Não foi possível validar a alteração.'],
  ] as const)(
    'ProcessEdit_Status%s_KeepsAuthoritativeDetailWithoutRetry',
    async (status, expectedMessage) => {
      const fetchMock = authenticatedFetch(
        [organizationB],
        response(200, processA),
        response(200, { requestToken: 'test-token' }),
        response(status, { detail: 'private mutation detail' }),
      )
      vi.stubGlobal('fetch', fetchMock)

      renderRoute(detailPath(organizationB, processA))
      await screen.findByRole('heading', { name: processA.title })
      openEditAndSubmit('Título sem autorização atual')

      const alert = await screen.findByRole('alert')
      expect(alert).toHaveTextContent(expectedMessage)
      expect(alert).not.toHaveTextContent('private mutation detail')
      expect(screen.getByRole('heading', { name: processA.title })).toBeInTheDocument()
      expect(fetchMock).toHaveBeenCalledTimes(5)
    },
  )

  it('ProcessEdit_NotFound_RemovesPreviouslyRenderedResourceWithoutTenantInference', async () => {
    const fetchMock = authenticatedFetch(
      [organizationA],
      response(200, processA),
      response(200, { requestToken: 'test-token' }),
      response(404, { detail: 'cross tenant' }),
    )
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(organizationA, processA))
    await screen.findByRole('heading', { name: processA.title })
    openEditAndSubmit('Título indisponível')

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Processo não encontrado ou indisponível.')
    expect(alert).not.toHaveTextContent('cross tenant')
    expect(screen.queryByText(processA.title)).not.toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(5)
  })

  it('ProcessEdit_NetworkFailure_ShowsUncertainStateWithoutRetryOrFakeTitle', async () => {
    const fetchMock = authenticatedFetch(
      [organizationA],
      response(200, processA),
      response(200, { requestToken: 'test-token' }),
    ).mockRejectedValueOnce(new Error('private connection detail'))
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(organizationA, processA))
    await screen.findByRole('heading', { name: processA.title })
    openEditAndSubmit('Resultado incerto')

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Atualize os dados antes de tentar novamente.')
    expect(alert).not.toHaveTextContent('private connection detail')
    expect(screen.getByRole('heading', { name: processA.title })).toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(5)
  })

  it('ProcessDetail_OldProcessResponseCompletesLast_RemainsOnCurrentProcess', async () => {
    let resolveProcessA: ((value: Response) => void) | undefined
    const pendingProcessA = new Promise<Response>((resolve) => {
      resolveProcessA = resolve
    })
    const fetchMock = authenticatedFetch(
      [organizationA],
      pendingProcessA,
      response(200, processB),
    )
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(detailPath(organizationA, processA))

    await screen.findByText('Carregando processo...')
    await act(async () => router.navigate(detailPath(organizationA, processB)))
    expect(
      await screen.findByRole('heading', { name: processB.title }),
    ).toBeInTheDocument()

    await act(async () => {
      resolveProcessA?.(response(200, processA))
      await pendingProcessA
    })

    expect(screen.getByRole('heading', { name: processB.title })).toBeInTheDocument()
    expect(
      screen.queryByRole('heading', { name: processA.title }),
    ).not.toBeInTheDocument()
  })

  it('ProcessDetail_OldOrganizationResponseCompletesLast_NeverRendersAcrossTenant', async () => {
    let resolveProcessA: ((value: Response) => void) | undefined
    const pendingProcessA = new Promise<Response>((resolve) => {
      resolveProcessA = resolve
    })
    const fetchMock = authenticatedFetch(
      [organizationA, organizationB],
      pendingProcessA,
      response(200, processB),
    )
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(detailPath(organizationA, processA))

    await screen.findByText('Carregando processo...')
    await act(async () => router.navigate(detailPath(organizationB, processB)))
    expect(
      await screen.findByRole('heading', { name: processB.title }),
    ).toBeInTheDocument()

    await act(async () => {
      resolveProcessA?.(response(200, processA))
      await pendingProcessA
    })

    expect(screen.getByRole('heading', { name: processB.title })).toBeInTheDocument()
    expect(
      screen.queryByRole('heading', { name: processA.title }),
    ).not.toBeInTheDocument()
  })

  it('ProcessDetail_SameIdAcrossOrganizations_UsesFullContextIdentity', async () => {
    let resolveOrganizationA: ((value: Response) => void) | undefined
    const pendingOrganizationA = new Promise<Response>((resolve) => {
      resolveOrganizationA = resolve
    })
    const sameIdProcessB = { ...processB, id: processA.id }
    const fetchMock = authenticatedFetch(
      [organizationA, organizationB],
      pendingOrganizationA,
      response(200, sameIdProcessB),
    )
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(detailPath(organizationA, processA))

    await screen.findByText('Carregando processo...')
    await act(async () =>
      router.navigate(detailPath(organizationB, sameIdProcessB)),
    )
    expect(
      await screen.findByRole('heading', { name: sameIdProcessB.title }),
    ).toBeInTheDocument()

    await act(async () => {
      resolveOrganizationA?.(response(200, processA))
      await pendingOrganizationA
    })

    expect(
      screen.getByRole('heading', { name: sameIdProcessB.title }),
    ).toBeInTheDocument()
    expect(
      screen.queryByRole('heading', { name: processA.title }),
    ).not.toBeInTheDocument()
  })

  it.each([
    ['same organization', organizationA, processB],
    ['new organization', organizationB, processB],
  ] as const)(
    'ProcessEdit_LatePutAfterNavigationTo%s_DoesNotAffectCurrentProcess',
    async (_scenario, targetOrganization, targetProcess) => {
      let resolveUpdate: ((value: Response) => void) | undefined
      const pendingUpdate = new Promise<Response>((resolve) => {
        resolveUpdate = resolve
      })
      const organizations =
        targetOrganization.id === organizationA.id
          ? [organizationA]
          : [organizationA, organizationB]
      const fetchMock = authenticatedFetch(
        organizations,
        response(200, processA),
        response(200, { requestToken: 'test-token' }),
        pendingUpdate,
        response(200, targetProcess),
      )
      vi.stubGlobal('fetch', fetchMock)
      const router = renderRoute(detailPath(organizationA, processA))

      await screen.findByRole('heading', { name: processA.title })
      openEditAndSubmit('Título atrasado')
      await screen.findByRole('button', { name: 'Salvando...' })
      await act(async () =>
        router.navigate(detailPath(targetOrganization, targetProcess)),
      )
      expect(
        await screen.findByRole('heading', { name: targetProcess.title }),
      ).toBeInTheDocument()

      await act(async () => {
        resolveUpdate?.(response(204))
        await pendingUpdate
      })

      await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(6))
      expect(
        screen.getByRole('heading', { name: targetProcess.title }),
      ).toBeInTheDocument()
      expect(screen.queryByText('Título atrasado')).not.toBeInTheDocument()
      expect(
        screen.queryByText('Processo atualizado com sucesso.'),
      ).not.toBeInTheDocument()
    },
  )

  it('ProcessEdit_AuthoritativeRefetchCompletesAfterNavigation_DoesNotOverwriteCurrentProcess', async () => {
    let resolveRefetchA: ((value: Response) => void) | undefined
    const pendingRefetchA = new Promise<Response>((resolve) => {
      resolveRefetchA = resolve
    })
    const updatedProcessA = { ...processA, title: 'Título confirmado em A' }
    const fetchMock = authenticatedFetch(
      [organizationA],
      response(200, processA),
      response(200, { requestToken: 'test-token' }),
      response(204),
      pendingRefetchA,
      response(200, processB),
    )
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(detailPath(organizationA, processA))

    await screen.findByRole('heading', { name: processA.title })
    openEditAndSubmit(updatedProcessA.title)
    await screen.findByText('Carregando processo...')
    await act(async () => router.navigate(detailPath(organizationA, processB)))
    expect(
      await screen.findByRole('heading', { name: processB.title }),
    ).toBeInTheDocument()

    await act(async () => {
      resolveRefetchA?.(response(200, updatedProcessA))
      await pendingRefetchA
    })

    expect(screen.getByRole('heading', { name: processB.title })).toBeInTheDocument()
    expect(screen.queryByText(updatedProcessA.title)).not.toBeInTheDocument()
  })

  it('ProcessEdit_NavigationResetsDraftAndDoesNotRequireClientLookup', async () => {
    const fetchMock = authenticatedFetch(
      [organizationA],
      response(200, processA),
      response(200, processB),
    )
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(detailPath(organizationA, processA))

    await screen.findByRole('heading', { name: processA.title })
    fireEvent.click(screen.getByRole('button', { name: 'Editar processo' }))
    fireEvent.change(screen.getByLabelText('Título'), {
      target: { value: 'Rascunho exclusivo de A' },
    })

    await act(async () => router.navigate(detailPath(organizationA, processB)))
    expect(
      await screen.findByRole('heading', { name: processB.title }),
    ).toBeInTheDocument()
    expect(screen.queryByDisplayValue('Rascunho exclusivo de A')).not.toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'Editar processo' }))
    expect(screen.getByLabelText('Título')).toHaveValue(processB.title)
    expect(screen.getByText(processB.clientName)).toBeInTheDocument()
    expect(
      fetchMock.mock.calls.some(([url]) =>
        String(url).includes('/clients/lookup'),
      ),
    ).toBe(false)
  })
})
