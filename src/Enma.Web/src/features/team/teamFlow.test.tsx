import {
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from '@testing-library/react'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import {
  AuthContext,
  type AuthContextValue,
} from '../authentication/AuthContext'
import { clearCsrfToken } from '../authentication/csrfClient'
import {
  CurrentOrganizationContext,
  OrganizationDiscoveryContext,
} from '../organizations/OrganizationContext'
import type { OrganizationNavigationItem } from '../organizations/organizationTypes'
import { TeamPage } from './TeamPage'

const ownerOrganization: OrganizationNavigationItem = {
  id: '11111111-1111-4111-8111-111111111111',
  membershipId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa1',
  name: 'Organização Alfa',
  role: 'Owner',
}

const administratorOrganization: OrganizationNavigationItem = {
  ...ownerOrganization,
  role: 'Administrator',
}

const memberOrganization: OrganizationNavigationItem = {
  ...ownerOrganization,
  role: 'Member',
}

const ownerMember = {
  id: ownerOrganization.membershipId,
  name: 'Olívia Proprietária',
  role: 'Owner',
  email: 'olivia@example.test',
  membershipStatus: 'Active',
  accountStatus: 'Active',
}

const administratorMember = {
  id: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbb2',
  name: 'André Administrador',
  role: 'Administrator',
  email: 'andre@example.test',
  membershipStatus: 'Active',
  accountStatus: 'Active',
}

const regularMember = {
  id: 'cccccccc-cccc-4ccc-8ccc-ccccccccccc3',
  name: 'Marina Membro',
  role: 'Member',
  email: 'marina@example.test',
  membershipStatus: 'Active',
  accountStatus: 'Active',
}

const authContextValue: AuthContextValue = {
  state: 'authenticated',
  login: async () => 'failure',
  logout: async () => undefined,
  retrySessionCheck: () => undefined,
  handleUnauthorized: vi.fn(),
}

function response(status: number, body?: unknown): Response {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers:
      body === undefined ? undefined : { 'Content-Type': 'application/json' },
  })
}

function teamResponse(
  items: readonly object[],
  pageNumber = 1,
  totalCount = items.length,
): Response {
  return response(200, {
    items,
    pageNumber,
    pageSize: 20,
    totalCount,
  })
}

interface RenderTeamOptions {
  readonly organization?: OrganizationNavigationItem
  readonly initialEntry?: string
  readonly refreshOrganizations?: ReturnType<typeof vi.fn<() => void>>
}

function renderTeam({
  organization = ownerOrganization,
  initialEntry = `/organizations/${organization.id}/team`,
  refreshOrganizations = vi.fn<() => void>(),
}: RenderTeamOptions = {}) {
  const router = createMemoryRouter(
    [
      {
        path: '/organizations/:organizationId/team',
        element: (
          <AuthContext.Provider value={authContextValue}>
            <OrganizationDiscoveryContext.Provider
              value={{
                state: { status: 'success', organizations: [organization] },
                refreshOrganizations,
              }}
            >
              <CurrentOrganizationContext.Provider
                value={{
                  currentOrganization: organization,
                  organizations: [organization],
                }}
              >
                <TeamPage />
              </CurrentOrganizationContext.Provider>
            </OrganizationDiscoveryContext.Provider>
          </AuthContext.Provider>
        ),
      },
    ],
    { initialEntries: [initialEntry] },
  )

  render(<RouterProvider router={router} />)
  return { router, refreshOrganizations }
}

beforeEach(() => {
  clearCsrfToken()
})

afterEach(() => {
  clearCsrfToken()
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

describe('team administration flow', () => {
  it('MemberProjection_ShowsOnlyBasicActiveTeamInformation', async () => {
    const fetchMock = vi.fn(() =>
      Promise.resolve(
        teamResponse([
          {
            id: regularMember.id,
            name: regularMember.name,
            role: regularMember.role,
          },
        ]),
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    renderTeam({ organization: memberOrganization })

    expect(await screen.findByText(regularMember.name)).toBeInTheDocument()
    expect(screen.getByText('Membro')).toBeInTheDocument()
    expect(screen.queryByText(regularMember.email)).not.toBeInTheDocument()
    expect(screen.queryByLabelText('Participação')).not.toBeInTheDocument()
    expect(
      screen.queryByRole('button', { name: 'Editar nome da organização' }),
    ).not.toBeInTheDocument()
    expect(screen.queryByText('Sem ações disponíveis')).not.toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledWith(
      `/api/organizations/${ownerOrganization.id}/members?status=active&pageNumber=1&pageSize=20`,
      expect.objectContaining({ method: 'GET', cache: 'no-store' }),
    )
  })

  it('MemberProjection_RejectsUnexpectedPrivilegedFieldsWithoutRenderingThem', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.resolve(teamResponse([regularMember]))),
    )

    renderTeam({ organization: memberOrganization })

    expect(await screen.findByText(/não foi possível carregar a equipe/i)).toBeInTheDocument()
    expect(screen.queryByText(regularMember.email)).not.toBeInTheDocument()
  })

  it('AdministratorProjection_CanManageOnlyMemberLifecycle', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() =>
        Promise.resolve(
          teamResponse([ownerMember, administratorMember, regularMember]),
        ),
      ),
    )

    renderTeam({ organization: administratorOrganization })

    const memberRow = (await screen.findByText(regularMember.name)).closest('tr')
    const administratorRow = screen.getByText(administratorMember.name).closest('tr')
    const ownerRow = screen.getByText(ownerMember.name).closest('tr')

    expect(memberRow).not.toBeNull()
    expect(administratorRow).not.toBeNull()
    expect(ownerRow).not.toBeNull()
    expect(within(memberRow!).getByRole('button', { name: 'Desativar' })).toBeInTheDocument()
    expect(within(memberRow!).queryByRole('button', { name: 'Alterar papel' })).not.toBeInTheDocument()
    expect(within(administratorRow!).queryByRole('button')).not.toBeInTheDocument()
    expect(within(ownerRow!).queryByRole('button')).not.toBeInTheDocument()
    expect(screen.getByLabelText('Participação')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Editar nome da organização' })).not.toBeInTheDocument()
  })

  it('OwnerProjection_ShowsRoleLifecycleAndRenameControlsExceptForOwner', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() =>
        Promise.resolve(
          teamResponse([ownerMember, administratorMember, regularMember]),
        ),
      ),
    )

    renderTeam()

    const memberRow = (await screen.findByText(regularMember.name)).closest('tr')
    const administratorRow = screen.getByText(administratorMember.name).closest('tr')
    const ownerRow = screen.getByText(ownerMember.name).closest('tr')

    expect(within(memberRow!).getByRole('button', { name: 'Alterar papel' })).toBeInTheDocument()
    expect(within(memberRow!).getByRole('button', { name: 'Desativar' })).toBeInTheDocument()
    expect(within(administratorRow!).getByRole('button', { name: 'Alterar papel' })).toBeInTheDocument()
    expect(within(administratorRow!).getByRole('button', { name: 'Desativar' })).toBeInTheDocument()
    expect(within(ownerRow!).queryByRole('button')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Editar nome da organização' })).toBeInTheDocument()
    expect(
      screen.queryByRole('navigation', { name: 'Paginação da equipe' }),
    ).not.toBeInTheDocument()
  })

  it('LoadingAndEmptyStates_PreserveControlsAndExplainSearchResult', async () => {
    let resolveList: ((value: Response) => void) | undefined
    const pendingList = new Promise<Response>((resolve) => {
      resolveList = resolve
    })
    vi.stubGlobal('fetch', vi.fn(() => pendingList))

    renderTeam({
      initialEntry: `/organizations/${ownerOrganization.id}/team?search=ausente`,
    })

    expect(screen.getByText('Carregando equipe…')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Buscar' })).toBeInTheDocument()

    resolveList?.(teamResponse([]))

    expect(
      await screen.findByText('Nenhum integrante encontrado para esta busca.'),
    ).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Limpar busca' })).toBeInTheDocument()
  })

  it('ListFailure_ShowsSafeRetryAndRecovers', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(response(500, { detail: 'private database detail' }))
      .mockResolvedValueOnce(teamResponse([regularMember]))
    vi.stubGlobal('fetch', fetchMock)

    renderTeam()

    expect(
      await screen.findByText('Não foi possível carregar a equipe. Tente novamente.'),
    ).toBeInTheDocument()
    expect(screen.queryByText('private database detail')).not.toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'Tentar novamente' }))

    expect(await screen.findByText(regularMember.name)).toBeInTheDocument()
  })

  it('Search_SubmitsServerQueryAndResetsPage', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(teamResponse([regularMember], 2, 25))
      .mockResolvedValueOnce(teamResponse([regularMember], 1, 1))
    vi.stubGlobal('fetch', fetchMock)
    const { router } = renderTeam({
      initialEntry: `/organizations/${ownerOrganization.id}/team?page=2`,
    })

    await screen.findByText(regularMember.name)
    fireEvent.change(screen.getByLabelText('Buscar por nome ou e-mail'), {
      target: { value: '  Marina  ' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Buscar' }))

    await waitFor(() => {
      expect(router.state.location.search).toBe('?search=Marina')
    })
    expect(fetchMock).toHaveBeenLastCalledWith(
      `/api/organizations/${ownerOrganization.id}/members?status=active&pageNumber=1&pageSize=20&search=Marina`,
      expect.objectContaining({ method: 'GET' }),
    )
  })

  it('StatusFilter_UsesInactiveServerProjectionAndResetsPage', async () => {
    const inactiveMember = {
      ...regularMember,
      membershipStatus: 'Inactive',
    }
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(teamResponse([regularMember], 2, 21))
      .mockResolvedValueOnce(teamResponse([inactiveMember]))
    vi.stubGlobal('fetch', fetchMock)
    const { router } = renderTeam({
      initialEntry: `/organizations/${ownerOrganization.id}/team?page=2`,
    })

    await screen.findByText(regularMember.name)
    fireEvent.change(screen.getByLabelText('Participação'), {
      target: { value: 'inactive' },
    })

    await waitFor(() => {
      expect(router.state.location.search).toBe('?status=inactive')
    })
    expect(fetchMock).toHaveBeenLastCalledWith(
      `/api/organizations/${ownerOrganization.id}/members?status=inactive&pageNumber=1&pageSize=20`,
      expect.objectContaining({ method: 'GET' }),
    )
  })

  it('Pagination_PreservesSearchAndStatusWhileUsingServerMetadata', async () => {
    const inactiveMember = {
      ...regularMember,
      membershipStatus: 'Inactive',
    }
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(teamResponse([inactiveMember], 1, 45))
      .mockResolvedValueOnce(teamResponse([inactiveMember], 2, 45))
    vi.stubGlobal('fetch', fetchMock)
    const { router } = renderTeam({
      initialEntry: `/organizations/${ownerOrganization.id}/team?status=inactive&search=Marina`,
    })

    expect(await screen.findByText('Página 1 de 3')).toBeInTheDocument()
    expect(
      screen.getByRole('navigation', { name: 'Paginação da equipe' }),
    ).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Próxima página' }))

    await waitFor(() => {
      expect(router.state.location.search).toBe(
        '?status=inactive&search=Marina&page=2',
      )
    })
    expect(fetchMock).toHaveBeenLastCalledWith(
      `/api/organizations/${ownerOrganization.id}/members?status=inactive&pageNumber=2&pageSize=20&search=Marina`,
      expect.objectContaining({ method: 'GET' }),
    )
  })

  it('Pagination_BeyondLastPageReturnsToTheLastAuthoritativePage', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(teamResponse([], 4, 21))
      .mockResolvedValueOnce(teamResponse([regularMember], 2, 21))
    vi.stubGlobal('fetch', fetchMock)
    const { router } = renderTeam({
      initialEntry: `/organizations/${ownerOrganization.id}/team?page=4`,
    })

    await waitFor(() => {
      expect(router.state.location.search).toBe('?page=2')
    })
    expect(await screen.findByText('Página 2 de 2')).toBeInTheDocument()
    expect(fetchMock).toHaveBeenLastCalledWith(
      `/api/organizations/${ownerOrganization.id}/members?status=active&pageNumber=2&pageSize=20`,
      expect.objectContaining({ method: 'GET' }),
    )
  })

  it('RoleChange_ConfirmsExactMutationAndRefreshesTeamAfterSuccess', async () => {
    const promotedMember = { ...regularMember, role: 'Administrator' }
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(teamResponse([ownerMember, regularMember]))
      .mockResolvedValueOnce(response(200, { requestToken: 'csrf-token' }))
      .mockResolvedValueOnce(response(204))
      .mockResolvedValueOnce(teamResponse([ownerMember, promotedMember]))
    vi.stubGlobal('fetch', fetchMock)

    renderTeam()

    fireEvent.click(
      await screen.findByRole('button', { name: 'Alterar papel' }),
    )
    expect(
      screen.getByText(/alterar o papel de Marina Membro para Administrador/i),
    ).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Confirmar' }))

    expect(
      await screen.findByText('Papel de Marina Membro atualizado.'),
    ).toBeInTheDocument()
    expect(fetchMock).toHaveBeenNthCalledWith(
      3,
      `/api/organizations/${ownerOrganization.id}/members/${regularMember.id}/role`,
      expect.objectContaining({
        method: 'PUT',
        body: JSON.stringify({
          role: 'Administrator',
          expectedCurrentRole: 'Member',
        }),
      }),
    )
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(4))
  })

  it('Deactivate_UsesDeliberateKeyboardConfirmationAndRefreshesAfterSuccess', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(teamResponse([ownerMember, regularMember]))
      .mockResolvedValueOnce(response(200, { requestToken: 'csrf-token' }))
      .mockResolvedValueOnce(response(204))
      .mockResolvedValueOnce(teamResponse([ownerMember], 1, 1))
    vi.stubGlobal('fetch', fetchMock)

    renderTeam()

    const trigger = await screen.findByRole('button', { name: 'Desativar' })
    fireEvent.click(trigger)
    const dialog = screen.getByRole('alertdialog')
    expect(dialog).toHaveTextContent('O acesso à organização será removido.')
    const cancelButton = screen.getByRole('button', { name: 'Cancelar' })
    const confirmButton = screen.getByRole('button', {
      name: 'Confirmar desativação',
    })
    expect(cancelButton).toHaveFocus()

    fireEvent.keyDown(dialog, { key: 'Tab', shiftKey: true })
    expect(confirmButton).toHaveFocus()
    fireEvent.keyDown(dialog, { key: 'Tab' })
    expect(cancelButton).toHaveFocus()

    fireEvent.keyDown(dialog, { key: 'Escape' })
    expect(screen.getByRole('button', { name: 'Desativar' })).toHaveFocus()

    fireEvent.click(screen.getByRole('button', { name: 'Desativar' }))
    fireEvent.click(
      screen.getByRole('button', { name: 'Confirmar desativação' }),
    )

    expect(
      await screen.findByText(
        'A participação de Marina Membro foi desativada.',
      ),
    ).toBeInTheDocument()
    expect(fetchMock).toHaveBeenNthCalledWith(
      3,
      `/api/organizations/${ownerOrganization.id}/members/${regularMember.id}/deactivate`,
      expect.objectContaining({ method: 'POST', body: undefined }),
    )
  })

  it('RowConfirmations_KeepOnlyTheLatestAdministrativeDecisionOpen', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() =>
        Promise.resolve(
          teamResponse([ownerMember, administratorMember, regularMember]),
        ),
      ),
    )

    renderTeam()

    const deactivateButtons = await screen.findAllByRole('button', {
      name: 'Desativar',
    })
    fireEvent.click(deactivateButtons[0])
    expect(screen.getByRole('alertdialog')).toHaveTextContent(
      administratorMember.name,
    )

    fireEvent.click(screen.getByRole('button', { name: 'Desativar' }))

    expect(screen.getAllByRole('alertdialog')).toHaveLength(1)
    expect(screen.getByRole('alertdialog')).toHaveTextContent(regularMember.name)
  })

  it('Reactivate_UsesServerMutationWithoutAConfirmationDialog', async () => {
    const inactiveMember = {
      ...regularMember,
      membershipStatus: 'Inactive',
    }
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(teamResponse([inactiveMember]))
      .mockResolvedValueOnce(response(200, { requestToken: 'csrf-token' }))
      .mockResolvedValueOnce(response(204))
      .mockResolvedValueOnce(teamResponse([]))
    vi.stubGlobal('fetch', fetchMock)

    renderTeam({
      initialEntry: `/organizations/${ownerOrganization.id}/team?status=inactive`,
    })

    fireEvent.click(await screen.findByRole('button', { name: 'Reativar' }))

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(
      await screen.findByText(
        'A participação de Marina Membro foi reativada.',
      ),
    ).toBeInTheDocument()
    expect(fetchMock).toHaveBeenNthCalledWith(
      3,
      `/api/organizations/${ownerOrganization.id}/members/${regularMember.id}/reactivate`,
      expect.objectContaining({ method: 'POST' }),
    )
  })

  it('Rename_OwnerUpdatesOnlyNameAndRefreshesOrganizationDiscovery', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(teamResponse([ownerMember]))
      .mockResolvedValueOnce(response(200, { requestToken: 'csrf-token' }))
      .mockResolvedValueOnce(response(204))
    vi.stubGlobal('fetch', fetchMock)
    const refreshOrganizations = vi.fn<() => void>()

    renderTeam({ refreshOrganizations })

    fireEvent.click(
      await screen.findByRole('button', {
        name: 'Editar nome da organização',
      }),
    )
    fireEvent.change(screen.getByLabelText('Nome da organização'), {
      target: { value: 'Nova Organização' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Salvar' }))

    expect(
      await screen.findByText('Nome da organização atualizado.'),
    ).toBeInTheDocument()
    expect(refreshOrganizations).toHaveBeenCalledOnce()
    expect(fetchMock).toHaveBeenNthCalledWith(
      3,
      `/api/organizations/${ownerOrganization.id}`,
      expect.objectContaining({
        method: 'PUT',
        body: JSON.stringify({ name: 'Nova Organização' }),
      }),
    )
    expect(
      screen.queryByLabelText(/slug/i),
    ).not.toBeInTheDocument()
  })

  it('DeactivateConflict_ExplainsActiveWorkWithoutExposingProblemDetails', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(teamResponse([ownerMember, regularMember]))
      .mockResolvedValueOnce(response(200, { requestToken: 'csrf-token' }))
      .mockResolvedValueOnce(
        response(409, { detail: 'private active assignment implementation' }),
      )
      .mockResolvedValueOnce(teamResponse([ownerMember, regularMember]))
    vi.stubGlobal('fetch', fetchMock)

    renderTeam()

    fireEvent.click(await screen.findByRole('button', { name: 'Desativar' }))
    fireEvent.click(
      screen.getByRole('button', { name: 'Confirmar desativação' }),
    )

    expect(
      await screen.findByText(/reatribua o trabalho antes de desativá-lo/i),
    ).toBeInTheDocument()
    expect(
      screen.queryByText('private active assignment implementation'),
    ).not.toBeInTheDocument()
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(4))
  })

  it('ReactivateConflict_ExplainsThatTheUserAccountCannotBeChangedHere', async () => {
    const inactiveMember = {
      ...regularMember,
      membershipStatus: 'Inactive',
    }
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(teamResponse([inactiveMember]))
      .mockResolvedValueOnce(response(200, { requestToken: 'csrf-token' }))
      .mockResolvedValueOnce(response(409, { detail: 'private account detail' }))
      .mockResolvedValueOnce(teamResponse([inactiveMember]))
    vi.stubGlobal('fetch', fetchMock)

    renderTeam({
      initialEntry: `/organizations/${ownerOrganization.id}/team?status=inactive`,
    })

    fireEvent.click(await screen.findByRole('button', { name: 'Reativar' }))

    expect(
      await screen.findByText(/reativada fora desta tela/i),
    ).toBeInTheDocument()
    expect(screen.queryByText('private account detail')).not.toBeInTheDocument()
  })

  it('StaleAuthorization_FailsClosedRefreshesStateAndHidesFurtherActions', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(teamResponse([ownerMember, regularMember]))
      .mockResolvedValueOnce(response(200, { requestToken: 'csrf-token' }))
      .mockResolvedValueOnce(response(403, { detail: 'private policy detail' }))
      .mockResolvedValueOnce(teamResponse([ownerMember, regularMember]))
    vi.stubGlobal('fetch', fetchMock)
    const refreshOrganizations = vi.fn<() => void>()

    renderTeam({ refreshOrganizations })

    fireEvent.click(
      await screen.findByRole('button', { name: 'Alterar papel' }),
    )
    fireEvent.click(screen.getByRole('button', { name: 'Confirmar' }))

    expect(
      await screen.findByText(/seu acesso mudou e esta ação não foi concluída/i),
    ).toBeInTheDocument()
    expect(refreshOrganizations).toHaveBeenCalledOnce()
    expect(screen.queryByText('private policy detail')).not.toBeInTheDocument()
    await waitFor(() => {
      expect(
        screen.queryByRole('button', { name: 'Alterar papel' }),
      ).not.toBeInTheDocument()
      expect(
        screen.queryByRole('button', { name: 'Desativar' }),
      ).not.toBeInTheDocument()
    })
  })
})
