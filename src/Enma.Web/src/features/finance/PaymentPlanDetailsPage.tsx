import { Link } from 'react-router-dom'
import { useCurrentOrganization } from '../organizations/OrganizationContext'

export function PaymentPlanDetailsPage() {
  const { currentOrganization } = useCurrentOrganization()
  const canAccess = currentOrganization.role !== 'Member'
  const financePath = `/organizations/${currentOrganization.id}/finance`

  return (
    <section className="finance-page" aria-labelledby="payment-plan-title">
      {!canAccess ? (
        <div role="alert">
          <h3>Acesso negado</h3>
          <p>Somente proprietários e administradores podem acessar o Financeiro.</p>
          <Link className="home-link" to={`/organizations/${currentOrganization.id}`}>
            Voltar para a visão geral
          </Link>
        </div>
      ) : (
        <>
          <Link className="home-link" to={financePath}>
            Voltar para Financeiro
          </Link>
          <header className="workspace-page-header">
            <div className="workspace-page-heading">
              <h2 className="workspace-page-title" id="payment-plan-title">
                Plano de pagamento
              </h2>
              <p className="workspace-page-subtitle">
                Consulte os dados deste plano de pagamento.
              </p>
            </div>
          </header>
        </>
      )}
    </section>
  )
}
