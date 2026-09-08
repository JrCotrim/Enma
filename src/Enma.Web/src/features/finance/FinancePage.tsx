import { Link } from 'react-router-dom'
import { useCurrentOrganization } from '../organizations/OrganizationContext'

export function FinancePage() {
  const { currentOrganization } = useCurrentOrganization()
  const canAccess = currentOrganization.role !== 'Member'

  return (
    <section className="finance-page" aria-labelledby="finance-title">
      <header className="workspace-page-header">
        <div className="workspace-page-heading">
          <h2 className="workspace-page-title" id="finance-title">
            Financeiro
          </h2>
          <p className="workspace-page-subtitle">
            Acompanhe os planos de pagamento desta organização.
          </p>
        </div>
      </header>

      {!canAccess ? (
        <div role="alert">
          <h3>Acesso negado</h3>
          <p>Somente proprietários e administradores podem acessar o Financeiro.</p>
          <Link className="home-link" to={`/organizations/${currentOrganization.id}`}>
            Voltar para a visão geral
          </Link>
        </div>
      ) : null}
    </section>
  )
}
