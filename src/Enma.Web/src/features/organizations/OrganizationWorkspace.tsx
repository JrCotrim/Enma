import { NavLink, Outlet, useNavigate } from 'react-router-dom'
import { useCurrentOrganization } from './OrganizationContext'
import { WorkspaceControlCenter } from './WorkspaceControlCenter'

export function OrganizationWorkspace() {
  const { currentOrganization, organizations } = useCurrentOrganization()
  const navigate = useNavigate()

  return (
    <section className="organization-workspace" aria-labelledby="workspace-title">
      <h1 id="workspace-title" className="workspace-title-sr">
        Espaço de trabalho: {currentOrganization.name}
      </h1>

      <div className="workspace-toolbar">
        <WorkspaceControlCenter
          key={currentOrganization.id}
          currentOrganization={currentOrganization}
          organizations={organizations}
          onSelectOrganization={(organizationId) => {
            navigate(`/organizations/${organizationId}`)
          }}
        />
      </div>

      <nav className="workspace-navigation" aria-label="Navegação da organização">
        <NavLink
          className={({ isActive }) =>
            `workspace-navigation-link${isActive ? ' is-active' : ''}`
          }
          to="."
          end
        >
          Visão geral
        </NavLink>
        <NavLink
          className={({ isActive }) =>
            `workspace-navigation-link${isActive ? ' is-active' : ''}`
          }
          to="agenda"
        >
          Agenda
        </NavLink>
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
        <NavLink
          className={({ isActive }) =>
            `workspace-navigation-link${isActive ? ' is-active' : ''}`
          }
          to="team"
        >
          Equipe
        </NavLink>
        {currentOrganization.role !== 'Member' ? (
          <>
            <NavLink
              className={({ isActive }) =>
                `workspace-navigation-link${isActive ? ' is-active' : ''}`
              }
              to="invitations"
            >
              Convites
            </NavLink>
            <NavLink
              className={({ isActive }) =>
                `workspace-navigation-link${isActive ? ' is-active' : ''}`
              }
              to="audit-log"
            >
              Auditoria
            </NavLink>
          </>
        ) : null}
      </nav>

      <Outlet key={currentOrganization.id} />
    </section>
  )
}
