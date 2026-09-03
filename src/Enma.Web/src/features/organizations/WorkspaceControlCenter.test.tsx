import { fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it, vi } from 'vitest'
import { WorkspaceControlCenter } from './WorkspaceControlCenter'
import type { OrganizationNavigationItem } from './organizationTypes'

vi.mock('../notifications/NotificationCenter', () => ({
  NotificationCenter: ({ visible }: { visible?: boolean }) =>
    visible ? <div>Notification content</div> : null,
}))

vi.mock('../authentication/AuthenticatedLogout', () => ({
  AuthenticatedLogout: () => <button type="button">Sair</button>,
}))

const organizationA: OrganizationNavigationItem = {
  id: '11111111-1111-4111-8111-111111111111',
  name: 'Escritório Alpha',
  role: 'Owner',
  membershipId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa1',
}

const organizationB: OrganizationNavigationItem = {
  id: '22222222-2222-4222-8222-222222222222',
  name: 'Escritório Beta',
  role: 'Administrator',
  membershipId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbb2',
}

function renderControl(onSelectOrganization = vi.fn()) {
  render(
    <MemoryRouter>
      <WorkspaceControlCenter
        currentOrganization={organizationA}
        organizations={[organizationA, organizationB]}
        onSelectOrganization={onSelectOrganization}
      />
    </MemoryRouter>,
  )

  return onSelectOrganization
}

describe('WorkspaceControlCenter', () => {
  it('opens Perfil with organization and session controls', () => {
    renderControl()

    fireEvent.click(screen.getByRole('tab', { name: 'Perfil' }))

    expect(
      screen.getByRole('button', {
        name: `${organizationA.name}, organização atual`,
      }),
    ).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Sair' })).toBeInTheDocument()
  })

  it('switches organization from Perfil', () => {
    const onSelectOrganization = renderControl()

    fireEvent.click(screen.getByRole('tab', { name: 'Perfil' }))
    fireEvent.click(
      screen.getByRole('button', { name: `Trocar para ${organizationB.name}` }),
    )

    expect(onSelectOrganization).toHaveBeenCalledWith(organizationB.id)
  })

  it('opens Notifications content from the tab', () => {
    renderControl()

    fireEvent.click(screen.getByRole('tab', { name: /Notificações/ }))

    expect(screen.getByText('Notification content')).toBeInTheDocument()
  })
})
