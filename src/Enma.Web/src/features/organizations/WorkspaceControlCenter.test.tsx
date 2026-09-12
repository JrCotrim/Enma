import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it, vi } from 'vitest'
import { WorkspaceControlCenter } from './WorkspaceControlCenter'
import type { OrganizationNavigationItem } from './organizationTypes'

vi.mock('../notifications/NotificationCenter', () => ({
  NotificationCenter: ({
    visible,
    panelId,
    panelLabelledBy,
  }: {
    visible?: boolean
    panelId?: string
    panelLabelledBy?: string
  }) =>
    visible ? (
      <div id={panelId} aria-labelledby={panelLabelledBy}>
        Notification content
      </div>
    ) : null,
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
  it('exposes Perfil and Notifications as disclosure buttons', () => {
    renderControl()

    const profile = screen.getByRole('button', { name: 'Perfil' })
    const notifications = screen.getByRole('button', {
      name: 'Notificações',
    })

    expect(screen.queryByRole('tablist')).not.toBeInTheDocument()
    expect(screen.queryByRole('tab')).not.toBeInTheDocument()
    expect(profile).toHaveAttribute('aria-expanded', 'false')
    expect(notifications).toHaveAttribute('aria-expanded', 'false')
    expect(profile).not.toHaveAttribute('aria-selected')
    expect(notifications).not.toHaveAttribute('aria-selected')
    expect(profile).toHaveAttribute('aria-controls')
    expect(notifications).toHaveAttribute('aria-controls')

    profile.focus()
    expect(profile).toHaveFocus()
    notifications.focus()
    expect(notifications).toHaveFocus()
  })

  it('opens Perfil with a labelled panel and toggles it closed', async () => {
    renderControl()

    const profile = screen.getByRole('button', { name: 'Perfil' })
    fireEvent.click(profile, { detail: 0 })

    expect(
      screen.getByRole('button', {
        name: `${organizationA.name}, organização atual`,
      }),
    ).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Sair' })).toBeInTheDocument()
    const panel = document.getElementById(profile.getAttribute('aria-controls')!)
    expect(panel).toHaveAttribute('aria-labelledby', profile.id)
    expect(profile).toHaveAttribute('aria-expanded', 'true')

    fireEvent.click(profile, { detail: 0 })

    expect(profile).toHaveAttribute('aria-expanded', 'false')
    await waitFor(() => expect(panel).not.toBeInTheDocument())
  })

  it('switches organization from Perfil', () => {
    const onSelectOrganization = renderControl()

    fireEvent.click(screen.getByRole('button', { name: 'Perfil' }))
    fireEvent.click(
      screen.getByRole('button', { name: `Trocar para ${organizationB.name}` }),
    )

    expect(onSelectOrganization).toHaveBeenCalledWith(organizationB.id)
  })

  it('opens Notifications with the matching control relation', () => {
    renderControl()

    const notifications = screen.getByRole('button', {
      name: /Notificações/,
    })
    fireEvent.click(notifications, { detail: 0 })

    const panel = screen.getByText('Notification content')
    expect(panel).toHaveAttribute('id', notifications.getAttribute('aria-controls'))
    expect(panel).toHaveAttribute('aria-labelledby', notifications.id)
    expect(notifications).toHaveAttribute('aria-expanded', 'true')
    expect(screen.getByRole('button', { name: 'Perfil' })).toHaveAttribute(
      'aria-expanded',
      'false',
    )
  })

  it('Escape closes the active panel and restores its trigger focus', () => {
    renderControl()

    const notifications = screen.getByRole('button', {
      name: /Notificações/,
    })
    fireEvent.click(notifications)
    fireEvent.keyDown(document, { key: 'Escape' })

    expect(screen.queryByText('Notification content')).not.toBeInTheDocument()
    expect(notifications).toHaveAttribute('aria-expanded', 'false')
    expect(notifications).toHaveFocus()
  })
})
