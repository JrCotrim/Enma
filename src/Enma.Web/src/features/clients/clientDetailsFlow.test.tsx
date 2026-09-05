import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

vi.mock('../notifications/NotificationCenter', () => ({
  NotificationCenter: () => null,
}))
import { createAppRoutes } from '../../app/router'
import { clearCsrfToken } from '../authentication/csrfClient'
import { createEmailVerificationFlow } from '../email-verification/emailVerificationService'
import type { OrganizationNavigationItem } from '../organizations/organizationTypes'
import type { Client, ClientDetail } from './clientTypes'

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

const clientA: ClientDetail = {
  id: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
  name: 'Cliente Alfa',
  email: 'cliente.alfa@example.com',
  phone: '22999998888',
  cpf: '52998224725',
  isActive: true,
  createdAt: '2026-08-12T14:30:00Z',
}

const clientB: ClientDetail = {
  id: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',
  name: 'Cliente Beta',
  email: null,
  phone: null,
  cpf: null,
  isActive: false,
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

function clientListResponse(items: readonly Client[]): Response {
  return response(200, {
    items: items.map((client) => ({
      id: client.id,
      name: client.name,
      isActive: client.isActive,
      createdAt: client.createdAt,
    })),
    pageNumber: 1,
    pageSize: 20,
  })
}

function renderRoute(path: string) {
  const router = createMemoryRouter(
    createAppRoutes(createEmailVerificationFlow(undefined)),
    { initialEntries: [path] },
  )

  render(<RouterProvider router={router} />)
  return router
}

function authenticatedDetailFetch(
  organization: OrganizationNavigationItem,
  detailResponse: Response,
) {
  return vi
    .fn()
    .mockResolvedValueOnce(organizationResponse([]))
    .mockResolvedValueOnce(organizationResponse([organization]))
    .mockResolvedValueOnce(detailResponse)
}

function detailPath(
  organization: OrganizationNavigationItem,
  client: Client,
): string {
  return `/organizations/${organization.id}/clients/${client.id}`
}

function openEditAndSubmit(name: string) {
  fireEvent.click(screen.getByRole('button', { name: 'Editar cliente' }))
  const input = screen.getByLabelText('Nome')
  fireEvent.change(input, { target: { value: name } })
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

describe('Clients D2 flow', () => {
  it('ClientDetail_DirectMemberRoute_TargetsTenantResourceAndRendersReadOnlyFields', async () => {
    const member = { ...organizationA, role: 'Member' as const }
    const localStorageSpy = vi.spyOn(window.localStorage, 'setItem')
    const sessionStorageSpy = vi.spyOn(window.sessionStorage, 'setItem')
    const fetchMock = authenticatedDetailFetch(member, response(200, clientA))
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(member, clientA))

    expect(
      await screen.findByRole('heading', { name: clientA.name }),
    ).toBeInTheDocument()
    expect(screen.getByText('Ativo')).toBeInTheDocument()
    expect(screen.getByText(clientA.email!)).toBeInTheDocument()
    expect(screen.getByText(clientA.phone!)).toBeInTheDocument()
    expect(screen.getByText(clientA.cpf!)).toBeInTheDocument()
    expect(screen.getByText(/12\/08\/2026/)).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Voltar para clientes' })).toHaveAttribute(
      'href',
      `/organizations/${member.id}/clients`,
    )
    expect(fetchMock).toHaveBeenNthCalledWith(
      3,
      `/api/organizations/${member.id}/clients/${clientA.id}`,
      {
        method: 'GET',
        cache: 'no-store',
        signal: expect.any(AbortSignal),
        credentials: 'same-origin',
      },
    )
    expect(screen.queryByRole('button', { name: 'Editar cliente' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Desativar cliente' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Reativar cliente' })).not.toBeInTheDocument()
    expect(localStorageSpy).not.toHaveBeenCalled()
    expect(sessionStorageSpy).not.toHaveBeenCalled()
  })

  it.each(['Owner', 'Administrator'] as const)(
    'ClientDetail_%sRole_ShowsStateAppropriateMutationControls',
    async (role) => {
      const organization = { ...organizationA, role }
      vi.stubGlobal(
        'fetch',
        authenticatedDetailFetch(organization, response(200, clientA)),
      )

      renderRoute(detailPath(organization, clientA))

      expect(
        await screen.findByRole('button', { name: 'Editar cliente' }),
      ).toBeInTheDocument()
      expect(screen.getByRole('button', { name: 'Desativar cliente' })).toBeInTheDocument()
      expect(screen.queryByRole('button', { name: 'Reativar cliente' })).not.toBeInTheDocument()
    },
  )

  it.each([true, false])(
    'ClientDetail_MemberLifecycleState%s_HidesAllMutationControls',
    async (isActive) => {
      const member = { ...organizationA, role: 'Member' as const }
      const client = { ...clientA, isActive }
      vi.stubGlobal(
        'fetch',
        authenticatedDetailFetch(member, response(200, client)),
      )

      renderRoute(detailPath(member, client))
      await screen.findByRole('heading', { name: client.name })

      expect(screen.queryByRole('button', { name: 'Editar cliente' })).not.toBeInTheDocument()
      expect(screen.queryByRole('button', { name: 'Desativar cliente' })).not.toBeInTheDocument()
      expect(screen.queryByRole('button', { name: 'Reativar cliente' })).not.toBeInTheDocument()
    },
  )

  it('ClientList_NameLink_NavigatesToCurrentOrganizationDetail', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(organizationResponse([]))
      .mockResolvedValueOnce(organizationResponse([organizationA]))
      .mockResolvedValueOnce(clientListResponse([clientA]))
      .mockResolvedValueOnce(response(200, clientA))
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(`/organizations/${organizationA.id}/clients`)

    fireEvent.click(await screen.findByRole('link', { name: clientA.name }))

    expect(
      await screen.findByRole('heading', { name: clientA.name }),
    ).toBeInTheDocument()
    expect(router.state.location.pathname).toBe(detailPath(organizationA, clientA))
  })

  it('ClientEdit_ValidTrimmedName_SendsExactCsrfBodyAndRefetchesWithoutOptimism', async () => {
    let resolveUpdate: ((value: Response) => void) | undefined
    const pendingUpdate = new Promise<Response>((resolve) => {
      resolveUpdate = resolve
    })
    const updatedClient = { ...clientA, name: 'Novo nome' }
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(organizationResponse([]))
      .mockResolvedValueOnce(organizationResponse([organizationA]))
      .mockResolvedValueOnce(response(200, clientA))
      .mockResolvedValueOnce(response(200, { requestToken: 'test-token' }))
      .mockReturnValueOnce(pendingUpdate)
      .mockResolvedValueOnce(response(200, updatedClient))
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(organizationA, clientA))
    await screen.findByRole('heading', { name: clientA.name })
    const form = openEditAndSubmit('  Novo nome  ')

    expect(await screen.findByRole('button', { name: 'Salvando...' })).toBeDisabled()
    fireEvent.submit(form)
    expect(fetchMock).toHaveBeenCalledTimes(5)
    expect(screen.getByRole('heading', { name: clientA.name })).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: updatedClient.name })).not.toBeInTheDocument()

    const [updateUrl, updateInit] = fetchMock.mock.calls[4] as [string, RequestInit]
    expect(updateUrl).toBe(`/api/organizations/${organizationA.id}/clients/${clientA.id}`)
    expect(updateInit.method).toBe('PUT')
    expect(updateInit.headers).toEqual({
      'Content-Type': 'application/json',
      'X-CSRF-TOKEN': 'test-token',
    })
    expect(JSON.parse(updateInit.body as string)).toEqual({
      name: 'Novo nome',
      email: clientA.email,
      phone: clientA.phone,
      cpf: clientA.cpf,
    })
    expect(Object.keys(JSON.parse(updateInit.body as string))).toEqual([
      'name',
      'email',
      'phone',
      'cpf',
    ])

    await act(async () => {
      resolveUpdate?.(response(204))
      await pendingUpdate
    })

    expect(
      await screen.findByRole('heading', { name: updatedClient.name }),
    ).toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(6)
  })

  it('ClientEdit_ClearedOptionalProfileFields_SendsNullAndRefetchesAuthoritativeDetail', async () => {
    const clearedClient: ClientDetail = {
      ...clientA,
      email: null,
      phone: null,
      cpf: null,
    }

    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(organizationResponse([]))
      .mockResolvedValueOnce(organizationResponse([organizationA]))
      .mockResolvedValueOnce(response(200, clientA))
      .mockResolvedValueOnce(response(200, { requestToken: 'test-token' }))
      .mockResolvedValueOnce(response(204))
      .mockResolvedValueOnce(response(200, clearedClient))

    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(organizationA, clientA))

    await screen.findByRole('heading', { name: clientA.name })

    fireEvent.click(
      screen.getByRole('button', { name: 'Editar cliente' }),
    )

    expect(screen.getByLabelText('E-mail')).toHaveValue(clientA.email)
    expect(screen.getByLabelText('Telefone')).toHaveValue(clientA.phone)
    expect(screen.getByLabelText('CPF')).toHaveValue(clientA.cpf)

    fireEvent.change(screen.getByLabelText('E-mail'), {
      target: { value: '   ' },
    })
    fireEvent.change(screen.getByLabelText('Telefone'), {
      target: { value: '   ' },
    })
    fireEvent.change(screen.getByLabelText('CPF'), {
      target: { value: '   ' },
    })

    const form = screen
      .getByRole('button', { name: 'Salvar alterações' })
      .closest('form')!

    fireEvent.submit(form)

    await waitFor(() => {
      expect(fetchMock).toHaveBeenCalledTimes(6)
    })

    const [updateUrl, updateInit] = fetchMock.mock.calls[4] as [
      string,
      RequestInit,
    ]

    expect(updateUrl).toBe(
      `/api/organizations/${organizationA.id}/clients/${clientA.id}`,
    )
    expect(updateInit.method).toBe('PUT')
    expect(JSON.parse(updateInit.body as string)).toEqual({
      name: clientA.name,
      email: null,
      phone: null,
      cpf: null,
    })

    expect(screen.getAllByText('Não informado')).toHaveLength(3)
  })

  it('ClientEdit_InvalidNames_PreventWhitespaceAndOverlongRequests', async () => {
    const fetchMock = authenticatedDetailFetch(organizationA, response(200, clientA))
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(organizationA, clientA))
    await screen.findByRole('heading', { name: clientA.name })
    fireEvent.click(screen.getByRole('button', { name: 'Editar cliente' }))
    const input = screen.getByLabelText('Nome')
    const form = screen.getByRole('button', { name: 'Salvar alterações' }).closest('form')!

    fireEvent.change(input, { target: { value: '   ' } })
    fireEvent.submit(form)
    expect(await screen.findByText('Informe o nome do cliente.')).toBeInTheDocument()

    fireEvent.change(input, { target: { value: 'x'.repeat(151) } })
    fireEvent.submit(form)
    expect(await screen.findByText('O nome deve ter no máximo 150 caracteres.')).toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(3)
  })

  it('ClientEdit_StaleAdministratorRoleForbidden_KeepsAuthoritativeClientWithoutRetry', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(organizationResponse([]))
      .mockResolvedValueOnce(organizationResponse([organizationB]))
      .mockResolvedValueOnce(response(200, clientA))
      .mockResolvedValueOnce(response(200, { requestToken: 'test-token' }))
      .mockResolvedValueOnce(response(403, { detail: 'private role reason' }))
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(organizationB, clientA))
    await screen.findByRole('heading', { name: clientA.name })
    openEditAndSubmit('Nome sem permissão')

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Você não tem permissão para alterar este cliente.')
    expect(alert).not.toHaveTextContent('private role reason')
    expect(screen.getByRole('heading', { name: clientA.name })).toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(5)
  })

  it('ClientDeactivate_ConfirmedPostsNoBodyWithCsrfAndConvergesFromRefetch', async () => {
    const deactivatedClient = { ...clientA, isActive: false }
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(organizationResponse([]))
      .mockResolvedValueOnce(organizationResponse([organizationA]))
      .mockResolvedValueOnce(response(200, clientA))
      .mockResolvedValueOnce(response(200, { requestToken: 'test-token' }))
      .mockResolvedValueOnce(response(204))
      .mockResolvedValueOnce(response(200, deactivatedClient))
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(organizationA, clientA))
    await screen.findByRole('heading', { name: clientA.name })
    fireEvent.click(screen.getByRole('button', { name: 'Desativar cliente' }))
    expect(screen.getByRole('alertdialog')).toHaveTextContent(
      `Desativar ${clientA.name}? O cliente poderá ser reativado depois.`,
    )
    fireEvent.click(screen.getByRole('button', { name: 'Confirmar desativação' }))

    expect(await screen.findByText('Inativo')).toBeInTheDocument()
    const [url, init] = fetchMock.mock.calls[4] as [string, RequestInit]
    expect(url).toBe(`/api/organizations/${organizationA.id}/clients/${clientA.id}/deactivate`)
    expect(init.method).toBe('POST')
    expect(init.headers).toEqual({ 'X-CSRF-TOKEN': 'test-token' })
    expect(init.body).toBeUndefined()
    expect(screen.getByRole('button', { name: 'Reativar cliente' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Desativar cliente' })).not.toBeInTheDocument()
  })

  it('ClientReactivate_PostsExactEndpointAndConvergesFromRefetch', async () => {
    const reactivatedClient = { ...clientB, isActive: true }
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(organizationResponse([]))
      .mockResolvedValueOnce(organizationResponse([organizationA]))
      .mockResolvedValueOnce(response(200, clientB))
      .mockResolvedValueOnce(response(200, { requestToken: 'test-token' }))
      .mockResolvedValueOnce(response(204))
      .mockResolvedValueOnce(response(200, reactivatedClient))
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(organizationA, clientB))
    await screen.findByText('Inativo')
    fireEvent.click(screen.getByRole('button', { name: 'Reativar cliente' }))

    expect(await screen.findByText('Ativo')).toBeInTheDocument()
    const [url, init] = fetchMock.mock.calls[4] as [string, RequestInit]
    expect(url).toBe(`/api/organizations/${organizationA.id}/clients/${clientB.id}/reactivate`)
    expect(init.method).toBe('POST')
    expect(init.body).toBeUndefined()
    expect(screen.getByRole('button', { name: 'Desativar cliente' })).toBeInTheDocument()
  })

  it('ClientDetail_NotFound_RendersGenericUnavailableStateWithoutResourceLeak', async () => {
    vi.stubGlobal(
      'fetch',
      authenticatedDetailFetch(organizationA, response(404, { detail: 'wrong tenant' })),
    )

    renderRoute(detailPath(organizationA, clientA))

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Cliente não encontrado ou indisponível.')
    expect(alert).not.toHaveTextContent('wrong tenant')
    expect(alert).not.toHaveTextContent(clientA.name)
  })

  it('ClientEdit_NotFound_RemovesPreviouslyRenderedClientData', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(organizationResponse([]))
      .mockResolvedValueOnce(organizationResponse([organizationA]))
      .mockResolvedValueOnce(response(200, clientA))
      .mockResolvedValueOnce(response(200, { requestToken: 'test-token' }))
      .mockResolvedValueOnce(response(404, { detail: 'private resource detail' }))
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(organizationA, clientA))
    await screen.findByRole('heading', { name: clientA.name })
    openEditAndSubmit('Nome indisponível')

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Cliente não encontrado ou indisponível.',
    )
    expect(screen.queryByText(clientA.name)).not.toBeInTheDocument()
    expect(screen.queryByText('private resource detail')).not.toBeInTheDocument()
  })

  it('ClientDetail_NetworkFailure_RetriesCurrentTenantResourceAndRecovers', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(organizationResponse([]))
      .mockResolvedValueOnce(organizationResponse([organizationA]))
      .mockRejectedValueOnce(new Error('private network detail'))
      .mockResolvedValueOnce(response(200, clientA))
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(organizationA, clientA))

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent(
      'Não foi possível carregar o cliente. Tente novamente.',
    )
    expect(alert).not.toHaveTextContent('private network detail')
    fireEvent.click(screen.getByRole('button', { name: 'Tentar novamente' }))

    expect(
      await screen.findByRole('heading', { name: clientA.name }),
    ).toBeInTheDocument()
    expect(fetchMock.mock.calls[3]?.[0]).toBe(
      `/api/organizations/${organizationA.id}/clients/${clientA.id}`,
    )
  })

  it('ClientDetail_MalformedClientId_DoesNotIssueClientRequest', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(organizationResponse([]))
      .mockResolvedValueOnce(organizationResponse([organizationA]))
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(`/organizations/${organizationA.id}/clients/not-a-guid`)

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Cliente não encontrado ou indisponível.',
    )
    expect(fetchMock).toHaveBeenCalledTimes(2)
  })

  it('ClientDetail_OldResourceResponseCompletesLast_RemainsOnCurrentClient', async () => {
    let resolveClientA: ((value: Response) => void) | undefined
    const pendingClientA = new Promise<Response>((resolve) => {
      resolveClientA = resolve
    })
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(organizationResponse([]))
      .mockResolvedValueOnce(organizationResponse([organizationA]))
      .mockReturnValueOnce(pendingClientA)
      .mockResolvedValueOnce(response(200, clientB))
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(detailPath(organizationA, clientA))

    await screen.findByText('Carregando cliente...')
    await act(async () => router.navigate(detailPath(organizationA, clientB)))
    expect(await screen.findByRole('heading', { name: clientB.name })).toBeInTheDocument()

    await act(async () => {
      resolveClientA?.(response(200, clientA))
      await pendingClientA
    })

    expect(screen.getByRole('heading', { name: clientB.name })).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: clientA.name })).not.toBeInTheDocument()
  })

  it('ClientDetail_OldOrganizationResponseCompletesLast_NeverRendersAcrossTenantContext', async () => {
    let resolveClientA: ((value: Response) => void) | undefined
    const pendingClientA = new Promise<Response>((resolve) => {
      resolveClientA = resolve
    })
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(organizationResponse([]))
      .mockResolvedValueOnce(organizationResponse([organizationA, organizationB]))
      .mockReturnValueOnce(pendingClientA)
      .mockResolvedValueOnce(response(200, clientB))
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(detailPath(organizationA, clientA))

    await screen.findByText('Carregando cliente...')
    await act(async () => router.navigate(detailPath(organizationB, clientB)))
    expect(await screen.findByRole('heading', { name: clientB.name })).toBeInTheDocument()

    await act(async () => {
      resolveClientA?.(response(200, clientA))
      await pendingClientA
    })

    expect(screen.getByRole('heading', { name: clientB.name })).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: clientA.name })).not.toBeInTheDocument()
  })

  it('ClientEdit_LateSuccessAfterNavigation_DoesNotRefetchOrOverwriteCurrentResource', async () => {
    let resolveUpdate: ((value: Response) => void) | undefined
    const pendingUpdate = new Promise<Response>((resolve) => {
      resolveUpdate = resolve
    })
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(organizationResponse([]))
      .mockResolvedValueOnce(organizationResponse([organizationA]))
      .mockResolvedValueOnce(response(200, clientA))
      .mockResolvedValueOnce(response(200, { requestToken: 'test-token' }))
      .mockReturnValueOnce(pendingUpdate)
      .mockResolvedValueOnce(response(200, clientB))
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(detailPath(organizationA, clientA))

    await screen.findByRole('heading', { name: clientA.name })
    openEditAndSubmit('Nome atrasado')
    await screen.findByRole('button', { name: 'Salvando...' })
    await act(async () => router.navigate(detailPath(organizationA, clientB)))
    expect(await screen.findByRole('heading', { name: clientB.name })).toBeInTheDocument()

    await act(async () => {
      resolveUpdate?.(response(204))
      await pendingUpdate
    })

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(6))
    expect(screen.getByRole('heading', { name: clientB.name })).toBeInTheDocument()
    expect(screen.queryByText('Nome atrasado')).not.toBeInTheDocument()
  })

  it('OrganizationSwitcher_FromClientDetail_NavigatesToSelectedWorkspaceRoot', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(organizationResponse([]))
      .mockResolvedValueOnce(organizationResponse([organizationA, organizationB]))
      .mockResolvedValueOnce(response(200, clientA))
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(detailPath(organizationA, clientA))

    await screen.findByRole('heading', { name: clientA.name })
    fireEvent.click(screen.getByRole('tab', { name: 'Perfil' }))
    fireEvent.click(
      await screen.findByRole('button', {
        name: `Trocar para ${organizationB.name}`,
      }),
    )

    await waitFor(() => {
      expect(router.state.location.pathname).toBe(`/organizations/${organizationB.id}`)
    })
    expect(
      screen.getByRole('heading', {
        name: `Espaço de trabalho: ${organizationB.name}`,
      }),
    ).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: clientA.name })).not.toBeInTheDocument()
  })
})
