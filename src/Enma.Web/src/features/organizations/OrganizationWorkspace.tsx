import { useRef } from 'react'
import { NavLink, Outlet, useNavigate } from 'react-router-dom'
import { BriefcaseBusinessIcon } from '../../components/icons/navigation/BriefcaseBusinessIcon'
import { CalendarDaysIcon } from '../../components/icons/navigation/CalendarDaysIcon'
import { ClipboardCheckIcon } from '../../components/icons/navigation/ClipboardCheckIcon'
import { ClockIcon } from '../../components/icons/navigation/ClockIcon'
import { FileTextIcon } from '../../components/icons/navigation/FileTextIcon'
import { LayoutPanelTopIcon } from '../../components/icons/navigation/LayoutPanelTopIcon'
import { SendIcon } from '../../components/icons/navigation/SendIcon'
import { ShieldCheckIcon } from '../../components/icons/navigation/ShieldCheckIcon'
import { UsersIcon } from '../../components/icons/navigation/UsersIcon'
import { UsersRoundIcon } from '../../components/icons/navigation/UsersRoundIcon'
import { useCurrentOrganization } from './OrganizationContext'
import { WorkspaceControlCenter } from './WorkspaceControlCenter'

interface NavigationIconHandle {
  startAnimation(): void
  stopAnimation(): void
}

export function OrganizationWorkspace() {
  const { currentOrganization, organizations } = useCurrentOrganization()
  const navigate = useNavigate()

  const overviewIconRef = useRef<NavigationIconHandle>(null)
  const agendaIconRef = useRef<NavigationIconHandle>(null)
  const clientsIconRef = useRef<NavigationIconHandle>(null)
  const processesIconRef = useRef<NavigationIconHandle>(null)
  const deadlinesIconRef = useRef<NavigationIconHandle>(null)
  const tasksIconRef = useRef<NavigationIconHandle>(null)
  const documentsIconRef = useRef<NavigationIconHandle>(null)
  const teamIconRef = useRef<NavigationIconHandle>(null)
  const invitationsIconRef = useRef<NavigationIconHandle>(null)
  const auditIconRef = useRef<NavigationIconHandle>(null)

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
          onMouseEnter={() => overviewIconRef.current?.startAnimation()}
          onMouseLeave={() => overviewIconRef.current?.stopAnimation()}
          onFocus={() => overviewIconRef.current?.startAnimation()}
          onBlur={() => overviewIconRef.current?.stopAnimation()}
        >
          <LayoutPanelTopIcon
            ref={overviewIconRef}
            className="workspace-navigation-icon"
            size={17}
          />
          <span>Visão geral</span>
        </NavLink>

        <NavLink
          className={({ isActive }) =>
            `workspace-navigation-link${isActive ? ' is-active' : ''}`
          }
          to="agenda"
          onMouseEnter={() => agendaIconRef.current?.startAnimation()}
          onMouseLeave={() => agendaIconRef.current?.stopAnimation()}
          onFocus={() => agendaIconRef.current?.startAnimation()}
          onBlur={() => agendaIconRef.current?.stopAnimation()}
        >
          <CalendarDaysIcon
            ref={agendaIconRef}
            className="workspace-navigation-icon"
            size={17}
          />
          <span>Agenda</span>
        </NavLink>

        <NavLink
          className={({ isActive }) =>
            `workspace-navigation-link${isActive ? ' is-active' : ''}`
          }
          to="clients"
          onMouseEnter={() => clientsIconRef.current?.startAnimation()}
          onMouseLeave={() => clientsIconRef.current?.stopAnimation()}
          onFocus={() => clientsIconRef.current?.startAnimation()}
          onBlur={() => clientsIconRef.current?.stopAnimation()}
        >
          <UsersIcon
            ref={clientsIconRef}
            className="workspace-navigation-icon"
            size={17}
          />
          <span>Clientes</span>
        </NavLink>

        <NavLink
          className={({ isActive }) =>
            `workspace-navigation-link${isActive ? ' is-active' : ''}`
          }
          to="processes"
          onMouseEnter={() => processesIconRef.current?.startAnimation()}
          onMouseLeave={() => processesIconRef.current?.stopAnimation()}
          onFocus={() => processesIconRef.current?.startAnimation()}
          onBlur={() => processesIconRef.current?.stopAnimation()}
        >
          <BriefcaseBusinessIcon
            ref={processesIconRef}
            className="workspace-navigation-icon"
            size={17}
          />
          <span>Processos</span>
        </NavLink>

        <NavLink
          className={({ isActive }) =>
            `workspace-navigation-link${isActive ? ' is-active' : ''}`
          }
          to="deadlines"
          onMouseEnter={() => deadlinesIconRef.current?.startAnimation()}
          onMouseLeave={() => deadlinesIconRef.current?.stopAnimation()}
          onFocus={() => deadlinesIconRef.current?.startAnimation()}
          onBlur={() => deadlinesIconRef.current?.stopAnimation()}
        >
          <ClockIcon
            ref={deadlinesIconRef}
            className="workspace-navigation-icon"
            size={17}
          />
          <span>Prazos</span>
        </NavLink>

        <NavLink
          className={({ isActive }) =>
            `workspace-navigation-link${isActive ? ' is-active' : ''}`
          }
          to="tasks"
          onMouseEnter={() => tasksIconRef.current?.startAnimation()}
          onMouseLeave={() => tasksIconRef.current?.stopAnimation()}
          onFocus={() => tasksIconRef.current?.startAnimation()}
          onBlur={() => tasksIconRef.current?.stopAnimation()}
        >
          <ClipboardCheckIcon
            ref={tasksIconRef}
            className="workspace-navigation-icon"
            size={17}
          />
          <span>Tarefas</span>
        </NavLink>

        <NavLink
          className={({ isActive }) =>
            `workspace-navigation-link${isActive ? ' is-active' : ''}`
          }
          to="documents"
          onMouseEnter={() => documentsIconRef.current?.startAnimation()}
          onMouseLeave={() => documentsIconRef.current?.stopAnimation()}
          onFocus={() => documentsIconRef.current?.startAnimation()}
          onBlur={() => documentsIconRef.current?.stopAnimation()}
        >
          <FileTextIcon
            ref={documentsIconRef}
            className="workspace-navigation-icon"
            size={17}
          />
          <span>Documentos</span>
        </NavLink>

        <NavLink
          className={({ isActive }) =>
            `workspace-navigation-link${isActive ? ' is-active' : ''}`
          }
          to="team"
          onMouseEnter={() => teamIconRef.current?.startAnimation()}
          onMouseLeave={() => teamIconRef.current?.stopAnimation()}
          onFocus={() => teamIconRef.current?.startAnimation()}
          onBlur={() => teamIconRef.current?.stopAnimation()}
        >
          <UsersRoundIcon
            ref={teamIconRef}
            className="workspace-navigation-icon"
            size={17}
          />
          <span>Equipe</span>
        </NavLink>

        {currentOrganization.role !== 'Member' ? (
          <>
            <NavLink
              className={({ isActive }) =>
                `workspace-navigation-link${isActive ? ' is-active' : ''}`
              }
              to="invitations"
              onMouseEnter={() => invitationsIconRef.current?.startAnimation()}
          onMouseLeave={() => invitationsIconRef.current?.stopAnimation()}
          onFocus={() => invitationsIconRef.current?.startAnimation()}
          onBlur={() => invitationsIconRef.current?.stopAnimation()}
            >
              <SendIcon
                ref={invitationsIconRef}
                className="workspace-navigation-icon"
                size={17}
              />
              <span>Convites</span>
            </NavLink>

            <NavLink
              className={({ isActive }) =>
                `workspace-navigation-link${isActive ? ' is-active' : ''}`
              }
              to="audit-log"
              onMouseEnter={() => auditIconRef.current?.startAnimation()}
          onMouseLeave={() => auditIconRef.current?.stopAnimation()}
          onFocus={() => auditIconRef.current?.startAnimation()}
          onBlur={() => auditIconRef.current?.stopAnimation()}
            >
              <ShieldCheckIcon
                ref={auditIconRef}
                className="workspace-navigation-icon"
                size={17}
              />
              <span>Auditoria</span>
            </NavLink>
          </>
        ) : null}
      </nav>

      <Outlet key={currentOrganization.id} />
    </section>
  )
}
