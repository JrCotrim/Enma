import { NavLink, Outlet, useNavigate } from 'react-router-dom'
import { AuthenticatedLogout } from '../authentication/AuthenticatedLogout'
import {
  useCurrentOrganization,
  useOrganizationDiscovery,
} from './OrganizationContext'
import { getOrganizationRoleLabel } from './organizationTypes'

export function OrganizationWorkspace() {
  const { currentOrganization, organizations } = useCurrentOrganization()
  const { refreshOrganizations } = useOrganizationDiscovery()
  const navigate = useNavigate()

  return (
    <section className="organization-workspace" aria-labelledby="workspace-title">
      <div className="workspace-toolbar">
        <div className="organization-switcher">
          <label htmlFor="organization-switcher">Organização atual</label>
          <select
            id="organization-switcher"
            value={currentOrganization.id}
            onChange={(event) => {
              navigate(`/organizations/${event.target.value}`)
            }}
          >
            {organizations.map((organization) => (
              <option key={organization.id} value={organization.id}>
                {organization.name}
              </option>
            ))}
          </select>
        </div>

        <div className="workspace-actions">
          <button
            className="secondary-button"
            type="button"
            onClick={refreshOrganizations}
          >
            Atualizar organizações
          </button>
          <AuthenticatedLogout />
        </div>
      </div>

      <div className="workspace-context">
        <p className="eyebrow">Espaço de trabalho</p>
        <h1 id="workspace-title">{currentOrganization.name}</h1>
        <p className="organization-role">
          Seu papel: {getOrganizationRoleLabel(currentOrganization.role)}
        </p>
      </div>

      <nav className="workspace-navigation" aria-label="Navegação da organização">
        <NavLink
          className={({ isActive }) =>
            `workspace-navigation-link${isActive ? ' is-active' : ''}`
          }
          to="clients"
        >
          Clientes
        </NavLink>
        <NavLink
          className={({ isActive }) =>
            `workspace-navigation-link${isActive ? ' is-active' : ''}`
          }
          to="processes"
        >
          Processos
        </NavLink>
        <NavLink
          className={({ isActive }) =>
            `workspace-navigation-link${isActive ? ' is-active' : ''}`
          }
          to="deadlines"
        >
          Prazos
        </NavLink>
        <NavLink
          className={({ isActive }) =>
            `workspace-navigation-link${isActive ? ' is-active' : ''}`
          }
          to="tasks"
        >
          Tarefas
        </NavLink>
        <NavLink
          className={({ isActive }) =>
            `workspace-navigation-link${isActive ? ' is-active' : ''}`
          }
          to="documents"
        >
          Documentos
        </NavLink>
      </nav>

      <Outlet key={currentOrganization.id} />
    </section>
  )
}
