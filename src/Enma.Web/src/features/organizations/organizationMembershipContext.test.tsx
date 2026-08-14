import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { useState } from 'react'
import { createMemoryRouter, Outlet, RouterProvider } from 'react-router-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  AuthContext,
  type AuthContextValue,
} from '../authentication/AuthContext'
import {
  OrganizationDiscoveryContext,
  useCurrentOrganization,
} from './OrganizationContext'
import { OrganizationRoute } from './OrganizationRoute'
import { getCurrentUserOrganizations } from './organizationService'
import type { OrganizationNavigationItem } from './organizationTypes'

const organizationA: OrganizationNavigationItem = {
  id: '11111111-1111-4111-8111-111111111111',
  name: 'Organization Alpha',
  role: 'Member',
  membershipId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa1',
}

const organizationB: OrganizationNavigationItem = {
  id: '22222222-2222-4222-8222-222222222222',
  name: 'Organization Beta',
  role: 'Owner',
  membershipId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbb2',
}

const authContextValue: AuthContextValue = {
  state: 'authenticated',
  login: async () => 'failure',
  logout: async () => undefined,
  retrySessionCheck: () => undefined,
  handleUnauthorized: () => undefined,
}

function response(items: readonly OrganizationNavigationItem[]): Response {
  return new Response(JSON.stringify({ items }), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  })
}

function CurrentOrganizationProbe() {
  const { currentOrganization } = useCurrentOrganization()

  return (
    <output data-testid="current-organization-identity">
      {currentOrganization.id}:{currentOrganization.membershipId}
    </output>
  )
}

function OrganizationContextHarness() {
  const [organizations, setOrganizations] = useState<
    readonly OrganizationNavigationItem[]
  >([organizationA, organizationB])

  return (
    <AuthContext.Provider value={authContextValue}>
      <OrganizationDiscoveryContext.Provider
        value={{
          state: { status: 'success', organizations },
          refreshOrganizations: () => undefined,
        }}
      >
        <button type="button" onClick={() => setOrganizations([organizationB])}>
          Revoke organization A
        </button>
        <Outlet />
      </OrganizationDiscoveryContext.Provider>
    </AuthContext.Provider>
  )
}

function renderOrganizationRoute() {
  const router = createMemoryRouter(
    [
      {
        path: '/',
        element: <OrganizationContextHarness />,
        children: [
          {
            path: 'organizations/:organizationId',
            element: <OrganizationRoute />,
            children: [{ index: true, element: <CurrentOrganizationProbe /> }],
          },
        ],
      },
    ],
    { initialEntries: [`/organizations/${organizationA.id}`] },
  )

  render(<RouterProvider router={router} />)
  return router
}

afterEach(() => {
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

describe('current organization membership identity', () => {
  it('OrganizationDiscovery_WithMembershipId_ParsesCompleteTransientItem', async () => {
    const localStorageSpy = vi.spyOn(window.localStorage, 'setItem')
    const sessionStorageSpy = vi.spyOn(window.sessionStorage, 'setItem')
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(response([organizationA]))))

    const organizations = await getCurrentUserOrganizations(vi.fn())

    expect(organizations).toEqual([organizationA])
    expect(localStorageSpy).not.toHaveBeenCalled()
    expect(sessionStorageSpy).not.toHaveBeenCalled()
  })

  it('OrganizationRoute_SwitchAndRevocation_BindsAndClearsMembershipIdentity', async () => {
    const router = renderOrganizationRoute()

    expect(await screen.findByTestId('current-organization-identity')).toHaveTextContent(
      `${organizationA.id}:${organizationA.membershipId}`,
    )

    fireEvent.change(screen.getByLabelText(/atual$/i), {
      target: { value: organizationB.id },
    })

    await waitFor(() => {
      expect(screen.getByTestId('current-organization-identity')).toHaveTextContent(
        `${organizationB.id}:${organizationB.membershipId}`,
      )
    })
    expect(screen.getByTestId('current-organization-identity')).not.toHaveTextContent(
      organizationA.membershipId,
    )

    await router.navigate(`/organizations/${organizationA.id}`)
    await waitFor(() => {
      expect(screen.getByTestId('current-organization-identity')).toHaveTextContent(
        organizationA.membershipId,
      )
    })
    fireEvent.click(screen.getByRole('button', { name: 'Revoke organization A' }))

    expect(
      await screen.findByRole('heading', { name: /indispon/i }),
    ).toBeInTheDocument()
    expect(screen.queryByTestId('current-organization-identity')).not.toBeInTheDocument()
  })
})
