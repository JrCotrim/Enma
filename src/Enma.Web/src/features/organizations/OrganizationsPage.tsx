import { Link } from 'react-router-dom'
import { AuthenticatedLogout } from '../authentication/AuthenticatedLogout'
import { useOrganizationDiscovery } from './OrganizationContext'
import {
  OrganizationDiscoveryError,
  OrganizationLoading,
} from './OrganizationStates'
import { getOrganizationRoleLabel } from './organizationTypes'

export function OrganizationsPage() {
  const { state, refreshOrganizations } = useOrganizationDiscovery()

  if (state.status === 'loading') {
    return <OrganizationLoading />
  }

  if (state.status === 'error') {
    return <OrganizationDiscoveryError />
  }

  return (
    <section className="organization-directory" aria-labelledby="organizations-title">
      <div className="organization-directory-header">
        <div>
          <p className="eyebrow">Espaços de trabalho</p>
          <h1 id="organizations-title">Suas organizações</h1>
          <p className="page-copy">
            Escolha explicitamente a organização que deseja acessar.
          </p>
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

      {state.organizations.length === 0 ? (
        <div className="organization-empty" role="status">
          <h2>Nenhuma organização disponível</h2>
          <p>Sua conta não possui organizações disponíveis no momento.</p>
        </div>
      ) : (
        <ul className="organization-list" aria-label="Organizações disponíveis">
          {state.organizations.map((organization) => (
            <li className="organization-card" key={organization.id}>
              <div>
                <h2>{organization.name}</h2>
                <p>{getOrganizationRoleLabel(organization.role)}</p>
              </div>
              <Link
                className="organization-link"
                to={`/organizations/${organization.id}`}
              >
                Acessar {organization.name}
              </Link>
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}
