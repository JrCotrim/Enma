import { act, fireEvent, render, screen } from '@testing-library/react'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createAppRoutes } from '../../app/router'
import { clearCsrfToken } from '../authentication/csrfClient'
import { createEmailVerificationFlow } from '../email-verification/emailVerificationService'
import type { OrganizationNavigationItem } from './organizationTypes'

const organizationA: OrganizationNavigationItem = {
  id: '11111111-1111-4111-8111-111111111111',
  membershipId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa1',
  name: 'Organização Alfa',
  role: 'Member',
}

const organizationB: OrganizationNavigationItem = {
  id: '22222222-2222-4222-8222-222222222222',
  membershipId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbb2',
  name: 'Organização Beta',
  role: 'Owner',
}

function response(status: number, body?: unknown): Response {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
  })
}

function organizationResponse(
  items: readonly OrganizationNavigationItem[],
): Response {
  return response(200, { items })
}

function renderRoute(path: string) {
  const router = createMemoryRouter(
    createAppRoutes(createEmailVerificationFlow(undefined)),
    { initialEntries: [path] },
  )

  render(<RouterProvider router={router} />)
  return router
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

describe('organization discovery and routing', () => {
  it('Organizations_AuthenticatedDiscovery_RendersNamesRolesWithoutPersistenceOrCsrf', async () => {
    const localStorageSpy = vi.spyOn(window.localStorage, 'setItem')
    const sessionStorageSpy = vi.spyOn(window.sessionStorage, 'setItem')
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(organizationResponse([]))
      .mockResolvedValueOnce(organizationResponse([organizationA, organizationB]))
    vi.stubGlobal('fetch', fetchMock)

    renderRoute('/organizations')

    expect(
      await screen.findByRole('heading', { name: 'Suas organizações' }),
    ).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: organizationA.name })).toBeInTheDocument()
    expect(screen.getByText('Membro')).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: organizationB.name })).toBeInTheDocument()
    expect(screen.getByText('Proprietário')).toBeInTheDocument()
    expect(fetchMock).toHaveBeenNthCalledWith(2, '/api/me/organizations', {
      method: 'GET',
      cache: 'no-store',
      signal: expect.any(AbortSignal),
      credentials: 'same-origin',
    })
    expect(fetchMock.mock.calls[1]?.[1]).not.toHaveProperty('headers')
    expect(localStorageSpy).not.toHaveBeenCalled()
    expect(sessionStorageSpy).not.toHaveBeenCalled()
  })

  it('Organizations_EmptyDiscovery_ShowsAuthenticatedEmptyState', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(organizationResponse([]))))

    renderRoute('/organizations')

    expect(
      await screen.findByRole('heading', { name: 'Nenhuma organização disponível' }),
    ).toBeInTheDocument()
    expect(screen.queryByLabelText('Senha')).not.toBeInTheDocument()
  })

  it('Organizations_DiscoveryFailure_ShowsSafeRetryAndRecovers', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(organizationResponse([]))
      .mockResolvedValueOnce(response(500, { detail: 'private server detail' }))
      .mockResolvedValueOnce(organizationResponse([organizationA]))
    vi.stubGlobal('fetch', fetchMock)

    renderRoute('/organizations')

    expect(
      await screen.findByRole('heading', {
        name: 'Não foi possível carregar suas organizações',
      }),
    ).toBeInTheDocument()
    expect(screen.queryByText('private server detail')).not.toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'Tentar novamente' }))

    expect(
      await screen.findByRole('heading', { name: organizationA.name }),
    ).toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(3)
  })

  it('Organizations_DiscoveryUnauthorized_InvalidatesSessionAndShowsLogin', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(organizationResponse([]))
      .mockResolvedValueOnce(response(401))
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute('/organizations')

    expect(
      await screen.findByRole('heading', { name: 'Entrar no ENMA' }),
    ).toBeInTheDocument()
    expect(router.state.location.pathname).toBe('/login')
    expect(fetchMock).toHaveBeenCalledTimes(2)
  })

  it.each([
    [organizationA, 'Membro'],
    [organizationB, 'Proprietário'],
  ])(
    'OrganizationRoute_DirectNavigation_ResolvesExactContextFor%s',
    async (organization, roleLabel) => {
      vi.stubGlobal(
        'fetch',
        vi
          .fn()
          .mockResolvedValueOnce(organizationResponse([]))
          .mockResolvedValueOnce(
            organizationResponse([organizationA, organizationB]),
          ),
      )

      renderRoute(`/organizations/${organization.id}`)

      expect(
        await screen.findByRole('heading', { name: organization.name }),
      ).toBeInTheDocument()
      expect(screen.getByText(`Seu papel: ${roleLabel}`)).toBeInTheDocument()
      expect(screen.getByLabelText('Organização atual')).toHaveValue(organization.id)
    },
  )

  it('OrganizationSwitcher_ChooseAnotherOrganization_NavigatesUrlAndUpdatesContext', async () => {
    vi.stubGlobal(
      'fetch',
      vi
        .fn()
        .mockResolvedValueOnce(organizationResponse([]))
        .mockResolvedValueOnce(organizationResponse([organizationA, organizationB])),
    )
    const router = renderRoute(`/organizations/${organizationA.id}`)

    await screen.findByRole('heading', { name: organizationA.name })
    fireEvent.change(screen.getByLabelText('Organização atual'), {
      target: { value: organizationB.id },
    })

    expect(
      await screen.findByRole('heading', { name: organizationB.name }),
    ).toBeInTheDocument()
    expect(screen.getByText('Seu papel: Proprietário')).toBeInTheDocument()
    expect(router.state.location.pathname).toBe(`/organizations/${organizationB.id}`)
  })

  it('OrganizationRoute_UnknownOrganization_ShowsGenericUnavailableState', async () => {
    const unknownId = '33333333-3333-4333-8333-333333333333'
    vi.stubGlobal(
      'fetch',
      vi
        .fn()
        .mockResolvedValueOnce(organizationResponse([]))
        .mockResolvedValueOnce(organizationResponse([organizationA])),
    )

    renderRoute(`/organizations/${unknownId}`)

    expect(
      await screen.findByRole('heading', { name: 'Organização indisponível' }),
    ).toBeInTheDocument()
    expect(screen.queryByText(organizationA.name)).not.toBeInTheDocument()
    expect(
      screen.getByRole('link', { name: 'Voltar para organizações' }),
    ).toHaveAttribute('href', '/organizations')
  })

  it('OrganizationRoute_MalformedOrganizationId_FailsSafelyWithoutScopedRequest', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(organizationResponse([]))
      .mockResolvedValueOnce(organizationResponse([organizationA]))
    vi.stubGlobal('fetch', fetchMock)

    renderRoute('/organizations/not-a-guid')

    expect(
      await screen.findByRole('heading', { name: 'Organização indisponível' }),
    ).toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(2)
    expect(
      fetchMock.mock.calls.every(([input]) => input === '/api/me/organizations'),
    ).toBe(true)
  })

  it('OrganizationRefresh_CurrentMembershipDisappears_StopsRenderingWorkspaceWithoutSelectingAnother', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(organizationResponse([]))
      .mockResolvedValueOnce(organizationResponse([organizationA, organizationB]))
      .mockResolvedValueOnce(organizationResponse([organizationB]))
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(`/organizations/${organizationA.id}`)

    await screen.findByRole('heading', { name: organizationA.name })
    fireEvent.click(
      screen.getByRole('button', { name: 'Atualizar organizações' }),
    )

    expect(
      await screen.findByRole('heading', { name: 'Organização indisponível' }),
    ).toBeInTheDocument()
    expect(screen.queryByText(organizationA.name)).not.toBeInTheDocument()
    expect(router.state.location.pathname).toBe(`/organizations/${organizationA.id}`)
  })

  it('OrganizationRefresh_CurrentRoleChanges_UpdatesUxWithoutRelogin', async () => {
    const updatedOrganizationA: OrganizationNavigationItem = {
      ...organizationA,
      role: 'Administrator',
    }
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(organizationResponse([]))
      .mockResolvedValueOnce(organizationResponse([organizationA]))
      .mockResolvedValueOnce(organizationResponse([updatedOrganizationA]))
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(`/organizations/${organizationA.id}`)

    expect(await screen.findByText('Seu papel: Membro')).toBeInTheDocument()
    fireEvent.click(
      screen.getByRole('button', { name: 'Atualizar organizações' }),
    )

    expect(
      await screen.findByText('Seu papel: Administrador'),
    ).toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(3)
  })

  it('OrganizationWorkspace_Logout_UnmountsOrganizationDataAndUsesAuthoritativeFlow', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(organizationResponse([]))
      .mockResolvedValueOnce(organizationResponse([organizationA]))
      .mockResolvedValueOnce(response(200, { requestToken: 'transient-token' }))
      .mockResolvedValueOnce(response(204))
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(`/organizations/${organizationA.id}`)

    await screen.findByRole('heading', { name: organizationA.name })
    fireEvent.click(screen.getByRole('button', { name: 'Sair' }))

    expect(
      await screen.findByRole('heading', { name: 'Entrar no ENMA' }),
    ).toBeInTheDocument()
    expect(screen.queryByText(organizationA.name)).not.toBeInTheDocument()
    expect(router.state.location.pathname).toBe('/login')
    expect(fetchMock).toHaveBeenNthCalledWith(4, '/api/auth/logout', {
      method: 'POST',
      headers: { 'X-CSRF-TOKEN': 'transient-token' },
      cache: 'no-store',
      credentials: 'same-origin',
    })
  })

  it('OrganizationDiscovery_UnmountedBeforeOldResponseCompletes_DoesNotRestoreStaleUi', async () => {
    let resolveDiscovery: ((value: Response) => void) | undefined
    const pendingDiscovery = new Promise<Response>((resolve) => {
      resolveDiscovery = resolve
    })
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(organizationResponse([]))
      .mockReturnValueOnce(pendingDiscovery)
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute('/organizations')

    expect(
      await screen.findByRole('heading', { name: 'Carregando organizações...' }),
    ).toBeInTheDocument()

    await act(async () => {
      await router.navigate('/')
    })

    await act(async () => {
      resolveDiscovery?.(organizationResponse([organizationA]))
      await pendingDiscovery
    })

    expect(
      screen.getByRole('heading', { name: 'Welcome to ENMA' }),
    ).toBeInTheDocument()
    expect(screen.queryByText(organizationA.name)).not.toBeInTheDocument()
  })
})
